package com.lunce.soundmanager;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import javax.crypto.Cipher;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class ConnectionCipher {
    private static final byte[] CONTEXT = "SoundManager.connection.v1".getBytes(StandardCharsets.UTF_8);
    private final SecretKey key;
    ConnectionCipher(SecretKey key) { this.key = key; }

    byte[] encrypt(String value) throws GeneralSecurityException {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key); cipher.updateAAD(CONTEXT);
        byte[] iv = cipher.getIV(), encrypted = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
        return ByteBuffer.allocate(2 + iv.length + encrypted.length).put((byte)1).put((byte)iv.length).put(iv).put(encrypted).array();
    }

    String decrypt(byte[] value) throws GeneralSecurityException {
        if (value.length < 30 || value[0] != 1 || value[1] != 12)
            throw new GeneralSecurityException("저장된 연결 정보 형식이 올바르지 않습니다.");
        byte[] iv = java.util.Arrays.copyOfRange(value, 2, 14);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(128, iv)); cipher.updateAAD(CONTEXT);
        return new String(cipher.doFinal(value, 14, value.length - 14), StandardCharsets.UTF_8);
    }
}
