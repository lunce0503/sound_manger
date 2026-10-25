import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {once} from 'node:events';
import {mkdtemp, mkdir, copyFile, readFile, writeFile} from 'node:fs/promises';
import {resolve, join, dirname} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const {chromium} = await import(process.env.SM_PLAYWRIGHT_MODULE ? pathToFileURL(process.env.SM_PLAYWRIGHT_MODULE).href : 'playwright');
const artifacts = resolve(root, '../artifacts/browser-e2e'); await mkdir(artifacts, {recursive: true});
const run = await mkdtemp(join(artifacts, 'run-')); const extension = join(run, 'extension'); await mkdir(extension);
const files = ['manifest.json','protocol.js','auth-store.js','pcm-batcher.js','pcm-worklet.js','offscreen.html','offscreen.js','service-worker.js','popup.html','popup.js','popup.css'];
for (const name of files) await copyFile(join(root, name), join(extension, name));
// Expose the real file-refresh operation only in this isolated test copy.
await writeFile(join(extension,'service-worker.js'),await readFile(join(extension,'service-worker.js'),'utf8')+'\nglobalThis.checkInstalledUpdate = refreshInstalledFiles;\n');
// Only the isolated test copy pregrants this loopback fixture. Production requests its chosen host at pairing time.
const manifest = JSON.parse(await readFile(join(extension, 'manifest.json'), 'utf8'));
manifest.host_permissions = ['http://127.0.0.1/*']; await writeFile(join(extension, 'manifest.json'), JSON.stringify(manifest));
const host = spawn('dotnet', [resolve(root, '../tools/SoundManager.BrowserCheck/bin/Release/net10.0/SoundManager.BrowserCheck.dll')], {stdio: ['pipe','pipe','pipe']});
const lines = createInterface({input: host.stdout}); const initial = once(lines, 'line');
const failure = once(host, 'exit').then(() => {throw new Error('Browser test host exited unexpectedly');});
failure.catch(() => {});
const [first] = await Promise.race([initial, failure]); const pair = JSON.parse(first);
const code = `soundmanager://pair?v=1&server=${encodeURIComponent(pair.address)}&key=${pair.key}`;
const fixture = createServer((request, response) => {
  const label = request.url === '/a' ? 'Test tab A' : 'Test tab B';
  response.setHeader('Content-Type','text/html; charset=utf-8');
  response.end(`<!doctype html><html><title>${label}</title><button id="tone">Start synthetic audio</button><script>
  document.querySelector('button').onclick=async()=>{let c=new AudioContext(),o=c.createOscillator(),g=c.createGain();o.frequency.value=${request.url === '/a' ? 440 : 660};g.gain.value=0.001;o.connect(g).connect(c.destination);o.start();await c.resume();};
  </script></html>`);
});
await new Promise(resolve => fixture.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${fixture.address().port}`;
let context;
async function launch() {
  context = await chromium.launchPersistentContext(join(run, 'profile'), {channel: 'chromium', headless: true,
    executablePath: process.env.SM_CHROMIUM_EXECUTABLE,
    ignoreDefaultArgs: ['--mute-audio', '--disable-extensions'],
    args: [`--disable-extensions-except=${extension}`,`--load-extension=${extension}`]});
  // Match the documented unpacked installation in this disposable browser profile.
  const setup=await context.newPage();await setup.goto('chrome://extensions');
  const developer=setup.locator('#devMode');await developer.waitFor();
  if(!await developer.evaluate(node=>node.checked))await developer.click();
  await setup.close();
  const worker = context.serviceWorkers()[0] ?? await context.waitForEvent('serviceworker');
  return {worker, id: new URL(worker.url()).host};
}
async function popupFor(page, id) {
  await page.bringToFront();
  const session = await context.newCDPSession(page); const {targetInfo} = await session.send('Target.getTargetInfo');
  const cdp = await context.browser().newBrowserCDPSession();
  const targets = await cdp.send('Target.getTargets', {filter: [{}]});
  const tabTarget = targets.targetInfos.find(target => target.type === 'tab' && target.url === page.url());
  await cdp.send('Extensions.triggerAction', {id, targetId: tabTarget?.targetId ?? targetInfo.targetId});
  const target = await until(async () => (await cdp.send('Target.getTargets', {filter: [{}]})).targetInfos
    .find(value => value.url === `chrome-extension://${id}/popup.html`), 'Extension popup missing');
  // Toolbar popups are "other" targets, not Playwright Page objects.
  const {sessionId} = await cdp.send('Target.attachToTarget', {targetId: target.targetId, flatten: false});
  let sequence = 0; const waiting = new Map();
  cdp.on('Target.receivedMessageFromTarget', event => {
    if (event.sessionId !== sessionId) return;
    const message = JSON.parse(event.message); const pending = waiting.get(message.id);
    if (pending) {waiting.delete(message.id); message.error ? pending.reject(new Error(message.error.message)) : pending.resolve(message.result);}
  });
  const command = (method, params = {}) => new Promise((resolve, reject) => {
    const requestId = ++sequence; waiting.set(requestId, {resolve, reject});
    cdp.send('Target.sendMessageToTarget', {sessionId, message: JSON.stringify({id: requestId, method, params})}).catch(reject);
  });
  const evaluate = async expression => {
    const result = await command('Runtime.evaluate', {expression, returnByValue: true, awaitPromise: true, userGesture: true});
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.text);
    return result.result.value;
  };
  await until(() => evaluate("document.readyState === 'complete'"), 'Popup not loaded');
  return {
    locator(selector) {
      const query = `document.querySelector(${JSON.stringify(selector)})`;
      return {
        evaluate: action => evaluate(`(${action.toString()})(${query})`),
        fill: value => evaluate(`${query}.value=${JSON.stringify(value)}`),
        click: () => evaluate(`${query}.click()`),
        isEnabled: () => evaluate(`!${query}.disabled`),
        selectOption: value => evaluate(`${query}.value=${JSON.stringify(value)};${query}.dispatchEvent(new Event('change'))`)
      };
    },
    async screenshot({path}) { const result = await command('Page.captureScreenshot'); await writeFile(path, Buffer.from(result.data,'base64')); },
    close: () => cdp.send('Target.closeTarget', {targetId: target.targetId})
  };
}
async function state() {
  const response = await fetch(new URL('/v1/state', pair.address), {headers: {'X-Sound-Key': pair.key}}); return response.json();
}
async function until(check, description) {
  for (let attempt=0; attempt<100; attempt++) { const value=await check(); if (value) return value; await new Promise(resolve=>setTimeout(resolve,100)); }
  throw new Error(description);
}
async function patch(id, changes) {
  const response=await fetch(new URL('/v1/sources/'+id,pair.address),{method:'PATCH',headers:{'X-Sound-Key':pair.key,'Content-Type':'application/json'},body:JSON.stringify(changes)});
  assert.equal(response.status,200);
}
async function render() {const reply=once(lines,'line');host.stdin.write('render\n');return JSON.parse((await reply)[0]);}
try {
  const {worker,id} = await launch();
  const a=await context.newPage(); await a.goto(origin+'/a');
  let popup=await popupFor(a,id);
  await popup.locator('#connection').evaluate(node=>node.open=true);
  await popup.locator('#pairing').fill(code); await popup.locator('#pair').click();
  await until(()=>popup.locator('#connection').evaluate(node=>!node.open),'Pairing failed');
  await popup.locator('#start').click();
  await until(async()=> (await state()).sources.length===1,'First tab did not connect');
  await popup.screenshot({path:join(run,'popup.png')}); await popup.close();
  await a.locator('#tone').click();
  const sourceA=await until(async()=> (await state()).sources.find(s=>s.name==='Test tab A'&&s.bufferedMs>=40),'First tab PCM missing');
  await until(async()=> (await state()).sources.find(s=>s.id===sourceA.id)?.droppedFrames>sourceA.droppedFrames,'Capture stopped when popup closed');
  await until(async()=> (await render()).rms>0,'First tab PCM remained silent');
  const profileId=await worker.evaluate(async()=> (await chrome.storage.local.get('profiles')).profiles[0].id);
  const b=await context.newPage();await b.goto(origin+'/b');
  popup=await popupFor(b,id);await popup.locator('#start').click();
  await until(async()=> (await state()).sources.length===2,'Second tab did not connect');await popup.close();await b.locator('#tone').click();
  const sourceB=await until(async()=> (await state()).sources.find(s=>s.name==='Test tab B'&&s.bufferedMs>=40),'Second tab PCM missing');
  await patch(sourceA.id,{muted:true,displayName:'음악 탭'});await patch(sourceB.id,{muted:true});
  assert.equal((await render()).rms,0);
  await patch(sourceB.id,{muted:false,volume:0.5});await until(async()=> (await render()).rms>0,'Second tab PCM remained silent');
  assert((await state()).sources.find(s=>s.id===sourceA.id).settings.muted);
  // Simulate the Windows updater replacing the installed files. Active captures defer reload.
  const nextVersion=manifest.version.split('.').map(Number);nextVersion[2]++;
  const nextManifest={...manifest,version:nextVersion.join('.')};
  await writeFile(join(extension,'manifest.json'),JSON.stringify(nextManifest));
  assert.equal(await worker.evaluate(async()=>(await(await fetch(chrome.runtime.getURL('manifest.json'),{cache:'no-store'})).json()).version),nextManifest.version,'Changed manifest is not visible to the updater');
  await worker.evaluate(()=>globalThis.checkInstalledUpdate());
  assert.equal(await worker.evaluate(()=>chrome.runtime.getManifest().version),manifest.version);
  assert.equal((await state()).sources.length,2);
  await worker.evaluate(()=>chrome.runtime.sendMessage({target:'offscreen',type:'stop-all'}));
  await until(async()=> (await state()).sources.length===0,'Stopped captures remain active');
  await worker.evaluate(()=>globalThis.checkInstalledUpdate()).catch(()=>{});
  const awaken=await context.newPage();await awaken.goto(`chrome-extension://${id}/popup.html`);
  await until(async()=>{
    for(const candidate of context.serviceWorkers())try{if(await candidate.evaluate(()=>chrome.runtime.getManifest().version)===nextManifest.version)return true;}catch{}
    return false;
  },'Extension did not reload the updated files when idle');
  await context.close();context=null;
  await until(async()=> (await state()).sources.length===0,'Closed browser sources remain active');
  const reopened=await launch();const page=await context.newPage();await page.goto(origin+'/a');
  popup=await popupFor(page,reopened.id);
  await until(()=>popup.locator('#start').isEnabled(),'Saved pairing was not reused after browser restart');
  await popup.locator('#profile').selectOption(profileId);await popup.locator('#start').click();
  await until(async()=> (await state()).sources.some(s=>s.name==='음악 탭'&&s.settings.muted),'Saved tab settings were not restored');
  console.log(JSON.stringify({tabs:2,rawPcm:true,popupIndependent:true,serverMuteAndVolume:true,rename:true,idleExtensionUpdate:true,pairingAndProfileRestored:true,hardwarePlayback:false}));
} finally {
  await context?.close();await new Promise(resolve=>fixture.close(resolve));
  host.stdin.end('exit\n');await Promise.race([once(host,'exit'),new Promise(resolve=>setTimeout(resolve,5000))]);if(host.exitCode===null)host.kill();
}
