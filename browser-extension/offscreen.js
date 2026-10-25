import {audioEndpoint, hello} from './protocol.js';
const streams = new Map();
function report(stream, status) {
  stream.status = status;
  chrome.runtime.sendMessage({target: 'worker', type: 'capture-state', tabId: stream.tabId, status}).catch(() => {});
}
async function stop(tabId, reason = '중지됨') {
  const stream = streams.get(tabId); if (!stream) return;
  stream.stopped = true; streams.delete(tabId); clearTimeout(stream.retry); clearInterval(stream.heartbeat);
  stream.socket?.close(1000, 'Stopped');
  stream.node?.disconnect(); stream.source?.disconnect();
  stream.media?.getTracks().forEach(track => track.stop());
  if (stream.context) await stream.context.close();
  report(stream, reason);
}
function connect(stream) {
  if (stream.stopped) return;
  report(stream, '서버 연결 중');
  const socket = new WebSocket(audioEndpoint(stream.pair.address)); stream.socket = socket; stream.ready = false;
  const timeout = setTimeout(() => { if (!stream.ready) socket.close(4000, 'Handshake timeout'); }, 8000);
  socket.onopen = () => socket.send(JSON.stringify(hello(stream.pair, stream.profile, stream.context.sampleRate, stream.channels)));
  socket.onmessage = event => {
    try {
      if (typeof JSON.parse(event.data).sourceId !== 'string') throw new Error();
      stream.ready = true; stream.backoff = 1000; clearTimeout(timeout); report(stream, '전송 중');
    } catch { socket.close(4000, 'Invalid response'); }
  };
  socket.onclose = event => {
    clearTimeout(timeout); stream.ready = false;
    if (stream.stopped || stream.socket !== socket) return;
    if ([1007, 1008].includes(event.code)) { stop(stream.tabId, '서버 인증·음원 형식·연결 한도를 확인하세요.'); return; }
    report(stream, '재연결 대기 중');
    stream.retry = setTimeout(() => connect(stream), stream.backoff);
    stream.backoff = Math.min(stream.backoff * 2, 15000);
  };
  socket.onerror = () => socket.close();
}
async function start(message) {
  if (streams.has(message.tabId)) throw new Error('이 탭은 이미 전송 중입니다.');
  const stream = {tabId: message.tabId, profile: message.profile, pair: message.pair, backoff: 1000, stopped: false, status: '탭 준비 중'};
  streams.set(message.tabId, stream);
  try {
    const media = navigator.mediaDevices.getUserMedia({audio: {mandatory: {chromeMediaSource: 'tab', chromeMediaSourceId: message.streamId}}, video: false});
    media.then(value => {if (stream.stopped) value.getTracks().forEach(track => track.stop());}, () => {});
    let deadline;
    try { stream.media = await Promise.race([media, new Promise((_, reject) => {deadline = setTimeout(() => reject(new Error('Capture timeout')), 12000);})]); }
    finally { clearTimeout(deadline); }
    if (stream.stopped) throw new Error('Capture cancelled');
    stream.channels = stream.media.getAudioTracks()[0].getSettings().channelCount === 1 ? 1 : 2;
    stream.context = new AudioContext();
    await stream.context.audioWorklet.addModule(chrome.runtime.getURL('pcm-worklet.js'));
    if (stream.stopped) throw new Error('Capture cancelled');
    stream.source = stream.context.createMediaStreamSource(stream.media);
    stream.node = new AudioWorkletNode(stream.context, 'raw-pcm', {numberOfInputs: 1, numberOfOutputs: 1,
      outputChannelCount: [stream.channels], channelCount: stream.channels, channelCountMode: 'explicit', processorOptions: {channels: stream.channels}});
    stream.node.port.onmessage = event => {
      if (!stream.ready || stream.socket.readyState !== WebSocket.OPEN) return;
      if (stream.socket.bufferedAmount > stream.context.sampleRate * stream.channels * 4 / 5) { stream.socket.close(4000, 'Network too slow'); return; }
      stream.socket.send(event.data);
    };
    stream.source.connect(stream.node).connect(stream.context.destination);
    stream.media.getTracks().forEach(track => track.addEventListener('ended', () => stop(stream.tabId), {once: true}));
    await stream.context.resume();
    stream.heartbeat = setInterval(() => { if (stream.ready && stream.socket.readyState === WebSocket.OPEN) stream.socket.send('{"type":"keepalive"}'); }, 3000);
    connect(stream);
    return {ok: true};
  } catch (error) { await stop(stream.tabId); throw error; }
}
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message.target !== 'offscreen' || sender.id !== chrome.runtime.id) return;
  const action = async () => {
    if (message.type === 'start') return start(message);
    if (message.type === 'stop') await stop(message.tabId);
    if (message.type === 'stop-all') await Promise.all([...streams.keys()].map(id => stop(id)));
    return {ok: true, streams: [...streams.values()].map(s => ({tabId: s.tabId, profileId: s.profile.id, label: s.profile.label, status: s.status}))};
  };
  action().then(reply).catch(() => reply({ok: false, error: '탭 전송을 시작하지 못했습니다. 탭을 새로 열고 다시 시도하세요.'}));
  return true;
});
