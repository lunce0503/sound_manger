export function normalizeAddress(address) {
  if (typeof address !== 'string' || address.length > 2048) throw new Error('서버 주소를 확인하세요.');
  const url = new URL(address.trim());
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.pathname !== '/' || url.search || url.hash || url.port === '0')
    throw new Error('경로·계정·쿼리 없는 서버 주소를 입력하세요.');
  const host = url.hostname.replace(/^\[|\]$/g, '').toLowerCase();
  const bytes = host.split('.').map(Number);
  const ipv4 = /^\d+\.\d+\.\d+\.\d+$/.test(host);
  const private4 = ipv4 && (bytes[0] === 10 || bytes[0] === 127 || bytes[0] === 192 && bytes[1] === 168 ||
    bytes[0] === 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] === 169 && bytes[1] === 254 || bytes[0] === 100 && bytes[1] >= 64 && bytes[1] <= 127);
  const private6 = host === '::1' || /^(fc|fd|fe[89ab])/i.test(host) && host.includes(':');
  const localName = !host.includes(':') && !ipv4 && (!host.includes('.') || host.endsWith('.local'));
  if (url.protocol === 'http:' && !private4 && !private6 && !localName) throw new Error('외부 연결에는 HTTPS가 필요합니다.');
  return url.origin;
}

export function parsePairing(text) {
  if (typeof text !== 'string' || text.length > 4096) throw new Error('연결 코드를 확인하세요.');
  const url = new URL(text.trim());
  if (url.protocol !== 'soundmanager:' || url.hostname !== 'pair' || !['', '/'].includes(url.pathname) || url.username || url.password || url.port || url.hash)
    throw new Error('Sound Manager 연결 코드를 붙여넣으세요.');
  const fields = [...url.searchParams];
  if (fields.length !== 3 || new Set(fields.map(([key]) => key)).size !== 3 || url.searchParams.get('v') !== '1' ||
      !url.searchParams.has('server') || !url.searchParams.has('key')) throw new Error('올바른 연결 코드를 사용하세요.');
  const key = url.searchParams.get('key');
  if (!/^[A-Za-z0-9]{24,128}$/.test(key)) throw new Error('연결 키를 확인하세요.');
  return {address: normalizeAddress(url.searchParams.get('server')), key};
}

export function audioEndpoint(address) {
  const url = new URL(normalizeAddress(address));
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.pathname = '/v1/browser-audio';
  return url.href;
}

export function hostPermission(address) {
  const url = new URL(normalizeAddress(address));
  return `${url.protocol}//${url.hostname}/*`;
}

export function sourceName(title) { return String(title || '브라우저 탭').replace(/[\x00-\x1f\x7f]/g, ' ').trim().slice(0, 64) || '브라우저 탭'; }

export function hello(pair, profile, sampleRate, channels) {
  if (!Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 192000 || ![1, 2].includes(channels) ||
      !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(profile.id) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(profile.id)) throw new Error('음원 형식을 확인하세요.');
  return {version: 1, deviceId: profile.id, name: sourceName(profile.label), format: {sampleRate, channels, encoding: 'float32'}, key: pair.key};
}
