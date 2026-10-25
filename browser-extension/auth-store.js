let keyPromise;
async function key() {
  return keyPromise ??= (async () => {
    const db = await new Promise((resolve, reject) => {
      const request = indexedDB.open('sound-manager-auth', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('keys');
      request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
    });
    const saved = await new Promise((resolve, reject) => {
      const request = db.transaction('keys').objectStore('keys').get('pairing');
      request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
    });
    if (saved) { db.close(); return saved; }
    const generated = await crypto.subtle.generateKey({name: 'AES-GCM', length: 256}, false, ['encrypt', 'decrypt']);
    await new Promise((resolve, reject) => {
      const transaction = db.transaction('keys', 'readwrite'); transaction.objectStore('keys').put(generated, 'pairing');
      transaction.oncomplete = resolve; transaction.onerror = () => reject(transaction.error);
    });
    db.close(); return generated;
  })();
}

export async function encryptPairing(pair, secret) {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const bytes = await crypto.subtle.encrypt({name: 'AES-GCM', iv}, secret, new TextEncoder().encode(JSON.stringify(pair)));
  return {iv: [...iv], bytes: [...new Uint8Array(bytes)]};
}
export async function decryptPairing(encrypted, secret) {
  const bytes = await crypto.subtle.decrypt({name: 'AES-GCM', iv: new Uint8Array(encrypted.iv)}, secret, new Uint8Array(encrypted.bytes));
  return JSON.parse(new TextDecoder().decode(bytes));
}
export async function savePairing(pair) { await chrome.storage.local.set({pairingEncrypted: await encryptPairing(pair, await key())}); }
export async function loadPairing() {
  const saved = (await chrome.storage.local.get('pairingEncrypted')).pairingEncrypted;
  return saved ? decryptPairing(saved, await key()) : null;
}
export async function forgetPairing() { await chrome.storage.local.remove('pairingEncrypted'); }
