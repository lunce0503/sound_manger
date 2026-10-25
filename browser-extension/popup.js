import {parsePairing, hostPermission} from './protocol.js';
const $ = id => document.getElementById(id);
let tab, busy = false, chosenProfile;
async function message(type, values = {}) {
  const result = await chrome.runtime.sendMessage({target: 'worker', type, ...values});
  if (!result?.ok) throw new Error(result?.error || '요청을 처리하지 못했습니다.');
  return result;
}
async function refresh() {
  if (busy) return;
  const state = await message('status');
  const stream = state.streams.find(s => s.tabId === tab?.id);
  const selection = chosenProfile ?? state.bindings[tab?.id] ?? '';
  const options = [new Option('새 탭 음원', ''), ...state.profiles.map(p => new Option(p.label, p.id))];
  $('profile').replaceChildren(...options); $('profile').value = selection;
  $('start').hidden = !!stream; $('start').disabled = !state.paired || !/^https?:/.test(tab?.url ?? '');
  $('stop').hidden = !stream; $('profile').disabled = !!stream;
  $('status').textContent = stream?.status || state.statuses[tab?.id] || (state.paired ? '서버 연결 정보가 저장되어 있습니다.' : '서버 연결 코드를 등록하세요.');
}
async function run(action) {
  if (busy) return;
  let succeeded = false;
  busy = true; document.querySelectorAll('button').forEach(button => button.disabled = true);
  try { await action(); succeeded = true; } catch (error) { $('status').textContent = error.message; }
  finally {
    busy = false; document.querySelectorAll('button').forEach(button => button.disabled = false);
    if (succeeded) await refresh().catch(() => {});
  }
}
$('pair').addEventListener('click', () => run(async () => {
  const code = $('pairing').value; const pair = parsePairing(code);
  if (!await chrome.permissions.request({origins: [hostPermission(pair.address)]})) throw new Error('서버 주소 접근을 허용해야 연결할 수 있습니다.');
  await message('pair', {code}); $('pairing').value = ''; $('connection').open = false; $('status').textContent = '연결 정보를 저장했습니다.';
}));
$('forget').addEventListener('click', () => run(async () => { await message('forget'); $('pairing').value = ''; $('status').textContent = '연결 정보를 지웠습니다.'; }));
$('start').addEventListener('click', () => run(async () => {
  $('status').textContent = '탭을 준비하고 있습니다…';
  const result = await message('start', {tabId: tab.id, profileId: $('profile').value}); chosenProfile = result.profileId;
}));
$('stop').addEventListener('click', () => run(() => message('stop', {tabId: tab.id})));
$('profile').addEventListener('change', () => {chosenProfile = $('profile').value;});
([tab] = await chrome.tabs.query({active: true, currentWindow: true}));
$('tab-name').textContent = tab?.title || '현재 탭';
refresh().catch(() => {$('status').textContent = '확장을 다시 열어주세요.';});
setInterval(() => refresh().catch(() => {}), 1500);
