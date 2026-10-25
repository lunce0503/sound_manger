package com.lunce.soundmanager;

import android.content.*;
import android.net.Uri;
import android.util.Base64;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.*;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=29)
public class AppUpdatesTest {
    private KeyPair key;private Context context;
    @Before public void prepare() throws Exception {
        KeyPairGenerator generator=KeyPairGenerator.getInstance("RSA");generator.initialize(2048);key=generator.generateKeyPair();
        context=RuntimeEnvironment.getApplication();context.getSharedPreferences("app_updates",Context.MODE_PRIVATE).edit().clear().commit();
    }
    private JSONObject manifest() throws Exception {
        return new JSONObject().put("schema",1).put("version","0.4.2")
            .put("windows",new JSONObject().put("file","SoundManager-0.4.2-windows-x64.exe").put("sha256","a".repeat(64)).put("size",123))
            .put("android",new JSONObject().put("file","SoundManager-0.4.2-android.apk").put("sha256","a".repeat(64)).put("size",456).put("versionCode",9));
    }
    private String sign(byte[] bytes)throws Exception {
        Signature signature=Signature.getInstance("SHA256withRSA");signature.initSign(key.getPrivate());signature.update(bytes);return Base64.encodeToString(signature.sign(),Base64.NO_WRAP);
    }
    private String publicKey(){return Base64.encodeToString(key.getPublic().getEncoded(),Base64.NO_WRAP);}
    @Test public void signatureProtectsExactManifestBytes()throws Exception {
        byte[] bytes=manifest().toString().getBytes(StandardCharsets.UTF_8);String signature=sign(bytes);
        assertEquals("0.4.2",AppUpdates.verify(bytes,signature,publicKey()).getString("version"));bytes[12]^=1;
        assertThrows(SecurityException.class,()->AppUpdates.verify(bytes,signature,publicKey()));
    }
    @Test public void signedManifestCannotChooseArbitraryPackageOrPath()throws Exception {
        JSONObject manifest=manifest();manifest.getJSONObject("android").put("file","../private.apk");
        byte[] bytes=manifest.toString().getBytes(StandardCharsets.UTF_8);String signature=sign(bytes);
        assertThrows(SecurityException.class,()->AppUpdates.verify(bytes,signature,publicKey()));
    }
    @Test public void versionsUseNumericOrderingAndRejectDowngradeNotation() {
        assertTrue(AppUpdates.compare("0.10.0","0.9.9")>0);assertFalse(AppUpdates.validVersion("0.04.2"));assertFalse(AppUpdates.validVersion("../0.4.2"));assertFalse(AppUpdates.validVersion("0.4.2-beta"));
    }
    @Test public void onlyHttpsReleaseHostsAreAllowed() {
        assertTrue(AppUpdates.allowed(okhttp3.HttpUrl.get("https://release-assets.githubusercontent.com/file")));
        assertFalse(AppUpdates.allowed(okhttp3.HttpUrl.get("http://github.com/file")));
        assertFalse(AppUpdates.allowed(okhttp3.HttpUrl.get("https://github.com.evil.example/file")));
        assertFalse(AppUpdates.allowed(okhttp3.HttpUrl.get("https://user@github.com/file")));
    }
    @Test public void apkHashAndLengthMustBothMatch()throws Exception {
        File file=File.createTempFile("update-test",".apk",context.getCacheDir());byte[] bytes={1,2,3};Files.write(file.toPath(),bytes);
        JSONObject asset=new JSONObject().put("size",3).put("sha256",AppUpdates.hex(MessageDigest.getInstance("SHA-256").digest(bytes)));
        assertTrue(AppUpdates.validFile(file,asset));Files.write(file.toPath(),new byte[]{1,2,4});assertFalse(AppUpdates.validFile(file,asset));
        asset.put("size",4);assertFalse(AppUpdates.validFile(file,asset));assertTrue(file.delete());
    }
    @Test public void disabledAutomaticCheckDoesNotChangeStoredPairingOrContactNetwork() {
        context.getSharedPreferences("trusted_connection",Context.MODE_PRIVATE).edit().putString("ciphertext","saved-pairing").commit();
        AppUpdates.configure(context,false,true);AppUpdates.check(context,false);
        assertEquals("saved-pairing",context.getSharedPreferences("trusted_connection",Context.MODE_PRIVATE).getString("ciphertext",null));
        assertFalse(AppUpdates.automatic(context));
    }
    @Test public void providerOnlyGrantsReadAccessToSingleApk()throws Exception {
        UpdateFileProvider provider=Robolectric.buildContentProvider(UpdateFileProvider.class).create().get();
        Uri uri=Uri.parse("content://"+context.getPackageName()+".updates/../trusted_connection.xml");
        assertThrows(FileNotFoundException.class,()->provider.openFile(uri,"r"));
        assertThrows(FileNotFoundException.class,()->provider.openFile(Uri.parse("content://"+context.getPackageName()+".updates/update.apk"),"rw"));
    }
}
