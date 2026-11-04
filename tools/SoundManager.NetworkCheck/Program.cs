using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

if(args.Length!=4 || args[0]!="--tunnel-exe" || args[2]!="--rust-exe")
{
    Console.Error.WriteLine("Usage: NetworkCheck --tunnel-exe <verified cloudflared.exe> --rust-exe <sound-manager-server.exe>");return 2;
}
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(150));
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);string key=HubServer.CreateToken();
var start=new ProcessStartInfo(Path.GetFullPath(args[3])){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)};
using var child=Process.Start(start)!;_ = child.StandardError.ReadToEndAsync();
try
{
    await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{key,port=0,host="127.0.0.1",headless=true}));await child.StandardInput.FlushAsync();
    using var ready=JsonDocument.Parse((await child.StandardOutput.ReadLineAsync(deadline.Token))!);
    int port=ready.RootElement.GetProperty("port").GetInt32();
    await using var tunnel=new ExternalTunnel();string address=await tunnel.StartAsync(Path.GetFullPath(args[1]),deadline.Token,port);
    using var handler=new HttpClientHandler{AllowAutoRedirect=false};
    using var http=new HttpClient(handler,disposeHandler:false){BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(8)};
    http.DefaultRequestHeaders.Add("X-Sound-Key",key);HttpStatusCode status=0;
    for(int attempt=0;attempt<24;attempt++)
    {
        try{using var response=await http.GetAsync("/v1/state",deadline.Token);status=response.StatusCode;}
        catch(Exception error)when(error is HttpRequestException or TaskCanceledException){deadline.Token.ThrowIfCancellationRequested();}
        if(status==HttpStatusCode.OK)break;await Task.Delay(1500,deadline.Token);
    }
    if(status!=HttpStatusCode.OK)throw new IOException("External HTTPS state endpoint was not reachable.");
    using var unauthenticated=new HttpClient(handler,disposeHandler:false){BaseAddress=new Uri(address)};
    using var forbidden=await unauthenticated.GetAsync("/v1/state",deadline.Token);
    if(forbidden.StatusCode!=HttpStatusCode.Unauthorized)throw new IOException("External endpoint did not require authentication.");
    using var update=await http.PutAsJsonAsync("/v1/mix",new MixSettings(.5f,6),deadline.Token);update.EnsureSuccessStatusCode();
    var sockets=new List<ClientWebSocket>();
    try
    {
        foreach(bool browser in new[]{false,true})
        {
            var socket=new ClientWebSocket();sockets.Add(socket);
            if(browser)socket.Options.SetRequestHeader("Origin","chrome-extension://abcdefghijklmnopabcdefghijklmnop");
            else socket.Options.SetRequestHeader("X-Sound-Key",key);
            await socket.ConnectAsync(new UriBuilder(address){Scheme="wss",Path=browser?"/v1/browser-audio":"/v1/audio"}.Uri,deadline.Token);
            byte[] hello=JsonSerializer.SerializeToUtf8Bytes(new{version=1,name=browser?"synthetic-browser-network-check":"synthetic-Android-network-check",format=new AudioFormat(48000,2,"pcm16"),deviceId=Guid.NewGuid(),key=browser?key:null},json);
            await socket.SendAsync(hello.AsMemory(),WebSocketMessageType.Text,true,deadline.Token);
            var buffer=new byte[4096];var ack=await socket.ReceiveAsync(buffer.AsMemory(),deadline.Token);
            using var helloReply=JsonDocument.Parse(buffer.AsMemory(0,ack.Count));
            if(!helloReply.RootElement.TryGetProperty("sourceId",out _))throw new IOException("WSS audio handshake failed.");
        }
        byte[] pcm=new byte[15360];for(int i=0;i<pcm.Length;i+=2)BitConverter.TryWriteBytes(pcm.AsSpan(i,2),(short)1000);
        bool heard=false;
        for(int attempt=0;attempt<20;attempt++)
        {
            foreach(var socket in sockets)await socket.SendAsync(pcm.AsMemory(),WebSocketMessageType.Binary,true,deadline.Token);
            using var state=JsonDocument.Parse(await http.GetStringAsync("/v1/state",deadline.Token));
            if(state.RootElement.GetProperty("sources").GetArrayLength()!=2)throw new IOException("WSS source lost");
            if(state.RootElement.GetProperty("outputRms").GetDouble()>0){heard=true;break;}
            await Task.Delay(40,deadline.Token);
        }
        if(!heard)throw new IOException("WSS synthetic audio did not reach Rust DSP.");
        var final=await http.GetFromJsonAsync<MixerState>("/v1/state",deadline.Token);
        if(final?.Settings.BassDb!=6 || final.Settings.MasterVolume!=.5f)throw new IOException("External controls failed.");
        foreach(var socket in sockets)await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"check complete",deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new{backend="rust",httpsStatus=(int)status,unauthenticatedStatus=(int)forbidden.StatusCode,webSocket="wss",nativeAndBrowserPaths=true,pcmReachedDsp=true,mixControl=true,hardwarePlayback=false}));
    }
    finally{foreach(var socket in sockets)socket.Dispose();}
}
finally
{
    child.StandardInput.Close();try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){child.Kill(true);await child.WaitForExitAsync();}
}
return 0;
