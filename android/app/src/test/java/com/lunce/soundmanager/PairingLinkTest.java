package com.lunce.soundmanager;

import org.junit.Test;
import static org.junit.Assert.*;

public class PairingLinkTest {
    private static final String KEY = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    @Test public void readsWindowsLanQr() {
        PairingLink pair = PairingLink.parse("soundmanager://pair?v=1&server=http%3A%2F%2F192.168.0.9%3A17842&key=" + KEY);
        assertEquals("http://192.168.0.9:17842", pair.address); assertEquals(KEY, pair.key);
    }

    @Test public void readsExternalHttpsQr() {
        PairingLink pair = PairingLink.parse("soundmanager://pair?v=1&server=https%3A%2F%2Fexample.trycloudflare.com&key=" + KEY);
        assertEquals("https://example.trycloudflare.com", pair.address);
    }

    @Test public void acceptsPrivateIpv6AndVpn() {
        assertEquals("http://[fd00::1]:17842", new PairingLink("http://[fd00::1]:17842", KEY).address);
        assertEquals("http://100.93.0.1:17842", new PairingLink("http://100.93.0.1:17842", KEY).address);
    }

    @Test public void rejectsDuplicateFieldsWrongVersionAndForeignQr() {
        for (String code : new String[] {
            "https://example.com",
            "soundmanager://pair?v=2&server=https%3A%2F%2Fexample.com&key=" + KEY,
            "soundmanager://pair?v=1&v=1&server=https%3A%2F%2Fexample.com&key=" + KEY,
            "soundmanager://pair?v=1&server=https%3A%2F%2Fexample.com&key=short",
            "soundmanager://pair?v=1&server=https%3A%2F%2Fexample.com&key=" + KEY + "&extra=true"
        }) assertThrows(IllegalArgumentException.class, () -> PairingLink.parse(code));
    }

    @Test public void rejectsPublicCleartextCredentialsAndPaths() {
        for (String address : new String[] {"http://example.com", "http://8.8.8.8", "https://user:password@example.com", "https://example.com/other", "https://example.com?x=1", "file:///tmp/a"})
            assertThrows(IllegalArgumentException.class, () -> new PairingLink(address, KEY));
    }
}
