package com.lunce.soundmanager;

import android.app.*;
import android.app.job.*;
import android.content.*;
import android.content.pm.*;
import android.net.*;
import android.os.Build;
import android.provider.Settings;
import android.util.Base64;
import org.json.*;
import okhttp3.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.*;
import java.security.spec.X509EncodedKeySpec;
import java.util.*;
import java.util.concurrent.TimeUnit;

final class AppUpdates {
    static final String CERTIFICATE="7217017ad413a98cc79e74bf44a6d4e13c93852ca32ef538e75881db1cda7687";
    static final String PUBLIC_KEY="MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAskaoCbavX3ooeUsouK2U316SgiAfj1j7qdG1c+yKxgBt4jG97rGgKO0/YTc58BdjSBVZPKQ4zuYMiCi4Vh209fMsvCdm9DBS09ajKd5Mde061VR/RVC237zK+XbhQ92PYH6DAT2caxrQrI4d47QBWjCZtc3IBWkoghQZjXu3LY5mmd3AKw+ayyP+oX2jOme2jMIN7ZDNM3K6rHMusHX8bYLyA5d+YsNY1xbkv8ne8mXXor0sby/OTnI89f/7bagnR1YHZObgh6m8cSwe9pQ5+X1xHtuCV31UV9paEJlRBAk3LTKPQKuNvbtHqL/jl86ZFCbwqz/x+qTXtka1IWmeBR1ehYPE4mFMHPD8/J9HwBIBK74Buv50iOzvRIHGzfRkmo61CQKtMp0GOYrNe/RCLZeIFVA1JD6aOEMbHovt/sorvbLEfpRcUMfE2K6YIkI/dOxCFm8tuyPIDUnQcQ5L8/oEcfejUGWJGzMzO0oIXNJIw9Zzfq0bcz7ZDQGREoNnAgMBAAE=";
    private static final int JOB=2401;
    private static final long INTERVAL=6*60*60*1000L;
    private static final OkHttpClient HTTP=new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(15,TimeUnit.SECONDS).readTimeout(30,TimeUnit.SECONDS).callTimeout(10,TimeUnit.MINUTES).build();
    static SharedPreferences preferences(Context context) { return context.getSharedPreferences("app_updates",Context.MODE_PRIVATE); }
    static boolean automatic(Context context) { return preferences(context).getBoolean("automatic",true); }
    static File directory(Context context) { return new File(context.getFilesDir(),"updates"); }
    static File apk(Context context) { return new File(directory(context),"pending.apk"); }
    static String status(Context context) { return preferences(context).getString("status","현재 버전 "+BuildConfig.VERSION_NAME); }
    static void configure(Context context,boolean automatic,boolean wifi) {
        preferences(context).edit().putBoolean("automatic",automatic).putBoolean("wifi",wifi).apply(); schedule(context);
    }
    static void schedule(Context context) {
        SharedPreferences prefs=preferences(context);String prepared=prefs.getString("ready_version",null);
        if(prepared!=null&&validVersion(prepared)&&compare(prepared,BuildConfig.VERSION_NAME)<=0) {
            prefs.edit().remove("ready_version").putString("status","현재 버전 "+BuildConfig.VERSION_NAME).apply();
            context.getSystemService(NotificationManager.class).cancel(JOB);
        }
        JobScheduler scheduler=context.getSystemService(JobScheduler.class);
        if(!automatic(context)) { scheduler.cancel(JOB); return; }
        scheduler.schedule(new JobInfo.Builder(JOB,new ComponentName(context,UpdateJobService.class))
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_UNMETERED).setPeriodic(INTERVAL).setPersisted(true).build());
    }
    static boolean unmetered(Context context) {
        ConnectivityManager manager=context.getSystemService(ConnectivityManager.class);
        NetworkCapabilities capabilities=manager.getNetworkCapabilities(manager.getActiveNetwork());
        return capabilities!=null&&capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
    }
    static synchronized String check(Context context,boolean manual) {
        SharedPreferences prefs=preferences(context);
        if(!manual&&(CaptureService.active||!automatic(context)||System.currentTimeMillis()-prefs.getLong("checked",0)<INTERVAL)) return status(context);
        try {
            JSONArray releases=new JSONArray(new String(read("https://api.github.com/repos/lunce0503/sound_manger/releases?per_page=20",2*1024*1024),StandardCharsets.UTF_8));
            List<String> versions=new ArrayList<>();
            for(int i=0;i<releases.length();i++) {
                JSONObject release=releases.getJSONObject(i); String tag=release.optString("tag_name");
                if(release.optBoolean("draft")||!tag.startsWith("v"))continue;
                String version=tag.substring(1);
                if(validVersion(version)&&compare(version,BuildConfig.VERSION_NAME)>0) {
                    Set<String> names=new HashSet<>();JSONArray assets=release.getJSONArray("assets");
                    for(int a=0;a<assets.length();a++)names.add(assets.getJSONObject(a).getString("name"));
                    if(names.contains("update.json")&&names.contains("update.json.sig"))versions.add(version);
                }
            }
            versions.sort((a,b)->compare(b,a));
            if(versions.isEmpty()) { prefs.edit().putLong("checked",System.currentTimeMillis()).putString("status","현재 버전이 최신입니다.").apply();return status(context); }
            String version=versions.get(0);byte[] bytes=read(url(version,"update.json"),65536);
            String signature=new String(read(url(version,"update.json.sig"),4096),StandardCharsets.UTF_8);
            JSONObject manifest=verify(bytes,signature,PUBLIC_KEY);
            JSONObject asset=manifest.getJSONObject("android");
            if(!version.equals(manifest.getString("version"))||asset.getInt("versionCode")<=BuildConfig.VERSION_CODE)throw new SecurityException("Version mismatch");
            boolean download=manual||(prefs.getBoolean("wifi",true)&&unmetered(context));
            if(download) {
                File folder=directory(context);if(!folder.isDirectory()&&!folder.mkdirs())throw new IOException("Update directory unavailable");
                if(!validFile(apk(context),asset)) download(url(version,asset.getString("file")),apk(context),asset);
                validatePackage(context,apk(context),asset.getInt("versionCode"));
                Files.write(new File(folder,"manifest.json").toPath(),bytes);
                Files.write(new File(folder,"manifest.sig").toPath(),signature.getBytes(StandardCharsets.UTF_8));
                prefs.edit().putString("ready_version",version).putString("status",version+" 준비 완료 · 앱 업데이트에서 설치하세요.").apply();
                if(!manual)notifyReady(context);
            } else prefs.edit().putString("status",version+(prefs.getBoolean("wifi",true)?" 업데이트 · Wi-Fi 연결 시 자동 다운로드합니다.":" 업데이트 · 지금 확인으로 다운로드할 수 있습니다.")).apply();
            prefs.edit().putLong("checked",System.currentTimeMillis()).apply();
        } catch(Exception error) { prefs.edit().putString("status","업데이트 확인·다운로드에 실패했습니다. 나중에 다시 시도하세요.").apply(); }
        return status(context);
    }
    static JSONObject verify(byte[] bytes,String signature,String publicKey) throws Exception {
        if(bytes.length>65536||signature.length()>4096)throw new SecurityException("Manifest too large");
        java.security.Signature verifier=java.security.Signature.getInstance("SHA256withRSA");
        verifier.initVerify(KeyFactory.getInstance("RSA").generatePublic(new X509EncodedKeySpec(Base64.decode(publicKey,Base64.DEFAULT))));
        verifier.update(bytes);if(!verifier.verify(Base64.decode(signature.trim(),Base64.DEFAULT)))throw new SecurityException("Invalid update signature");
        JSONObject manifest=new JSONObject(new String(bytes,StandardCharsets.UTF_8));String version=manifest.getString("version");
        if(manifest.getInt("schema")!=1||!validVersion(version))throw new SecurityException("Unsupported manifest");
        validateAsset(manifest.getJSONObject("android"),"SoundManager-"+version+"-android.apk",128L*1024*1024);
        validateAsset(manifest.getJSONObject("windows"),"SoundManager-"+version+"-windows-x64.exe",512L*1024*1024);
        if(manifest.getJSONObject("android").getInt("versionCode")<=0)throw new SecurityException("Invalid version code");
        return manifest;
    }
    private static void validateAsset(JSONObject asset,String name,long limit) throws Exception {
        if(!asset.getString("file").equals(name)||!asset.getString("sha256").matches("[a-fA-F0-9]{64}")||asset.getLong("size")<=0||asset.getLong("size")>limit)throw new SecurityException("Invalid update asset");
    }
    static boolean validVersion(String version) {return version.matches("(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})");}
    static int compare(String a,String b) {
        if(!validVersion(a)||!validVersion(b))throw new IllegalArgumentException("Invalid version");
        String[] first=a.split("\\."),second=b.split("\\.");
        for(int i=0;i<3;i++){int value=Integer.compare(Integer.parseInt(first[i]),Integer.parseInt(second[i]));if(value!=0)return value;}return 0;
    }
    private static String url(String version,String file) {return "https://github.com/lunce0503/sound_manger/releases/download/v"+version+"/"+file;}
    static boolean allowed(HttpUrl url) {return url.isHttps()&&url.port()==443&&url.username().isEmpty()&&url.password().isEmpty()&&Set.of("github.com","api.github.com","release-assets.githubusercontent.com","objects.githubusercontent.com").contains(url.host());}
    private static Response get(String address) throws Exception {
        HttpUrl url=Objects.requireNonNull(HttpUrl.parse(address));
        for(int i=0;i<6;i++) {
            if(!allowed(url))throw new SecurityException("Untrusted update host");
            Response response=HTTP.newCall(new Request.Builder().url(url).header("User-Agent","SoundManager-Updater/1").build()).execute();
            if(response.isRedirect()) {
                String location=response.header("Location");response.close();url=location==null?null:url.resolve(location);
                if(url==null)throw new IOException("Invalid redirect");continue;
            }
            if(!response.isSuccessful()||response.body()==null){response.close();throw new IOException("Update request failed");}return response;
        }throw new IOException("Too many redirects");
    }
    private static byte[] read(String address,int limit) throws Exception {
        try(Response response=get(address);InputStream input=response.body().byteStream();ByteArrayOutputStream output=new ByteArrayOutputStream()) {
            byte[] buffer=new byte[8192];int count;while((count=input.read(buffer))!=-1){if(output.size()+count>limit)throw new IOException("Response too large");output.write(buffer,0,count);}return output.toByteArray();
        }
    }
    private static void download(String address,File target,JSONObject asset) throws Exception {
        File temporary=new File(target.getParentFile(),"pending.download");
        try {
            try(Response response=get(address);InputStream input=response.body().byteStream();OutputStream output=new FileOutputStream(temporary)) {
                long total=0,expected=asset.getLong("size");byte[] buffer=new byte[65536];int count;
                while((count=input.read(buffer))!=-1){total+=count;if(total>expected)throw new IOException("Oversized APK");output.write(buffer,0,count);}
            }
            if(!validFile(temporary,asset))throw new SecurityException("APK hash mismatch");
            Files.move(temporary.toPath(),target.toPath(),StandardCopyOption.REPLACE_EXISTING);
        } finally {Files.deleteIfExists(temporary.toPath());}
    }
    static boolean validFile(File file,JSONObject asset) throws Exception {
        if(!file.isFile()||file.length()!=asset.getLong("size"))return false;
        MessageDigest hash=MessageDigest.getInstance("SHA-256");try(InputStream input=new FileInputStream(file)){byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1)hash.update(buffer,0,count);}
        return hex(hash.digest()).equalsIgnoreCase(asset.getString("sha256"));
    }
    static String hex(byte[] bytes) {StringBuilder value=new StringBuilder();for(byte b:bytes)value.append(String.format(Locale.ROOT,"%02x",b&255));return value.toString();}
    static boolean ready(Context context) {
        try {
            File file=new File(directory(context),"manifest.json");if(file.length()>65536)return false;
            JSONObject manifest=new JSONObject(new String(Files.readAllBytes(file.toPath()),StandardCharsets.UTF_8));
            return compare(manifest.getString("version"),BuildConfig.VERSION_NAME)>0&&apk(context).isFile()&&new File(directory(context),"manifest.sig").isFile();
        }catch(Exception error){return false;}
    }
    static synchronized void validateReady(Context context) throws Exception {
        File folder=directory(context);File manifestFile=new File(folder,"manifest.json"),signatureFile=new File(folder,"manifest.sig");
        if(manifestFile.length()>65536||signatureFile.length()>4096)throw new SecurityException("Manifest too large");
        JSONObject manifest=verify(Files.readAllBytes(manifestFile.toPath()),new String(Files.readAllBytes(signatureFile.toPath()),StandardCharsets.UTF_8),PUBLIC_KEY);
        JSONObject asset=manifest.getJSONObject("android");
        if(compare(manifest.getString("version"),BuildConfig.VERSION_NAME)<=0||!validFile(apk(context),asset))throw new SecurityException("Invalid APK");
        validatePackage(context,apk(context),asset.getInt("versionCode"));
    }
    @SuppressWarnings("deprecation")
    private static void validatePackage(Context context,File file,int expectedCode) throws Exception {
        PackageInfo archive=context.getPackageManager().getPackageArchiveInfo(file.getAbsolutePath(),PackageManager.GET_SIGNING_CERTIFICATES);
        if(archive==null||!context.getPackageName().equals(archive.packageName)||archive.getLongVersionCode()!=expectedCode||expectedCode<=BuildConfig.VERSION_CODE||archive.signingInfo==null)throw new SecurityException("Wrong APK package/version");
        android.content.pm.Signature[] signatures=archive.signingInfo.getApkContentsSigners();
        if(signatures.length!=1||!hex(MessageDigest.getInstance("SHA-256").digest(signatures[0].toByteArray())).equals(CERTIFICATE))throw new SecurityException("Wrong APK signer");
    }
    static void install(Activity activity) {
        if(!activity.getPackageManager().canRequestPackageInstalls()) {
            activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+activity.getPackageName())));return;
        }
        Uri uri=Uri.parse("content://"+activity.getPackageName()+".updates/update.apk");
        activity.startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
    }
    private static void notifyReady(Context context) {
        if(Build.VERSION.SDK_INT>=33&&context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)return;
        NotificationManager manager=context.getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("updates","앱 업데이트",NotificationManager.IMPORTANCE_DEFAULT));
        PendingIntent open=PendingIntent.getActivity(context,JOB,new Intent(context,MainActivity.class).putExtra("updates",true),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        manager.notify(JOB,new Notification.Builder(context,"updates").setSmallIcon(R.drawable.ic_sound_manager).setContentTitle("Sound Manager 업데이트 준비 완료")
            .setContentText("눌러서 새 버전을 설치하세요.").setContentIntent(open).setAutoCancel(true).build());
    }
}
