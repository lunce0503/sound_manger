package com.lunce.soundmanager;

import org.json.JSONObject;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import okhttp3.*;

final class Api {
    static final class Failure extends IOException {
        final int status;
        Failure(int status) { super(status == 401 ? "서버 인증을 다시 연결해주세요." : "서버 응답: " + status); this.status = status; }
    }
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
            if (!response.isSuccessful()) throw new Failure(response.code());
            String text = response.body() == null ? "" : response.body().string();
            return text.isEmpty() ? new JSONObject() : new JSONObject(text);
        }
    }
}
