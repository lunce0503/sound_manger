package com.lunce.soundmanager;

import android.Manifest;
import android.app.*;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.media.projection.MediaProjectionManager;
import android.os.*;
import android.text.InputType;
import android.view.*;
import android.widget.*;
import org.json.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.IntConsumer;

public final class MainActivity extends Activity {
    private final ScheduledExecutorService io=Executors.newSingleThreadScheduledExecutor();
    private final Map<String,SourceCard> cards=new LinkedHashMap<>();
    private LinearLayout root,sources;
    private TextView status,connectionHint,captureStatus,deviceCount,eqSummary;
    private Button captureButton;
    private Slider volume;
    private JSONObject mix=new JSONObject();
    private ConnectionStore connectionStore;
    private volatile Api api;
    private volatile boolean visible,needsSave,authRejected;
    private boolean connected;
    private volatile int revision,session;
    private Dialog directionDialog;
    private String directionSourceId;
    private final ExecutorService updateWorker=Executors.newSingleThreadExecutor();
    private Dialog updateDialog;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        ScrollView scroll=new ScrollView(this); scroll.setFillViewport(true);
        root=Ui.column(this); int pad=Ui.dp(this,20); root.setPadding(pad,pad,pad,pad); root.setBackgroundColor(Ui.BG);
        root.setOnApplyWindowInsetsListener((view,insets)->{
            int top,bottom;
            if (Build.VERSION.SDK_INT>=30) { android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars()); top=bars.top; bottom=bars.bottom; }
            else { top=insets.getSystemWindowInsetTop(); bottom=insets.getSystemWindowInsetBottom(); }
            view.setPadding(pad,top+pad,pad,bottom+pad); return insets;
        });
        scroll.addView(root); setContentView(scroll);
        Ui.text(root,"SOUND MANAGER",12,Ui.ACCENT); Ui.title(root,"내 소리 공간",28); Ui.space(root,12);
        LinearLayout connection=Ui.card(root);
        status=Ui.title(connection,"서버에 연결해주세요",18);
        connectionHint=Ui.text(connection,"한 번 연결하면 다음부터 자동으로 연결됩니다.",14,Ui.SUB);
        Ui.button(connection,"연결 관리",this::showConnection);
        LinearLayout master=Ui.card(root); Ui.title(master,"전체 음량",20);
        volume=new Slider(master,"전체 음량",100,70,0,"%",value->updateVolume(value));
        LinearLayout equalizer=Ui.card(root); Ui.title(equalizer,"이퀄라이저",18);
        eqSummary=Ui.text(equalizer,"기본",14,Ui.SUB); Ui.button(equalizer,"이퀄라이저 설정  ›",this::showEqualizer);
        deviceCount=Ui.title(root,"연결된 기기",20);
        sources=Ui.column(this); root.addView(sources); showEmpty();
        LinearLayout capture=Ui.card(root); Ui.title(capture,"이 휴대폰의 소리",18);
        captureStatus=Ui.text(capture,"휴대폰에서 재생하는 소리를 서버로 보냅니다.",14,Ui.SUB);
        captureButton=Ui.button(capture,"소리 전송 시작",()->{
            if (CaptureService.active) stopService(new Intent(this,CaptureService.class)); else requestCapture();
        });
        Ui.text(root,"Sound Manager "+BuildConfig.VERSION_NAME,12,Ui.SUB);
        Ui.button(root,"앱 업데이트",this::showUpdates);
        AppUpdates.schedule(this);
        final int restoreSession=session;
        io.execute(()->{
            int generation=restoreSession;
            try {
                PairingLink pair=store().load();
                runOnUiThread(()->{ if (session==generation && !isDestroyed() && pair!=null) connect(pair,false); });
            } catch (Exception error) { runOnUiThread(()->{ if (session==generation) connectionHint.setText("저장된 연결을 읽지 못했습니다. 연결 관리에서 다시 연결해주세요."); }); }
        });
        io.scheduleWithFixedDelay(this::poll,1,2,TimeUnit.SECONDS);
        if (getIntent().getData()!=null) { session++; confirmPairing(getIntent().getData().toString()); }
    }

    private ConnectionStore store() throws Exception {
        if (connectionStore==null) connectionStore=new ConnectionStore(this);
        return connectionStore;
    }

    private void poll() {
        if (!visible) return;
        runOnUiThread(()->{
            captureStatus.setText(CaptureService.active?"휴대폰 소리를 전송하고 있습니다.":"휴대폰에서 재생하는 소리를 서버로 보냅니다.");
            captureButton.setText(CaptureService.active?"소리 전송 중지":"소리 전송 시작");
        });
        Api current=api; int version=revision;
        if (current==null || authRejected) return;
        try {
            JSONObject state=current.call("GET","/v1/state",null);
            if (api!=current) return;
            if (needsSave) { store().save(new PairingLink(current.address,current.key)); needsSave=false; }
            runOnUiThread(()->{ if (api==current && revision==version && !isDestroyed()) showState(state); });
        } catch (Exception error) { runOnUiThread(()->{ if (api==current) connectionFailed(error); }); }
    }

    private void connect(PairingLink pair,boolean save) {
        Api candidate=new Api(pair.address,pair.key); api=candidate; needsSave=save; authRejected=false;
        connected=false; int generation=++session; revision++;
        status.setText("서버에 연결 중…"); connectionHint.setText(save?"연결 정보를 확인하고 있습니다.":"저장된 인증으로 연결하고 있습니다.");
        io.execute(()->{
            try {
                JSONObject state=candidate.call("GET","/v1/state",null);
                if (session!=generation) return;
                if (save) { store().save(pair); needsSave=false; }
                runOnUiThread(()->{ if (session==generation && !isDestroyed()) showState(state); });
            } catch (Exception error) { runOnUiThread(()->{ if (session==generation) connectionFailed(error); }); }
        });
    }

    private void connectionFailed(Exception error) {
        if (isDestroyed()) return;
        connected=false;
        authRejected=error instanceof Api.Failure && ((Api.Failure)error).status==401;
        status.setText(authRejected?"연결 인증을 확인해주세요":"서버 연결 대기 중");
        connectionHint.setText(authRejected?"연결 관리에서 서버를 다시 연결할 수 있습니다.":"인증 정보는 유지됩니다. 서버가 돌아오면 자동으로 연결합니다.");
    }

    private void showConnection() {
        LinearLayout content=Ui.column(this); int pad=Ui.dp(this,20); content.setPadding(pad,pad,pad,pad);
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("서버 연결").setView(content).setNegativeButton("닫기",null).create();
        Ui.text(content,"QR로 한 번 인증하면 다음 실행부터 자동으로 연결됩니다.",14,Ui.SUB);
        Ui.button(content,"QR로 서버 연결",()->{ dialog.dismiss(); startActivityForResult(new Intent(this,QrScanActivity.class),30); });
        Ui.button(content,"연결 코드 붙여넣기",()->{
            android.content.ClipboardManager clipboard=getSystemService(android.content.ClipboardManager.class);
            if (clipboard.hasPrimaryClip() && clipboard.getPrimaryClip().getItemCount()>0) {
                String code=clipboard.getPrimaryClip().getItemAt(0).coerceToText(this).toString(); dialog.dismiss(); confirmPairing(code);
            } else Toast.makeText(this,"복사한 연결 코드가 없습니다.",Toast.LENGTH_SHORT).show();
        });
        if (api!=null) {
            Ui.button(content,"저장된 인증으로 다시 연결",()->{ PairingLink pair=new PairingLink(api.address,api.key); dialog.dismiss(); connect(pair,false); });
            Ui.button(content,"저장된 연결 지우기",()->{ dialog.dismiss(); forgetConnection(); });
        }
        LinearLayout manual=Ui.column(this); manual.setVisibility(View.GONE);
        Ui.button(content,"주소와 키로 연결",()->manual.setVisibility(View.VISIBLE)); content.addView(manual);
        Ui.text(manual,"서버 주소",14,Ui.SUB); EditText address=new EditText(this); address.setSingleLine(true);
        address.setText(api==null?"":api.address); address.setHint("http://서버IP:17842"); manual.addView(address);
        Ui.text(manual,"연결 키",14,Ui.SUB); EditText key=new EditText(this); key.setSingleLine(true); key.setSaveEnabled(false);
        key.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD); manual.addView(key);
        Ui.button(manual,"연결",()->{
            try { PairingLink pair=new PairingLink(address.getText().toString(),key.getText().toString().trim()); dialog.dismiss(); switchServer(pair); }
            catch (Exception error) { Toast.makeText(this,"서버 주소와 연결 키를 확인해주세요.",Toast.LENGTH_LONG).show(); }
        });
        dialog.show();
    }

    private void confirmPairing(String text) {
        try {
            PairingLink pair=PairingLink.parse(text);
            new AlertDialog.Builder(this).setTitle("이 서버에 연결할까요?").setMessage(pair.address)
                .setNegativeButton("취소",null).setPositiveButton("연결",(d,w)->switchServer(pair)).show();
        } catch (Exception error) { Toast.makeText(this,"올바른 Sound Manager 연결 코드를 사용해주세요.",Toast.LENGTH_LONG).show(); }
    }

    private void switchServer(PairingLink pair) {
        if (CaptureService.active) stopService(new Intent(this,CaptureService.class));
        cards.clear(); sources.removeAllViews(); showEmpty(); connect(pair,true);
    }

    private void forgetConnection() {
        session++; revision++; api=null; connected=false; needsSave=false; authRejected=false;
        stopService(new Intent(this,CaptureService.class)); cards.clear(); sources.removeAllViews(); showEmpty();
        status.setText("서버에 연결해주세요"); connectionHint.setText("연결 관리에서 새로운 서버를 연결할 수 있습니다."); deviceCount.setText("연결된 기기");
        io.execute(()->{
            try { store().clear(); }
            catch (Exception error) { runOnUiThread(()->Toast.makeText(this,"저장된 연결 삭제에 실패했습니다.",Toast.LENGTH_LONG).show()); }
        });
    }

    private void showEmpty() { Ui.text(sources,"연결된 기기를 기다리고 있습니다.",15,Ui.SUB); }

    void showState(JSONObject state) {
        try {
            connected=true; authRejected=false; status.setText("서버에 연결됨"); connectionHint.setText("인증을 다시 하지 않아도 자동으로 연결됩니다.");
            mix=state.getJSONObject("settings"); volume.set((int)Math.round(mix.optDouble("masterVolume",0.7)*100));
            eqSummary.setText(eqName(mix));
            JSONArray list=state.getJSONArray("sources"); Set<String> ids=new LinkedHashSet<>();
            for (int i=0;i<list.length();i++) ids.add(list.getJSONObject(i).getString("id"));
            deviceCount.setText(getString(R.string.device_count,list.length()));
            if (directionDialog!=null && !ids.contains(directionSourceId)) directionDialog.dismiss();
            if (!cards.keySet().equals(ids)) {
                sources.removeAllViews(); cards.clear();
                for (int i=0;i<list.length();i++) { JSONObject source=list.getJSONObject(i); cards.put(source.getString("id"),new SourceCard(source)); }
                if (list.length()==0) showEmpty();
            }
            for (int i=0;i<list.length();i++) { JSONObject source=list.getJSONObject(i); cards.get(source.getString("id")).update(source); }
        } catch (Exception error) { Toast.makeText(this,"서버 상태를 읽지 못했습니다.",Toast.LENGTH_SHORT).show(); }
    }

    private String eqName(JSONObject values) {
        int b=values.optInt("bassDb"),t=values.optInt("trebleDb"),p=values.optInt("presenceDb");
        if (b==0 && t==0 && p==0) return "기본";
        if (b==6 && t==0 && p==0) return "저음 강조";
        if (b==0 && t==6 && p==0) return "고음 강조";
        if (b==2 && t==-4 && p==-2) return "부드럽게";
        if (b==-2 && t==3 && p==4) return "선명하게";
        return "사용자 설정";
    }

    private Dialog settingsDialog(String title,LinearLayout content) {
        Dialog dialog=new Dialog(this); dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        content.setPadding(Ui.dp(this,24),Ui.dp(this,20),Ui.dp(this,24),Ui.dp(this,20));
        content.setBackground(Ui.background(this,Ui.PANEL,24)); dialog.setContentView(content);
        Ui.title(content,title,22); dialog.getWindow().setBackgroundDrawableResource(android.R.color.transparent);
        return dialog;
    }
    private void showDialog(Dialog dialog) {
        dialog.show(); dialog.getWindow().setLayout(getResources().getDisplayMetrics().widthPixels-Ui.dp(this,32),-2);
    }

    private void showEqualizer() {
        if (!connected) { showConnectHint(); return; }
        LinearLayout content=Ui.column(this); Dialog dialog=settingsDialog("이퀄라이저",content);
        LinearLayout body=Ui.column(this); ScrollView scroll=new ScrollView(this); scroll.addView(body);
        content.addView(scroll,new LinearLayout.LayoutParams(-1,Math.min(Ui.dp(this,440),getResources().getDisplayMetrics().heightPixels/2)));
        Slider bass=new Slider(body,"저음",24,mix.optInt("bassDb")+12,-12,"dB",v->{});
        Slider treble=new Slider(body,"고음",24,mix.optInt("trebleDb")+12,-12,"dB",v->{});
        Slider presence=new Slider(body,"명료도",24,mix.optInt("presenceDb")+12,-12,"dB",v->{});
        String[] labels={"기본","저음 강조","고음 강조","부드럽게","선명하게"}; int[][] presets={{0,0,0},{6,0,0},{0,6,0},{2,-4,-2},{-2,3,4}};
        for (int i=0;i<labels.length;i++) { final int index=i; Ui.button(body,labels[i],()->{ bass.set(presets[index][0]+12); treble.set(presets[index][1]+12); presence.set(presets[index][2]+12); }); }
        Ui.button(content,"적용",()->{
            try { mix.put("bassDb",bass.value()-12).put("trebleDb",treble.value()-12).put("presenceDb",presence.value()-12); eqSummary.setText(eqName(mix)); send("/v1/mix",mix.toString()); dialog.dismiss(); }
            catch (JSONException ignored) { }
        });
        Ui.button(content,"취소",dialog::dismiss); showDialog(dialog);
    }

    private void updateVolume(int value) {
        if (!connected) { showConnectHint(); return; }
        try { mix.put("masterVolume",value/100d); send("/v1/mix",mix.toString()); }
        catch (JSONException ignored) { }
    }
    private void showConnectHint() { Toast.makeText(this,"서버에 연결한 뒤 조정할 수 있습니다.",Toast.LENGTH_SHORT).show(); }
    private void send(String path,String json) {
        send("PUT",path,json);
    }
    private void send(String method,String path,String json) {
        Api current=api; if (current==null || !connected) { showConnectHint(); return; }
        revision++;
        io.execute(()->{
            try { current.call(method,path,new JSONObject(json)); }
            catch (Exception error) { runOnUiThread(()->{ if (api==current) connectionFailed(error); }); }
        });
    }

    private void requestCapture() {
        if (!connected || api==null) { showConnectHint(); return; }
        if (CaptureService.active) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=PackageManager.PERMISSION_GRANTED) {
            if (Build.VERSION.SDK_INT>=33) requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO,Manifest.permission.POST_NOTIFICATIONS},10);
            else requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO},10); return;
        }
        startActivityForResult(getSystemService(MediaProjectionManager.class).createScreenCaptureIntent(),20);
    }
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] grants) {
        super.onRequestPermissionsResult(request,permissions,grants);
        if (request==10 && grants.length>0 && grants[0]==PackageManager.PERMISSION_GRANTED) requestCapture();
        else Toast.makeText(this,"소리 전송에는 오디오 권한이 필요합니다.",Toast.LENGTH_LONG).show();
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);
        if (request==30 && result==RESULT_OK && data!=null) { confirmPairing(data.getStringExtra("pairing")); return; }
        if (request==20 && result==RESULT_OK && data!=null && api!=null) startForegroundService(new Intent(this,CaptureService.class)
            .putExtra("permission",data).putExtra("address",api.address).putExtra("key",api.key));
    }
    @Override protected void onStart() {
        super.onStart(); visible=true;
        if(AppUpdates.automatic(this))updateWorker.execute(()->AppUpdates.check(getApplicationContext(),false));
        if(getIntent().getBooleanExtra("updates",false)){getIntent().removeExtra("updates");showUpdates();}
    }
    @Override protected void onStop() { visible=false; super.onStop(); }
    @Override protected void onDestroy() { session++; io.shutdownNow();updateWorker.shutdownNow(); if (directionDialog!=null) directionDialog.dismiss();if(updateDialog!=null)updateDialog.dismiss(); super.onDestroy(); }

    private void showUpdates() {
        LinearLayout content=Ui.column(this);int pad=Ui.dp(this,20);content.setPadding(pad,pad,pad,pad);
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("앱 업데이트 · "+BuildConfig.VERSION_NAME).setView(content).setNegativeButton("닫기",null).create();
        updateDialog=dialog;
        TextView details=Ui.text(content,AppUpdates.status(this),14,Ui.SUB);
        Switch automatic=new Switch(this);automatic.setText("새 버전 자동 확인");automatic.setChecked(AppUpdates.automatic(this));Ui.toggleStyle(automatic);content.addView(automatic);
        Switch wifi=new Switch(this);wifi.setText(R.string.update_wifi);wifi.setChecked(AppUpdates.preferences(this).getBoolean("wifi",true));Ui.toggleStyle(wifi);content.addView(wifi);
        automatic.setOnCheckedChangeListener((v,checked)->AppUpdates.configure(this,checked,wifi.isChecked()));
        wifi.setOnCheckedChangeListener((v,checked)->AppUpdates.configure(this,automatic.isChecked(),checked));
        Ui.text(content,"설치할 때 Android 시스템 확인이 필요합니다. 연결 인증과 기기 설정은 유지됩니다.",14,Ui.SUB);
        Button install=Ui.button(content,"다운로드한 업데이트 설치",()->{
            Runnable action=()->{
                installUpdate(details);
            };
            if(CaptureService.active)new AlertDialog.Builder(this).setTitle("업데이트 설치").setMessage("현재 소리 전송을 중지한 뒤 설치합니다. 설치 후 소리 전송은 다시 시작해주세요.")
                .setNegativeButton("취소",null).setPositiveButton("전송 중지 후 설치",(d,w)->{stopService(new Intent(this,CaptureService.class));action.run();}).show();
            else action.run();
        });
        install.setEnabled(AppUpdates.ready(this));
        Button check=Ui.button(content,"지금 확인 및 다운로드",()->{
            details.setText("새 버전 확인 및 다운로드 중…");
            updateWorker.execute(()->{
                String result=AppUpdates.check(getApplicationContext(),true);
                runOnUiThread(()->{if(!isDestroyed()&&dialog.isShowing()){details.setText(result);install.setEnabled(AppUpdates.ready(this));}});
            });
        });
        check.setContentDescription("모바일 데이터에서도 업데이트 확인 및 다운로드");
        dialog.setOnDismissListener(d->updateDialog=null);dialog.show();
    }
    private void installUpdate(TextView details) {
        details.setText("설치 파일을 검증하고 있습니다…");
        updateWorker.execute(()->{
            try {
                AppUpdates.validateReady(getApplicationContext());
                runOnUiThread(()->{
                    if(!isDestroyed())try {boolean allowed=getPackageManager().canRequestPackageInstalls();AppUpdates.install(this);details.setText(allowed?"Android 시스템 설치 화면에서 확인하세요.":"이 앱의 설치 권한을 허용한 뒤 설치 버튼을 다시 누르세요.");}
                    catch(Exception error){details.setText("시스템 설치 화면을 열지 못했습니다.");}
                });
            }catch(Exception error){runOnUiThread(()->{if(!isDestroyed())details.setText("설치 파일 검증에 실패했습니다. 지금 확인으로 다시 다운로드하세요.");});}
        });
    }

    private final class Slider {
        final SeekBar seek; final TextView valueText; final int offset; final String unit;
        Slider(LinearLayout parent,String title,int max,int value,int offset,String unit,IntConsumer changed) {
            this.offset=offset; this.unit=unit; LinearLayout labels=Ui.row(MainActivity.this); parent.addView(labels);
            TextView name=Ui.text(labels,title,14,Ui.SUB); name.setLayoutParams(new LinearLayout.LayoutParams(0,-2,1));
            valueText=Ui.title(labels,"",18); seek=new SeekBar(MainActivity.this); seek.setMax(max);
            seek.setProgressTintList(android.content.res.ColorStateList.valueOf(Ui.ACCENT)); seek.setThumbTintList(android.content.res.ColorStateList.valueOf(Ui.ACCENT));
            seek.setContentDescription(title); parent.addView(seek,new LinearLayout.LayoutParams(-1,Ui.dp(MainActivity.this,48)));
            seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
                public void onProgressChanged(SeekBar bar,int v,boolean user) { display(v); if (user && !bar.isPressed()) changed.accept(v); }
                public void onStartTrackingTouch(SeekBar bar) { }
                public void onStopTrackingTouch(SeekBar bar) { changed.accept(bar.getProgress()); }
            }); set(value);
        }
        private void display(int v) { valueText.setText(getString(R.string.control_value,v+offset,unit)); }
        void set(int value) { if (!seek.isPressed()) { seek.setProgress(value); display(value); } }
        int value() { return seek.getProgress(); }
    }

    private final class SourceCard {
        final String id; final TextView name; final Switch muted;
        JSONObject settings=new JSONObject(); String sourceName; boolean updating;
        SourceCard(JSONObject source) throws JSONException {
            id=source.getString("id"); LinearLayout card=Ui.card(sources); int pad=Ui.dp(MainActivity.this,12); card.setPadding(pad,pad,pad,pad);
            LinearLayout row=Ui.row(MainActivity.this); card.addView(row);
            name=Ui.title(row,source.getString("name"),17); name.setSingleLine(true); name.setEllipsize(android.text.TextUtils.TruncateAt.END);
            name.setLayoutParams(new LinearLayout.LayoutParams(0,-2,1));
            muted=new Switch(MainActivity.this); muted.setText("음소거"); Ui.toggleStyle(muted); row.addView(muted,new LinearLayout.LayoutParams(-2,Ui.dp(MainActivity.this,56)));
            muted.setCompoundDrawablePadding(Ui.dp(MainActivity.this,10));
            muted.setOnCheckedChangeListener((view,checked)->{ updateMuteIcon(); if (!updating) { try { settings.put("muted",checked); patch(new JSONObject().put("muted",checked)); } catch (JSONException ignored) { } } });
            ImageButton menu=new ImageButton(MainActivity.this); menu.setImageResource(R.drawable.ic_more_vert); menu.setBackgroundColor(Color.TRANSPARENT);
            menu.setContentDescription("기기 설정"); row.addView(menu,new LinearLayout.LayoutParams(Ui.dp(MainActivity.this,48),Ui.dp(MainActivity.this,48)));
            menu.setOnClickListener(v->{
                PopupMenu popup=new PopupMenu(MainActivity.this,menu);
                popup.getMenu().add(0,1,0,"음량 조절"); popup.getMenu().add(0,2,1,"이름 변경"); popup.getMenu().add(0,3,2,"360° 방향 설정");
                popup.setOnMenuItemClickListener(item->{ if (item.getItemId()==1) showVolume(); else if (item.getItemId()==2) showRename(); else showDirection(); return true; });
                popup.show();
            });
            updateMuteIcon();
        }
        private void updateMuteIcon() {
            muted.setCompoundDrawablesRelativeWithIntrinsicBounds(muted.isChecked()?R.drawable.ic_volume_off:R.drawable.ic_volume_on,0,0,0);
            muted.setTextColor(muted.isChecked()?Ui.RED:Ui.TEXT);
            if (Build.VERSION.SDK_INT>=30) muted.setStateDescription(muted.isChecked()?"음소거 켜짐":"음소거 꺼짐");
        }
        void update(JSONObject source) throws JSONException {
            updating=true; sourceName=source.getString("name"); name.setText(sourceName); settings=source.getJSONObject("settings");
            muted.setChecked(settings.optBoolean("muted")); updateMuteIcon(); updating=false;
        }
        private void patch(JSONObject values) { send("PATCH","/v1/sources/"+id,values.toString()); }
        private void showVolume() {
            if (!connected) { showConnectHint(); return; }
            LinearLayout content=Ui.column(MainActivity.this); Dialog dialog=settingsDialog("음량 조절",content);
            Ui.text(content,sourceName,14,Ui.SUB);
            Slider gain=new Slider(content,"기기 음량",200,(int)Math.round(settings.optDouble("volume",1)*100),0,"%",v->{});
            Ui.button(content,"적용",()->{ try { patch(new JSONObject().put("volume",gain.value()/100d)); dialog.dismiss(); } catch (JSONException ignored) {} });
            Ui.button(content,"취소",dialog::dismiss); showDialog(dialog);
        }
        private void showRename() {
            if (!connected) { showConnectHint(); return; }
            EditText input=new EditText(MainActivity.this); input.setSingleLine(true); input.setText(sourceName); input.selectAll();
            input.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(64)});
            AlertDialog dialog=new AlertDialog.Builder(MainActivity.this).setTitle("이름 변경").setView(input)
                .setNegativeButton("취소",null).setNeutralButton("원래 이름으로",(d,w)->{ try { patch(new JSONObject().put("resetName",true)); } catch (JSONException ignored) {} })
                .setPositiveButton("적용",null).create();
            dialog.show(); dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
                String value=input.getText().toString().trim();
                if (value.isEmpty() || value.chars().anyMatch(Character::isISOControl)) { input.setError("이름을 입력하세요."); return; }
                try { patch(new JSONObject().put("displayName",value)); dialog.dismiss(); } catch (JSONException ignored) {}
            });
        }
        private void showDirection() {
            if (!connected) { showConnectHint(); return; }
            LinearLayout content=Ui.column(MainActivity.this); Dialog dialog=settingsDialog("360° 방향 설정",content);
            Ui.text(content,sourceName,14,Ui.SUB); Switch spatial=new Switch(MainActivity.this); spatial.setText("가상 방향 사용"); Ui.toggleStyle(spatial); spatial.setChecked(settings.optBoolean("spatial")); content.addView(spatial);
            final DirectionView[] view={null};
            Slider angle=new Slider(content,"방향",359,settings.optInt("azimuth"),0,"°",value->{ if (view[0]!=null) view[0].setAzimuth(value); });
            view[0]=new DirectionView(MainActivity.this,angle.value(),angle::set);
            view[0].setMinimumHeight(Ui.dp(MainActivity.this,100));
            content.addView(view[0],new LinearLayout.LayoutParams(-1,0,1));
            Ui.text(content,"표시된 점을 잡아 방향을 움직이세요.",13,Ui.SUB);
            Ui.button(content,"적용",()->{
                try { patch(new JSONObject().put("azimuth",angle.value()).put("spatial",spatial.isChecked())); dialog.dismiss(); }
                catch (JSONException ignored) { }
            });
            Ui.button(content,"취소",dialog::dismiss);
            directionDialog=dialog; directionSourceId=id;
            dialog.setOnDismissListener(d->{ directionDialog=null; directionSourceId=null; }); showDialog(dialog);
            dialog.getWindow().setLayout(getResources().getDisplayMetrics().widthPixels-Ui.dp(MainActivity.this,32),getResources().getDisplayMetrics().heightPixels-Ui.dp(MainActivity.this,80));
        }
    }
}
