package com.lunce.soundmanager;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class PairingLink {
    final String address, key;

    PairingLink(String address, String key) {
        this.address = normalizeAddress(address);
        if (key == null || !key.matches("[a-zA-Z0-9]{24,128}"))
            throw new IllegalArgumentException("연결 키를 확인하세요.");
        this.key = key;
    }

    static String normalizeAddress(String text) {
        try {
            if (text == null || text.length() > 2048) throw new IllegalArgumentException("서버 주소를 확인하세요.");
            URI uri = new URI(text.trim()); String scheme = uri.getScheme();
            if (!("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme)) || uri.getHost() == null ||
                uri.getRawUserInfo() != null || uri.getRawQuery() != null || uri.getRawFragment() != null ||
                !(uri.getRawPath().isEmpty() || uri.getRawPath().equals("/")) || uri.getPort() == 0 || uri.getPort() > 65535)
                throw new IllegalArgumentException("서버의 http(s) 주소만 입력하세요. 경로·계정·쿼리는 사용할 수 없습니다.");
            if ("http".equalsIgnoreCase(scheme) && !isPrivateHost(uri.getHost()))
                throw new IllegalArgumentException("외부 서버는 HTTPS 주소가 필요합니다.");
            return new URI(scheme.toLowerCase(Locale.ROOT), null, uri.getHost(), uri.getPort(), null, null, null).toASCIIString();
        } catch (URISyntaxException | NullPointerException error) {
            throw new IllegalArgumentException("서버의 http(s) 주소만 입력하세요.");
        }
    }

    private static boolean isPrivateHost(String name) {
        String host = name.toLowerCase(Locale.ROOT).replace("[", "").replace("]", "");
        // Never resolve DNS when checking a QR payload.
        if (host.contains(":")) {
            try {
                InetAddress ip = InetAddress.getByName(host);
                byte[] bytes = ip.getAddress();
                if (bytes.length == 4) return privateV4(bytes);
                return ip.isLoopbackAddress() || ip.isLinkLocalAddress() || ip.isSiteLocalAddress() || (bytes[0] & 0xfe) == 0xfc;
            } catch (Exception ignored) { return false; }
        }
        if (host.matches("[0-9.]+")) {
            String[] parts = host.split("\\.", -1);
            if (parts.length != 4) return false;
            byte[] bytes = new byte[4];
            for (int i=0; i<4; i++) {
                try { int value = Integer.parseInt(parts[i]); if (value < 0 || value > 255) return false; bytes[i] = (byte)value; }
                catch (NumberFormatException error) { return false; }
            }
            return privateV4(bytes);
        }
        return host.equals("localhost") || !host.contains(".") || host.endsWith(".local");
    }

    private static boolean privateV4(byte[] bytes) {
        int a = bytes[0] & 255, b = bytes[1] & 255;
        return a == 10 || a == 127 || a == 192 && b == 168 || a == 172 && b >= 16 && b <= 31 ||
            a == 169 && b == 254 || a == 100 && b >= 64 && b <= 127;
    }

    static PairingLink parse(String text) {
        try {
            if (text == null || text.length() > 4096) throw new IllegalArgumentException();
            URI uri = new URI(text.trim());
            if (!"soundmanager".equals(uri.getScheme()) || !"pair".equals(uri.getHost()) ||
                uri.getPort() != -1 || uri.getRawUserInfo() != null || uri.getRawFragment() != null ||
                !(uri.getRawPath().isEmpty() || uri.getRawPath().equals("/")) || uri.getRawQuery() == null)
                throw new IllegalArgumentException();
            Map<String, String> fields = new HashMap<>();
            for (String part : uri.getRawQuery().split("&", -1)) {
                String[] pair = part.split("=", 2);
                if (pair.length != 2 || fields.put(decode(pair[0]), decode(pair[1])) != null)
                    throw new IllegalArgumentException();
            }
            if (fields.size() != 3 || !"1".equals(fields.get("v"))) throw new IllegalArgumentException();
            return new PairingLink(fields.get("server"), fields.get("key"));
        } catch (URISyntaxException | IllegalArgumentException error) {
            throw new IllegalArgumentException("올바른 Sound Manager 연결 QR 또는 연결 코드를 사용하세요.");
        }
    }

    private static String decode(String value) {
        try { return URLDecoder.decode(value, StandardCharsets.UTF_8.name()); }
        catch (java.io.UnsupportedEncodingException impossible) { throw new IllegalStateException(impossible); }
    }
}
