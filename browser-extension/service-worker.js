import {parsePairing, sourceName, hostPermission} from './protocol.js';
import {loadPairing, savePairing, forgetPairing} from './auth-store.js';
let creating;
let queue = Promise.resolve();
const starting = new Set();
async function ensureOffscreen() {
  if ((await chrome.runtime.getContexts({contextTypes: ['OFFSCREEN_DOCUMENT']})).length) return;
  await (creating ??= chrome.offscreen.createDocument({url: 'offscreen.html', reasons: ['USER_MEDIA'], justification: '선택한 탭의 원본 오디오를 서버로 전송합니다.'}).finally(() => {creating = null;}));
}
async function offscreen(message) {
  if (!(await chrome.runtime.getContexts({contextTypes: ['OFFSCREEN_DOCUMENT']})).length) return {ok: true, streams: []};
  return chrome.runtime.sendMessage({target: 'offscreen', ...message});
}
async function state(pair) {
  const response = await fetch(pair.address + '/v1/state', {headers: {'X-Sound-Key': pair.key}, redirect: 'error', signal: AbortSignal.timeout(8000)});
  if (!response.ok) throw new Error('서버 연결과 인증을 확인하세요.');
  return response.json();
}
async function handle(message) {
  if (message.type === 'capture-state') {
    const text = message.status === '전송 중' ? 'ON' : message.status === '중지됨' ? '' : '…';
    await chrome.action.setBadgeText({tabId: message.tabId, text}).catch(() => {});
    await chrome.action.setBadgeBackgroundColor({tabId: message.tabId, color: '#257465'}).catch(() => {});
    await chrome.action.setTitle({tabId: message.tabId, title: 'Sound Manager · ' + message.status}).catch(() => {});
    const values = (await chrome.storage.session.get('statuses')).statuses ?? {};
    values[message.tabId] = message.status; await chrome.storage.session.set({statuses: values}); return {ok: true};
  }
  if (message.type === 'status') {
    const pair = await loadPairing();
    const saved = await chrome.storage.local.get('profiles');
    const session = await chrome.storage.session.get(['bindings', 'statuses']);
    const captures = await offscreen({type: 'status'});
    return {ok: true, paired: !!pair, profiles: saved.profiles ?? [], bindings: session.bindings ?? {}, statuses: session.statuses ?? {}, streams: captures.streams ?? []};
  }
  if (message.type === 'pair') {
    const pair = parsePairing(message.code);
    if (!await chrome.permissions.contains({origins: [hostPermission(pair.address)]})) throw new Error('서버 주소 접근 권한이 필요합니다.');
    await state(pair);
    await offscreen({type: 'stop-all'});
    await savePairing(pair); return {ok: true};
  }
  if (message.type === 'forget') {
    await offscreen({type: 'stop-all'}); await forgetPairing(); return {ok: true};
  }
  if (message.type === 'stop') { return offscreen({type: 'stop', tabId: message.tabId}); }
  if (message.type === 'start') {
    if (starting.has(message.tabId)) throw new Error('탭을 준비하고 있습니다.');
    starting.add(message.tabId);
    try {
      const pair = await loadPairing(); if (!pair) throw new Error('서버 연결 코드를 먼저 등록하세요.');
      const tab = await chrome.tabs.get(message.tabId);
      if (!/^https?:/.test(tab.url ?? '')) throw new Error('음악을 재생하는 일반 웹 탭에서 시작하세요.');
      const current = await offscreen({type: 'status'});
      if (current.streams?.some(s => s.tabId === tab.id)) throw new Error('이 탭은 이미 전송 중입니다.');
      const profiles = (await chrome.storage.local.get('profiles')).profiles ?? [];
      let profile = profiles.find(p => p.id === message.profileId);
      if (!profile) {
        if (profiles.length >= 128) throw new Error('저장된 탭 설정이 너무 많습니다.');
        profile = {id: crypto.randomUUID(), label: sourceName(tab.title)}; profiles.push(profile);
        await chrome.storage.local.set({profiles});
      }
      if (current.streams?.some(s => s.profileId === profile.id)) throw new Error('해당 탭 설정은 다른 탭에서 사용 중입니다. 새 항목을 선택하세요.');
      await ensureOffscreen();
      const streamId = await chrome.tabCapture.getMediaStreamId({targetTabId: tab.id});
      const result = await offscreen({type: 'start', tabId: tab.id, streamId, profile, pair});
      if (!result.ok) throw new Error(result.error);
      const bindings = (await chrome.storage.session.get('bindings')).bindings ?? {};
      bindings[tab.id] = profile.id; await chrome.storage.session.set({bindings});
      return {ok: true, profileId: profile.id};
    } finally { starting.delete(message.tabId); }
  }
  throw new Error('지원하지 않는 동작입니다.');
}
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message.target !== 'worker' || sender.id !== chrome.runtime.id) return;
  const operation = queue.then(() => handle(message)); queue = operation.catch(() => {});
  operation.then(reply).catch(error => reply({ok: false, error: error.message || '요청을 처리하지 못했습니다.'}));
  return true;
});
chrome.tabs.onRemoved.addListener(tabId => { offscreen({type: 'stop', tabId}).catch(() => {}); });

// The Windows updater refreshes this unpacked extension's stable folder.
// Reload only after every capture ends so an update cannot interrupt audio.
async function refreshInstalledFiles() {
  if (starting.size) return;
  const response = await fetch(chrome.runtime.getURL('manifest.json'), {cache: 'no-store'});
  const installed = await response.json();
  if (installed.version === chrome.runtime.getManifest().version) return;
  const captures = await offscreen({type: 'status'});
  if (!(captures.streams?.length)) chrome.runtime.reload();
}
chrome.alarms.create('update-files', {periodInMinutes: 5});
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === 'update-files') {
    const operation = queue.then(refreshInstalledFiles); queue = operation.catch(() => {});
  }
});
