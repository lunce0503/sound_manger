package com.lunce.soundmanager;

import org.json.JSONObject;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import okhttp3.*;
import okio.ByteString;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

/** Real OkHttp/API -> Rust integration with synthetic PCM. Does not test AudioRecord hardware. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=29)
public class RustApiIntegrationTest {
    @Test(timeout=30000) public void androidApiAndPcmReconnectToRustWithSavedControls() throws Exception {
        String executable=System.getenv("SM_RUST_SERVER");
        Assume.assumeTrue("Set SM_RUST_SERVER to run the native backend integration",executable!=null && Files.isRegularFile(Path.of(executable)));
        Path folder=Files.createTempDirectory("SoundManager-Android-Rust-");
        Process child=new ProcessBuilder(executable).start();
        Thread errors=new Thread(()->{try(InputStream stream=child.getErrorStream()){byte[] bytes=new byte[4096];while(stream.read(bytes)!=-1){}}catch(IOException ignored){}});errors.setDaemon(true);errors.start();
        List<WebSocket> sockets=new ArrayList<>();
        try(BufferedWriter input=new BufferedWriter(new OutputStreamWriter(child.getOutputStream(),StandardCharsets.UTF_8));
            BufferedReader output=new BufferedReader(new InputStreamReader(child.getInputStream(),StandardCharsets.UTF_8))) {
            String key=UUID.randomUUID().toString()+UUID.randomUUID();
            input.write(new JSONObject().put("key",key).put("host","127.0.0.1").put("port",0).put("headless",true).put("settingsPath",folder.resolve("sources.json").toString()).toString());input.newLine();input.flush();
            JSONObject ready=new JSONObject(output.readLine());assertEquals("ready",ready.getString("event"));
            Api api=new Api("http://127.0.0.1:"+ready.getInt("port"),key);
            assertEquals(0,api.call("GET","/v1/state",null).getJSONArray("sources").length());
            api.call("PUT","/v1/mix",new JSONObject().put("masterVolume",0.4).put("bassDb",6));
            String device=UUID.randomUUID().toString();
            WebSocket first=connect(api,device);sockets.add(first);
            byte[] pcm=new byte[3840];for(int i=0;i<pcm.length;i+=2){pcm[i]=(byte)0xe8;pcm[i+1]=3;}
            for(int i=0;i<8;i++){assertTrue(first.send(ByteString.of(pcm)));Thread.sleep(20);}
            JSONObject source=api.call("GET","/v1/state",null).getJSONArray("sources").getJSONObject(0);
            assertTrue(source.getJSONObject("diagnostics").getLong("receivedPackets")>0);
            assertEquals("pcm16",source.getJSONObject("format").getString("encoding"));
            assertEquals(48000,source.getJSONObject("format").getInt("sampleRate"));
            api.call("PATCH","/v1/sources/"+source.getString("id"),new JSONObject().put("volume",0.35).put("azimuth",275).put("spatial",true).put("muted",true).put("autoGain",true).put("displayName","Android 합성 음원"));
            first.close(1000,"reconnect");
            for(int i=0;i<100 && api.call("GET","/v1/state",null).getJSONArray("sources").length()!=0;i++)Thread.sleep(10);
            WebSocket second=connect(api,device);sockets.add(second);
            JSONObject restored=api.call("GET","/v1/state",null).getJSONArray("sources").getJSONObject(0);
            JSONObject settings=restored.getJSONObject("settings");assertEquals("Android 합성 음원",restored.getString("name"));
            assertEquals(0.35,settings.getDouble("volume"),0.00001);assertEquals(275,settings.getInt("azimuth"));
            assertTrue(settings.getBoolean("muted") && settings.getBoolean("spatial") && settings.getBoolean("autoGain"));
            assertEquals(6,api.call("GET","/v1/state",null).getJSONObject("settings").getInt("bassDb"));
            try {new Api(api.address,UUID.randomUUID().toString()).call("GET","/v1/state",null);fail("Expected authentication failure");}
            catch(Api.Failure error){assertEquals(401,error.status);}
        } finally {
            for(WebSocket socket:sockets)socket.cancel();
            child.getOutputStream().close();if(!child.waitFor(5,TimeUnit.SECONDS)){child.destroyForcibly();child.waitFor();}
            try(java.util.stream.Stream<Path> paths=Files.walk(folder)){for(Path path:(Iterable<Path>)paths.sorted(Comparator.reverseOrder())::iterator)Files.delete(path);}
        }
    }
    private WebSocket connect(Api api,String device) throws Exception {
        CompletableFuture<WebSocket> ready=new CompletableFuture<>();
        WebSocket socket=Api.CLIENT.newWebSocket(new Request.Builder().url(api.address+"/v1/audio").header("X-Sound-Key",api.key).build(),new WebSocketListener(){
            @Override public void onOpen(WebSocket ws,Response response){try{ws.send(new JSONObject().put("version",1).put("name","Android synthetic").put("deviceId",device).put("format",new JSONObject().put("sampleRate",48000).put("channels",2).put("encoding","pcm16")).toString());}catch(Exception error){ready.completeExceptionally(error);}}
            @Override public void onMessage(WebSocket ws,String message){try{new JSONObject(message).getString("sourceId");ready.complete(ws);}catch(Exception error){ready.completeExceptionally(error);}}
            @Override public void onClosing(WebSocket ws,int code,String reason){ws.close(code,reason);}
            @Override public void onFailure(WebSocket ws,Throwable error,Response response){ready.completeExceptionally(error);}
        });
        try{return ready.get(10,TimeUnit.SECONDS);}catch(Exception error){socket.cancel();throw error;}
    }
}
