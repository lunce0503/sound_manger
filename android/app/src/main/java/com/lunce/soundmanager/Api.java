package com.lunce.soundmanager;

import org.json.JSONObject;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import okhttp3.*;

final class Api {
    static final OkHttpClient CLIENT = new OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS).readTimeout(5, TimeUnit.SECONDS)
        .callTimeout(8, TimeUnit.SECONDS).pingInterval(15, TimeUnit.SECONDS).build();
    final String address, key;

    Api(String address, String key) {
        HttpUrl url = HttpUrl.parse(address.trim());
        if (url == null || !url.username().isEmpty() || !url.password().isEmpty())
            throw new IllegalArgumentException("http://서버IP:17842 주소를 입력하세요.");
        this.address = url.newBuilder().encodedPath("/").query(null).fragment(null).build().toString().replaceAll("/$", "");
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
