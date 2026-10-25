import test from 'node:test';
import assert from 'node:assert/strict';
let listener;
const reports = [], sockets = [], nodes = [], contexts = [], tracks = [];
globalThis.chrome = {runtime: {id: 'a'.repeat(32), getURL: path => path, onMessage: {addListener: value => listener = value}, sendMessage: async value => reports.push(value)}};
Object.defineProperty(globalThis, 'navigator', {value: {mediaDevices: {getUserMedia: async () => {
  const track = {stopped: false, getSettings: () => ({channelCount: 2}), stop() {this.stopped = true;}, addEventListener() {}};
  tracks.push(track); return {getTracks: () => [track], getAudioTracks: () => [track]};
}}}, configurable: true});
globalThis.AudioContext = class {
  sampleRate = 48000; destination = {}; audioWorklet = {addModule: async () => {}};
  constructor() { contexts.push(this); }
  createMediaStreamSource() {return {connect: node => node, disconnect() {}};}
  async resume() {} async close() {this.closed = true;}
};
globalThis.AudioWorkletNode = class {
  port = {}; constructor() {nodes.push(this);} connect() {return this;} disconnect() {}
};
globalThis.WebSocket = class {
  static OPEN = 1; readyState = 0; bufferedAmount = 0; sent = [];
  constructor(url) {this.url = url; sockets.push(this);}
  open() {this.readyState = 1; this.onopen?.();}
  send(value) {this.sent.push(value);}
  close(code = 1000) {this.readyState = 3; this.onclose?.({code});}
};
await import('../offscreen.js');
const request = values => new Promise(resolve => listener({target: 'offscreen', ...values}, {id: chrome.runtime.id}, resolve));
const start = tabId => request({type: 'start', tabId, streamId: 'test', pair: {address: 'http://127.0.0.1:17842', key: 'b'.repeat(48)}, profile: {id: crypto.randomUUID(), label: 'Tab ' + tabId}});
const tick = () => new Promise(resolve => setTimeout(resolve, 10));

test('Offscreen capture outlives popup, authenticates first, and streams independent raw tab buffers', async () => {
  try {
    assert((await start(1)).ok); assert((await start(2)).ok);
    const [a, b] = sockets; const packet = new ArrayBuffer(8192);
    nodes[0].port.onmessage({data: packet}); assert.equal(a.sent.length, 0);
    a.open(); b.open();
    assert(!a.url.includes('b'.repeat(48)));
    assert.notEqual(JSON.parse(a.sent[0]).deviceId, JSON.parse(b.sent[0]).deviceId);
    nodes[0].port.onmessage({data: packet}); assert.equal(a.sent.length, 1);
    a.onmessage({data: JSON.stringify({sourceId: crypto.randomUUID()})}); b.onmessage({data: JSON.stringify({sourceId: crypto.randomUUID()})});
    nodes[0].port.onmessage({data: packet});
    assert.equal(a.sent[1], packet); assert.equal(b.sent.length, 1);
    assert.equal((await request({type: 'status'})).streams.length, 2);
    // Transport restarts keep the authorized capture and stable tab identity.
    const id = JSON.parse(a.sent[0]).deviceId; a.close(1006);
    await new Promise(resolve => setTimeout(resolve, 1100));
    assert.equal(sockets.length, 3); sockets[2].open();
    assert.equal(JSON.parse(sockets[2].sent[0]).deviceId, id);
    assert.equal(tracks.length, 2);
  } finally {await request({type: 'stop-all'});}
  assert(tracks.every(track => track.stopped)); assert(contexts.every(context => context.closed));
});
test('Authentication failure releases capture instead of retrying with rejected credentials', async () => {
  assert((await start(3)).ok);
  const current = sockets.at(-1); current.open(); current.close(1008); await tick();
  assert.equal((await request({type: 'status'})).streams.length, 0);
  assert(tracks.at(-1).stopped);
  assert(reports.at(-1).status.includes('확인'));
});
