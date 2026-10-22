package com.lunce.soundmanager;

import java.security.GeneralSecurityException;
import javax.crypto.KeyGenerator;
import org.junit.Test;
import static org.junit.Assert.*;

public class ConnectionCipherTest {
    @Test public void credentialsRoundTripWithoutPlaintextAndUseFreshNonce() throws Exception {
        KeyGenerator generator=KeyGenerator.getInstance("AES"); generator.init(256);
        ConnectionCipher cipher=new ConnectionCipher(generator.generateKey());
        String secret="soundmanager://pair?v=1&server=https%3A%2F%2Fexample.com&key=aaaaaaaaaaaaaaaaaaaaaaaa";
        byte[] first=cipher.encrypt(secret),second=cipher.encrypt(secret);
        assertFalse(java.util.Arrays.equals(first,second));
        assertFalse(new String(first,java.nio.charset.StandardCharsets.ISO_8859_1).contains("aaaaaaaaaaaaaaaaaaaaaaaa"));
        assertEquals(secret,cipher.decrypt(first));
    }
    @Test public void tamperedDataAndWrongKeyAreRejected() throws Exception {
        KeyGenerator generator=KeyGenerator.getInstance("AES"); generator.init(256);
        ConnectionCipher cipher=new ConnectionCipher(generator.generateKey());
        byte[] value=cipher.encrypt("private pairing"); value[value.length-1]^=1;
        assertThrows(GeneralSecurityException.class,()->cipher.decrypt(value));
        ConnectionCipher other=new ConnectionCipher(generator.generateKey());
        byte[] valid=cipher.encrypt("private pairing");
        assertThrows(GeneralSecurityException.class,()->other.decrypt(valid));
    }
}
