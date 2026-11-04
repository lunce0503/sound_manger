using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundManager.Core;

// Synthetic 10-source, wall-clock comparison. No audio device or personal audio is used.
if(args.Length!=4)throw new ArgumentException("Usage: SoakCheck <rust.exe> <legacy-host.dll> <seconds> <report.jsonl>");
int seconds=int.Parse(args[2]); var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
await using var report=new StreamWriter(args[3],false,new UTF8Encoding(false)){AutoFlush=true};
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+60));
var children=new List<Process>();var clients=new List<HttpClient>();var streams=new List<ClientWebSocket>();
var senders=new List<Task>();
try
{
    var hosts=new List<(string Name,Process Process,HttpClient Http)>();
    foreach(string name in new[]{"rust","csharp"})
    {
        var start=new ProcessStartInfo(name=="rust"?Path.GetFullPath(args[0]):"dotnet")
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)};
        if(name=="csharp"){start.ArgumentList.Add(Path.GetFullPath(args[1]));start.ArgumentList.Add("--realtime");}
        var child=Process.Start(start)!;children.Add(child);
        _=child.StandardError.ReadToEndAsync(deadline.Token);
        string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if(name=="rust"){await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{key,host="127.0.0.1",port=0,headless=true}));await child.StandardInput.FlushAsync();}
        using var ready=JsonDocument.Parse((await child.StandardOutput.ReadLineAsync(deadline.Token))!);
        string address=name=="rust"?$"http://127.0.0.1:{ready.RootElement.GetProperty("port").GetInt32()}":ready.RootElement.GetProperty("address").GetString()!;
        if(name=="csharp")key=ready.RootElement.GetProperty("key").GetString()!;
        var http=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(5)};http.DefaultRequestHeaders.Add("X-Sound-Key",key);clients.Add(http);hosts.Add((name,child,http));
        var sockets=new List<ClientWebSocket>();
        for(int i=0;i<10;i++)
        {
            var ws=new ClientWebSocket();streams.Add(ws);sockets.Add(ws);ws.Options.SetRequestHeader("X-Sound-Key",key);
            await ws.ConnectAsync(new UriBuilder(address){Scheme="ws",Path="/v1/audio"}.Uri,deadline.Token);
            byte[] hello=JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1,$"synthetic-{i}",new(48000,2,"float32"),Guid.NewGuid()),json);
            await ws.SendAsync(hello.AsMemory(),WebSocketMessageType.Text,true,deadline.Token);
            await ws.ReceiveAsync(new byte[4096].AsMemory(),deadline.Token);
        }
        // Each sender has a known +/-1000 ppm sample clock skew.
        senders.Add(Send(sockets,seconds,deadline.Token));
    }
    var clock=Stopwatch.StartNew();var prior=hosts.ToDictionary(h=>h.Name,h=>h.Process.TotalProcessorTime);
    int samples=0; var firstMemory=new Dictionary<string,long>();var lastMemory=new Dictionary<string,long>();
    while(clock.Elapsed.TotalSeconds<seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(10),deadline.Token);
        foreach(var sender in senders)if(sender.IsFaulted)await sender;
        foreach(var host in hosts)
        {
            host.Process.Refresh();if(host.Process.HasExited)throw new IOException($"{host.Name} exited");
            var state=await host.Http.GetFromJsonAsync<MixerState>("/v1/state",json,deadline.Token)??throw new IOException("Missing state");
            if(state.Sources.Length!=10)throw new IOException($"{host.Name} lost sources");
            var cpu=host.Process.TotalProcessorTime;double used=(cpu-prior[host.Name]).TotalMilliseconds;prior[host.Name]=cpu;
            if(samples==2)firstMemory[host.Name]=host.Process.PrivateMemorySize64;lastMemory[host.Name]=host.Process.PrivateMemorySize64;
            var row=new{backend=host.Name,elapsedSeconds=clock.Elapsed.TotalSeconds,cpuCorePercent=used/100,privateBytes=host.Process.PrivateMemorySize64,workingSetBytes=host.Process.WorkingSet64,
                sources=state.Sources.Length,receivedPackets=state.Sources.Sum(s=>s.Diagnostics?.ReceivedPackets??0),underruns=state.Sources.Sum(s=>s.Underruns),droppedFrames=state.Sources.Sum(s=>s.DroppedFrames),latencyTrims=state.Sources.Sum(s=>s.Diagnostics?.LatencyTrims??0),
                minBufferMs=state.Sources.Min(s=>s.BufferedMs),maxBufferMs=state.Sources.Max(s=>s.BufferedMs),render=state.Diagnostics};
            await report.WriteLineAsync(JsonSerializer.Serialize(row,json));
        }
        samples++;
    }
    await report.WriteLineAsync(JsonSerializer.Serialize(new{complete=true,durationSeconds=clock.Elapsed.TotalSeconds,sourcesPerBackend=10,hardwarePlayback=false,
        memoryGrowthBytes=firstMemory.ToDictionary(x=>x.Key,x=>lastMemory[x.Key]-x.Value)},json));
    Console.WriteLine($"Completed {clock.Elapsed.TotalSeconds:F0} seconds, 10 sources per backend. Report: {args[3]}");
}
finally
{
    deadline.Cancel();foreach(var socket in streams)socket.Dispose();foreach(var http in clients)http.Dispose();
    foreach(var child in children){if(!child.HasExited){child.StandardInput.Close();try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){child.Kill(true);await child.WaitForExitAsync();}}child.Dispose();}
}

async Task Send(List<ClientWebSocket> sockets,int duration,CancellationToken token)
{
    try
    {
        var clock=Stopwatch.StartNew();var fractions=new double[10];var positions=new long[10];
        // Seed 80 ms, then send one packet every 20 ms without cumulative timer drift.
        for(int i=0;i<10;i++)await sockets[i].SendAsync(Tone(3840,i).AsMemory(),WebSocketMessageType.Binary,true,token);
        int block=0;
        while(clock.Elapsed.TotalSeconds<duration+15)
        {
            for(int i=0;i<10;i++)
            {
                fractions[i]+=960*(1+(i-4.5)/4.5*0.001);int frames=(int)fractions[i];fractions[i]-=frames;
                await sockets[i].SendAsync(Tone(frames,i).AsMemory(),WebSocketMessageType.Binary,true,token);
            }
            block++;double remaining=block*20-clock.Elapsed.TotalMilliseconds;
            if(remaining>0)await Task.Delay(TimeSpan.FromMilliseconds(remaining),token);
            else if(remaining < -200)block=(int)(clock.Elapsed.TotalMilliseconds/20);
        }
        byte[] Tone(int frames,int source)
        {
            var bytes=new byte[frames*8];
            for(int f=0;f<frames;f++){float sample=(float)(Math.Sin(2*Math.PI*(220+source*30)*positions[source]++/48000)*0.025);BitConverter.TryWriteBytes(bytes.AsSpan(f*8,4),sample);BitConverter.TryWriteBytes(bytes.AsSpan(f*8+4,4),sample*0.5f);}
            return bytes;
        }
    }
    catch(OperationCanceledException)when(token.IsCancellationRequested){}
}
