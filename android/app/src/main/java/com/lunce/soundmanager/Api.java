package com.lunce.soundmanager;

import org.json.JSONObject;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import okhttp3.*;

final class Api {
    static final OkHttpClient CLIENT = new OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS).readTimeout(10, TimeUnit.SECONDS)
        .callTimeout(20, TimeUnit.SECONDS).pingInterval(15, TimeUnit.SECONDS)
        .followRedirects(false).followSslRedirects(false).build();
    final String address, key;

    Api(String address, String key) {
        this.address = PairingLink.normalizeAddress(address);
        if (key.trim().length() < 24) throw new IllegalArgumentException("서버 연결 키를 확인하세요.");
        this.key = key.trim();
    }

    JSONObject call(String method, String path, JSONObject body) throws Exception {
        Request.Builder request = new Request.Builder().url(address + path).header("X-Sound-Key", key);
        if (body != null) request.method(method, RequestBody.create(body.toString(), MediaType.get("application/json")));
        try (Response response = CLIENT.newCall(request.build()).execute()) {
            if (!response.isSuccessful()) throw new IOException(response.code() == 401 ? "연결 키가 올바르지 않습니다." : "서버 응답: " + response.code());
            String text = response.body() == null ? "" : response.body().string();
            return text.isEmpty() ? new JSONObject() : new JSONObject(text);
        }
    }
}
