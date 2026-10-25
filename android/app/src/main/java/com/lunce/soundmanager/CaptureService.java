package com.lunce.soundmanager;

import android.Manifest;
import android.app.*;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.media.projection.*;
import android.os.*;
import org.json.JSONObject;
import java.util.concurrent.atomic.AtomicBoolean;
import okhttp3.*;
import okio.ByteString;

public final class CaptureService extends Service {
    public static volatile String status = "음원 전송 중지됨";
    public static volatile boolean active;
    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicBoolean finishing = new AtomicBoolean(false);
    private final Handler main = new Handler(Looper.getMainLooper());
    private MediaProjection projection;
    private AudioRecord recorder;
    private WebSocket socket;
    private PowerManager.WakeLock wakeLock;
    private final MediaProjection.Callback callback = new MediaProjection.Callback() {
        @Override public void onStop() { finish("캡처 권한이 종료되었습니다."); }
    };

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || "stop".equals(intent.getAction())) { finish("음원 전송 중지됨"); return START_NOT_STICKY; }
        if (running.get()) return START_NOT_STICKY;
        try {
            NotificationManager notifications = getSystemService(NotificationManager.class);
            notifications.createNotificationChannel(new NotificationChannel("capture", "음원 전송", NotificationManager.IMPORTANCE_LOW));
            PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, CaptureService.class).setAction("stop"), PendingIntent.FLAG_IMMUTABLE);
            Notification notification = new Notification.Builder(this, "capture")
                .setSmallIcon(android.R.drawable.ic_media_play).setContentTitle("Sound Manager")
                .setContentText("재생 오디오를 서버로 전송 중").setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "중지", stop).build()).build();
            startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
            Intent permission = intent.getParcelableExtra("permission");
            if (permission == null) throw new IllegalArgumentException("화면/오디오 캡처 승인이 필요합니다.");
            projection = getSystemService(MediaProjectionManager.class).getMediaProjection(Activity.RESULT_OK, permission);
            projection.registerCallback(callback, main);
            running.set(true); active = true; status = "서버 연결 중…";
            wakeLock = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SoundManager:Capture");
            wakeLock.acquire(4 * 60 * 60 * 1000L);
            Api api = new Api(intent.getStringExtra("address"), intent.getStringExtra("key"));
            String deviceId=DeviceIdentity.get(this);
            Request request = new Request.Builder().url(api.address + "/v1/audio").header("X-Sound-Key", api.key).build();
            socket = Api.CLIENT.newWebSocket(request, new WebSocketListener() {
                private boolean ready;
                @Override public void onOpen(WebSocket ws, Response response) {
                    try {
                        JSONObject format = new JSONObject().put("sampleRate", 48000).put("channels", 2).put("encoding", "pcm16");
                        ws.send(new JSONObject().put("version", 1).put("name", Build.MODEL).put("format", format).put("deviceId",deviceId).toString());
                    } catch (Exception error) { finish(error.getMessage()); }
                }
                @Override public void onMessage(WebSocket ws, String text) {
                    if (ready || !running.get()) return;
                    try {
                        new JSONObject(text).getString("sourceId"); ready = true;
                        new Thread(() -> capture(ws), "pcm-capture").start();
                    } catch (Exception error) { finish("서버 형식 응답 오류"); }
                }
                @Override public void onFailure(WebSocket ws, Throwable error, Response response) { finish("전송 실패: " + error.getMessage()); }
                @Override public void onClosing(WebSocket ws, int code, String reason) { ws.close(code, reason); finish("서버 연결 종료"); }
            });
            main.postDelayed(() -> { if (running.get() && status.equals("서버 연결 중…")) finish("서버 연결 시간 초과"); }, 10000);
        } catch (Exception error) { finish("캡처 시작 실패: " + error.getMessage()); }
        return START_NOT_STICKY;
    }

    private void capture(WebSocket ws) {
        AudioRecord local = null;
        try {
            if (!running.get()) return;
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
                throw new SecurityException("오디오 캡처 권한이 없습니다.");
            AudioPlaybackCaptureConfiguration config = new AudioPlaybackCaptureConfiguration.Builder(projection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA).addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN).build();
            int minimum = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) throw new IllegalStateException("48 kHz 스테레오 캡처를 지원하지 않는 기기입니다.");
            local = new AudioRecord.Builder().setAudioFormat(new AudioFormat.Builder().setSampleRate(48000)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(Math.max(minimum * 2, 7680)).setAudioPlaybackCaptureConfig(config).build();
            synchronized (this) {
                if (!running.get()) return;
                recorder = local;
                local.startRecording();
            }
            status = "원본 PCM 전송 중 · 48 kHz / 16-bit / stereo";
            byte[] bytes = new byte[3840]; // 20 ms, no EQ, volume or resampling on client.
            while (running.get()) {
                int read = local.read(bytes, 0, bytes.length, AudioRecord.READ_BLOCKING);
                if (read < 0) throw new IllegalStateException("AudioRecord 오류 " + read);
                if (read == 0) continue;
                if (read % 4 != 0) throw new IllegalStateException("불완전한 캡처 프레임");
                if (ws.queueSize() > 38400 || !ws.send(ByteString.of(bytes, 0, read)))
                    throw new IllegalStateException("네트워크가 느려 전송을 중지했습니다. Wi-Fi를 확인하세요.");
            }
        } catch (Exception error) { if (running.get()) finish(error.getMessage()); }
        finally {
            synchronized (this) { if (recorder == local) recorder = null; }
            if (local != null) local.release();
        }
    }

    private void finish(String message) {
        if (!finishing.compareAndSet(false, true)) return;
        main.post(() -> { status = message == null ? "전송 종료" : message; cleanup(); stopSelf(); });
    }

    private synchronized void cleanup() {
        running.set(false); active = false;
        if (recorder != null) { try { recorder.stop(); } catch (IllegalStateException ignored) { } }
        if (socket != null) { socket.cancel(); socket = null; }
        if (projection != null) { projection.unregisterCallback(callback); projection.stop(); projection = null; }
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        wakeLock = null; stopForeground(STOP_FOREGROUND_REMOVE);
    }

    @Override public void onDestroy() {
        finishing.set(true);
        if (active) status = "음원 전송 중지됨";
        cleanup(); super.onDestroy();
    }
}
