package com.lunce.soundmanager;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.UUID;

final class DeviceIdentity {
    private DeviceIdentity() {}

    static synchronized String get(Context context) {
        SharedPreferences preferences=context.getSharedPreferences("audio-device-identity",Context.MODE_PRIVATE);
        String saved=preferences.getString("id",null);
        if (saved!=null) {
            UUID id=UUID.fromString(saved);
            if (!id.toString().equals(saved) || id.equals(new UUID(0,0))) throw new IllegalStateException("저장된 기기 ID를 확인하세요.");
            return saved;
        }
        String id=UUID.randomUUID().toString();
        if (!preferences.edit().putString("id",id).commit()) throw new IllegalStateException("기기 ID를 저장하지 못했습니다.");
        return id;
    }
}
