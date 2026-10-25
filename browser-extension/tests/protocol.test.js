import test from 'node:test';
import assert from 'node:assert/strict';
import {parsePairing, normalizeAddress, audioEndpoint, hello, hostPermission} from '../protocol.js';
import {encryptPairing, decryptPairing} from '../auth-store.js';
import {PcmBatcher} from '../pcm-batcher.js';

const key = 'a'.repeat(48);
test('Pairing validates LAN/HTTPS, duplicate fields, and keeps credentials out of the socket URL', () => {
  const code = `soundmanager://pair?v=1&server=${encodeURIComponent('http://192.168.1.5:17842')}&key=${key}`;
  assert.deepEqual(parsePairing(code), {address: 'http://192.168.1.5:17842', key});
  assert.equal(audioEndpoint('https://test.trycloudflare.com'), 'wss://test.trycloudflare.com/v1/browser-audio');
  assert.equal(audioEndpoint('http://127.0.0.1:17842'), 'ws://127.0.0.1:17842/v1/browser-audio');
  assert.equal(hostPermission('http://127.0.0.1:17842'), 'http://127.0.0.1/*');
  for (const url of ['http://example.com', 'https://example.com/path', 'https://user:pass@example.com', 'https://example.com/?key=abc'])
    assert.throws(() => normalizeAddress(url));
  assert.throws(() => parsePairing(code + '&key=' + key));
  assert.throws(() => parsePairing(code.replace('v=1', 'v=2')));
});
test('Each tab profile has its own stable identity and advertises the actual capture format', () => {
  const pair = {address: 'http://127.0.0.1:17842', key};
  const a = {id: crypto.randomUUID(), label: '음악 탭'}, b = {id: crypto.randomUUID(), label: '다른 탭'};
  assert.notEqual(hello(pair, a, 44100, 2).deviceId, hello(pair, b, 48000, 1).deviceId);
  assert.equal(hello(pair, a, 44100, 2).deviceId, a.id);
  assert.deepEqual(hello(pair, a, 44100, 2).format, {sampleRate: 44100, channels: 2, encoding: 'float32'});
  assert.throws(() => hello(pair, a, 48000, 8));
});
test('Saved pairing uses authenticated encryption and survives serialization without plain credentials', async () => {
  const secret = await crypto.subtle.generateKey({name: 'AES-GCM', length: 256}, false, ['encrypt', 'decrypt']);
  const pair = {address: 'https://test.trycloudflare.com', key};
  const encrypted = await encryptPairing(pair, secret);
  assert(!JSON.stringify(encrypted).includes(key));
  assert(!JSON.stringify(encrypted).includes(pair.address));
  assert.deepEqual(await decryptPairing(JSON.parse(JSON.stringify(encrypted)), secret), pair);
  encrypted.bytes[0] ^= 1;
  await assert.rejects(() => decryptPairing(encrypted, secret));
});
test('PCM batching preserves exact sample values, channel order, and little endian representation', () => {
  const packets = []; const pcm = new PcmBatcher(2, bytes => packets.push(bytes), 4);
  pcm.push([new Float32Array([0.25, -0.5]), new Float32Array([0.75, 0])]);
  assert.equal(packets.length, 0);
  pcm.push([new Float32Array([1, -1]), new Float32Array([-0.25, 0.125])]);
  const expected = [0.25, 0.75, -0.5, 0, 1, -0.25, -1, 0.125];
  const view = new DataView(packets[0]); expected.forEach((value, i) => assert.equal(view.getFloat32(i * 4, true), value));
  assert(packets[0].byteLength <= 65536);
});
test('A paused tab produces silence without changing the sample clock or replaying old samples', () => {
  const packets = []; const pcm = new PcmBatcher(1, bytes => packets.push(bytes), 128);
  pcm.push([], 128); assert.equal(packets.length, 1);
  assert([...new Float32Array(packets[0])].every(value => value === 0));
});
