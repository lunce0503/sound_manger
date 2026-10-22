package com.lunce.soundmanager;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.security.KeyStore;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;

final class ConnectionStore {
    private static final String ALIAS = "SoundManager.savedConnection.v1";
    private final SharedPreferences preferences;
    private final ConnectionCipher cipher;

    ConnectionStore(Context context) throws Exception { this(context, getKey()); }
    ConnectionStore(Context context, SecretKey key) {
        preferences = context.getSharedPreferences("trusted_connection", Context.MODE_PRIVATE);
        cipher = new ConnectionCipher(key);
    }

    private static SecretKey getKey() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (store.containsAlias(ALIAS)) return (SecretKey)store.getKey(ALIAS, null);
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setKeySize(256).setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
        return generator.generateKey();
    }

    PairingLink load() throws Exception {
        String value = preferences.getString("encrypted", null);
        return value == null ? null : PairingLink.parse(cipher.decrypt(Base64.decode(value, Base64.NO_WRAP)));
    }

    void save(PairingLink pair) throws Exception {
        String encrypted = Base64.encodeToString(cipher.encrypt(pair.encode()), Base64.NO_WRAP);
        if (!preferences.edit().putString("encrypted", encrypted).commit())
            throw new java.io.IOException("연결 정보를 저장하지 못했습니다.");
    }

    void clear() {
        if (!preferences.edit().clear().commit()) throw new IllegalStateException("저장된 연결 정보를 지우지 못했습니다.");
    }
}
