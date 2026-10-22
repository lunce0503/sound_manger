package com.lunce.soundmanager;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.media.projection.MediaProjectionManager;
import android.os.*;
import android.view.*;
import android.widget.*;
import org.json.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.IntConsumer;

public final class MainActivity extends Activity {
    private final ScheduledExecutorService io = Executors.newSingleThreadScheduledExecutor();
    private final Map<String, SourceCard> cards = new LinkedHashMap<>();
    private EditText address, key;
    private TextView status, captureStatus;
    private LinearLayout root, sources;
    private Slider volume, bass, treble, presence;
    private JSONObject mix = new JSONObject();
    private volatile Api api;
    private volatile boolean visible;
    private volatile int revision;
    private boolean binding;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        ScrollView scroll = new ScrollView(this);
        root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(28, 28, 28, 36); root.setBackgroundColor(Color.rgb(16,24,39));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
            if (Build.VERSION.SDK_INT >= 30) { android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars()); top = bars.top; bottom = bars.bottom; }
            else { top = insets.getSystemWindowInsetTop(); bottom = insets.getSystemWindowInsetBottom(); }
            view.setPadding(28, top+28, 28, bottom+36); return insets;
        });
        scroll.addView(root); setContentView(scroll);
        text(root, "SOUND MANAGER", 15).setTextColor(Color.rgb(110,231,208));
        text(root, "소리를 모으고, 공간을 조정하세요", 25);
        text(root, "Windows 서버와 같은 Wi-Fi에 연결하세요.", 14);
        address = input("서버 주소", getPreferences(0).getString("address", "http://192.168.0.10:17842"));
        key = input("서버에 표시된 연결 키", "");
        key.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        button(root, "서버 연결 / 상태 새로고침", this::connect);
        status = text(root, "서버 연결 전", 14);
        text(root, "총합 음원", 21);
        volume = new Slider(root, "전체 음량 %", 100, 70, value -> updateMix("masterVolume", value/100d));
        bass = new Slider(root, "저음 dB", 24, 12, value -> updateMix("bassDb", value-12));
        treble = new Slider(root, "고음 dB", 24, 12, value -> updateMix("trebleDb", value-12));
        presence = new Slider(root, "명료도 dB", 24, 12, value -> updateMix("presenceDb", value-12));
        String[] labels = {"기본", "저음 강조", "고음 강조", "부드럽게", "선명하게"};
        int[][] presets = {{0,0,0}, {6,0,0}, {0,6,0}, {2,-4,-2}, {-2,3,4}};
        for (int i=0; i<labels.length; i++) {
            final int index = i;
            button(root, labels[i], () -> {
                try {
                    mix.put("bassDb", presets[index][0]).put("trebleDb", presets[index][1]).put("presenceDb", presets[index][2]);
                    showMix(mix); sendMix();
                } catch (JSONException error) { showError(error); }
            });
        }
        text(root, "음원별 방향 · 360°", 21);
        text(root, "앞 0° · 오른쪽 90° · 뒤 180° · 왼쪽 270°\n가상 방향을 켜면 스테레오 음원을 모노로 합쳐 배치합니다.", 14);
        sources = new LinearLayout(this); sources.setOrientation(LinearLayout.VERTICAL); root.addView(sources);
        text(root, "이 휴대폰의 재생음 전송", 21);
        text(root, "캡처를 허용한 앱의 재생음만 전송합니다. 캡처한 PCM의 음량·EQ·샘플레이트를 변경하지 않습니다.", 14);
        button(root, "재생음 전송 시작", this::requestCapture);
        button(root, "재생음 전송 중지", () -> stopService(new Intent(this, CaptureService.class)));
        captureStatus = text(root, CaptureService.status, 14);
        io.scheduleWithFixedDelay(() -> {
            Api current = api; int version = revision;
            if (!visible) return;
            runOnUiThread(() -> captureStatus.setText(CaptureService.active ? CaptureService.status : "전송 중지 · " + CaptureService.status));
            if (current == null) return;
            try {
                JSONObject state = current.call("GET", "/v1/state", null);
                runOnUiThread(() -> { if (api == current && revision == version) showState(state); });
            } catch (Exception error) { runOnUiThread(() -> { if (api == current) showError(error); }); }
        }, 0, 2, TimeUnit.SECONDS);
    }

    private EditText input(String label, String value) {
        text(root, label, 14); EditText edit = new EditText(this); edit.setSingleLine(true); edit.setText(value);
        root.addView(edit); return edit;
    }

    private TextView text(LinearLayout parent, String value, int size) {
        TextView view = new TextView(this); view.setText(value); view.setTextSize(size); view.setTextColor(Color.rgb(237,243,255));
        view.setPadding(0, 14, 0, 10); parent.addView(view); return view;
    }

    private void button(LinearLayout parent, String label, Runnable action) {
        Button button = new Button(this); button.setText(label); parent.addView(button);
        button.setOnClickListener(view -> action.run());
    }

    private void connect() {
        try {
            Api candidate = new Api(address.getText().toString(), key.getText().toString());
            int version = ++revision; api = null; status.setText("연결 중…");
            io.execute(() -> {
                try {
                    JSONObject state = candidate.call("GET", "/v1/state", null);
                    runOnUiThread(() -> { if (revision == version) { api = candidate; showState(state); } });
                } catch (Exception error) { runOnUiThread(() -> { if (revision == version) showError(error); }); }
            });
            getPreferences(0).edit().putString("address", candidate.address).apply();
        } catch (Exception error) { showError(error); }
    }

    private void showState(JSONObject state) {
        try {
            mix = state.getJSONObject("settings"); showMix(mix);
            JSONArray list = state.getJSONArray("sources"); Set<String> ids = new LinkedHashSet<>();
            for (int i=0; i<list.length(); i++) ids.add(list.getJSONObject(i).getString("id"));
            if (!cards.keySet().equals(ids)) {
                sources.removeAllViews(); cards.clear();
                for (int i=0; i<list.length(); i++) {
                    JSONObject source = list.getJSONObject(i);
                    cards.put(source.getString("id"), new SourceCard(source));
                }
            }
            for (int i=0; i<list.length(); i++) {
                JSONObject source = list.getJSONObject(i); cards.get(source.getString("id")).update(source);
            }
            status.setText(getString(R.string.connected_status, list.length(), state.optLong("clippedSamples")));
        } catch (Exception error) { showError(error); }
    }

    private void showMix(JSONObject values) {
        binding = true;
        volume.set((int)Math.round(values.optDouble("masterVolume", 0.7)*100));
        bass.set(values.optInt("bassDb")+12); treble.set(values.optInt("trebleDb")+12); presence.set(values.optInt("presenceDb")+12);
        binding = false;
    }

    private void updateMix(String field, double value) {
        if (binding) return;
        try { mix.put(field, value); sendMix(); } catch (Exception error) { showError(error); }
    }

    private void sendMix() { send("/v1/mix", mix.toString()); }

    private void send(String path, String json) {
        Api current = api;
        if (current == null) { status.setText("먼저 서버에 연결하세요."); return; }
        revision++;
        io.execute(() -> {
            try { current.call("PUT", path, new JSONObject(json)); }
            catch (Exception error) { runOnUiThread(() -> { if (api == current) showError(error); }); }
        });
    }

    private void requestCapture() {
        if (api == null) { status.setText("먼저 서버에 연결하세요."); return; }
        if (CaptureService.active) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            if (Build.VERSION.SDK_INT >= 33) requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO, Manifest.permission.POST_NOTIFICATIONS}, 10);
            else requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, 10);
            return;
        }
        startActivityForResult(getSystemService(MediaProjectionManager.class).createScreenCaptureIntent(), 20);
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request, permissions, grants);
        if (request == 10 && grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) requestCapture();
        else status.setText("재생음 캡처에는 오디오 녹음 권한이 필요합니다.");
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == 20 && result == RESULT_OK && data != null && api != null) {
            startForegroundService(new Intent(this, CaptureService.class).putExtra("permission", data)
                .putExtra("address", api.address).putExtra("key", api.key));
        }
    }

    private void showError(Exception error) { status.setText(getString(R.string.error_status, error.getMessage())); }
    @Override protected void onStart() { super.onStart(); visible = true; }
    @Override protected void onStop() { visible = false; super.onStop(); }
    @Override protected void onDestroy() { io.shutdownNow(); super.onDestroy(); }

    private final class Slider {
        final SeekBar seek; final TextView label;
        final String title;
        Slider(LinearLayout parent, String title, int max, int value, IntConsumer changed) {
            this.title = title; label = text(parent, title, 14); seek = new SeekBar(MainActivity.this);
            seek.setMax(max); seek.setProgress(value); parent.addView(seek);
            seek.setContentDescription(title);
            seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
                public void onProgressChanged(SeekBar bar, int v, boolean user) { label.setText(getString(R.string.slider_value, title, title.endsWith("dB") ? v-12 : v)); }
                public void onStartTrackingTouch(SeekBar bar) { }
                public void onStopTrackingTouch(SeekBar bar) { changed.accept(bar.getProgress()); }
            });
            set(value);
        }
        void set(int value) { if (!seek.isPressed()) { seek.setProgress(value); label.setText(getString(R.string.slider_value, title, title.endsWith("dB") ? value-12 : value)); } }
    }

    private final class SourceCard {
        final String id;
        final TextView label;
        final CheckBox spatial, muted;
        final Slider angle, gain;
        final DirectionView circle;
        boolean updating;

        SourceCard(JSONObject source) throws JSONException {
            id = source.getString("id");
            LinearLayout box = new LinearLayout(MainActivity.this); box.setOrientation(LinearLayout.VERTICAL);
            box.setPadding(12,12,12,24); sources.addView(box);
            label = text(box, source.getString("name"), 18);
            spatial = new CheckBox(MainActivity.this); spatial.setText("가상 방향 사용"); box.addView(spatial);
            muted = new CheckBox(MainActivity.this); muted.setText("음소거"); box.addView(muted);
            angle = new Slider(box, "방향 °", 359, 0, value -> { circleAngle(value); save(); });
            gain = new Slider(box, "음원 음량 %", 200, 100, value -> save());
            circle = new DirectionView(MainActivity.this, 0, value -> { angle.set(value); save(); });
            box.addView(circle, new LinearLayout.LayoutParams(-1, (int)(230*getResources().getDisplayMetrics().density)));
            spatial.setOnCheckedChangeListener((button, checked) -> save()); muted.setOnCheckedChangeListener((button, checked) -> save());
        }
        void circleAngle(int value) { circle.setAzimuth(value); }
        void update(JSONObject source) throws JSONException {
            updating = true; JSONObject settings = source.getJSONObject("settings");
            label.setText(getString(R.string.source_buffer, source.getString("name"), source.optInt("bufferedMs")));
            if (!angle.seek.isPressed() && !circle.isPressed()) {
                angle.set(settings.optInt("azimuth")); circle.setAzimuth(settings.optInt("azimuth"));
            }
            gain.set((int)Math.round(settings.optDouble("volume",1)*100));
            spatial.setChecked(settings.optBoolean("spatial")); muted.setChecked(settings.optBoolean("muted"));
            updating = false;
        }
        void save() {
            if (updating) return;
            try {
                send("/v1/sources/" + id, new JSONObject().put("volume", gain.seek.getProgress()/100d)
                    .put("azimuth", angle.seek.getProgress()).put("spatial", spatial.isChecked()).put("muted", muted.isChecked()).toString());
            } catch (Exception error) { showError(error); }
        }
    }
}
