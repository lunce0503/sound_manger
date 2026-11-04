using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class RustBackendTests
{
    [Fact] public async Task LatePollCannotUndoACompletedMuteInTheControllerCache()
    {
        await using var backend=new RustBackend();
        Guid id=Guid.NewGuid();
        var old=new MixerState(new(),[new(id,"music",new(48000,2,"pcm16"),new(Muted:false),60,0,0)],0);
        var updated=old with{Sources=[old.Sources[0] with{Settings=new(Muted:true)}]};
        backend.AcceptSnapshot(12,updated);
        Assert.True(Assert.Single(backend.AcceptSnapshot(11,old).Sources).Settings.Muted);
        Assert.True(Assert.Single(backend.Snapshot().Sources).Settings.Muted);
    }
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static string Executable()
    {
        string? configured=Environment.GetEnvironmentVariable("SM_RUST_SERVER");
        if(configured is not null&&File.Exists(configured))return configured;
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        foreach(string profile in new[]{"release","debug"})
        {
            string path=Path.Combine(root,"rust","target",profile,"sound-manager-server.exe");
            if(File.Exists(path))return path;
        }
        throw new IOException("Build the Rust server first: cargo build --manifest-path rust/Cargo.toml");
    }
    [Fact] public async Task WpfControllerUsesRealRustServerAndPreservesDeviceSettings()
    {
        string directory=Path.Combine(Path.GetTempPath(),"SoundManager-Rust-bridge-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string key=HubServer.CreateToken();Guid device=Guid.NewGuid();
        try
        {
            await using var backend=new RustBackend();
            await backend.StartAsync(Executable(),null,key,Path.Combine(directory,"sources.json"),new MixSettings(.5f),port:0,headless:true,host:"127.0.0.1");
            int pid=backend.ProcessId!.Value;
            using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("X-Sound-Key",key);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{backend.Port}/v1/audio"),timeout.Token);
            byte[] hello=JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1,"Windows bridge",new AudioFormat(48000,2,"float32"),device),Json);
            await socket.SendAsync(hello.AsMemory(),WebSocketMessageType.Text,true,timeout.Token);
            await socket.ReceiveAsync(new byte[4096].AsMemory(),timeout.Token);
            var state=await backend.RefreshAsync(timeout.Token);Assert.Equal(.5f,state.Settings.MasterVolume);Guid id=Assert.Single(state.Sources).Id;
            Assert.Equal(SourceUpdateResult.Updated,await backend.UpdateSourceAsync(id,new(Volume:.42f,Azimuth:190,Muted:true,DisplayName:"Rust 음악",AutoGain:true)));
            var source=Assert.Single((await backend.RefreshAsync(timeout.Token)).Sources);Assert.Equal("Rust 음악",source.Name);Assert.True(source.Settings.Muted);Assert.Equal(.42f,source.Settings.Volume);
            await backend.SetMixAsync(new MixSettings(.33f,6),timeout.Token);Assert.Equal(6,(await backend.RefreshAsync(timeout.Token)).Settings.BassDb);
            await backend.SetProcessingAsync(new ProcessingSettings(TargetDbfs:-21,MaxGainDb:6),timeout.Token);
            Assert.Equal(-21,(await backend.RefreshAsync(timeout.Token)).Processing!.TargetDbfs);
            Assert.False(backend.TryBeginUpdate(true));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"test",timeout.Token);
            while((await backend.RefreshAsync(timeout.Token)).Sources.Length!=0)await Task.Delay(10,timeout.Token);
            Assert.True(backend.TryBeginUpdate(true));backend.CancelUpdate();
            await backend.DisposeAsync();Assert.Throws<ArgumentException>(()=>Process.GetProcessById(pid));
            using var stored=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"sources.json")));
            Assert.Equal("Rust 음악",stored.RootElement.GetProperty("Devices").GetProperty(device.ToString()).GetProperty("DisplayName").GetString());
            Assert.True(stored.RootElement.GetProperty("Devices").GetProperty(device.ToString()).GetProperty("AutoGain").GetBoolean());
            await using var restarted=new RustBackend();
            await restarted.StartAsync(Executable(),null,key,Path.Combine(directory,"sources.json"),new(),port:0,headless:true,host:"127.0.0.1");
            Assert.Equal(6,(await restarted.RefreshAsync(timeout.Token)).Processing!.MaxGainDb);
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact] public async Task MissingDeviceFailsWithoutOpeningAFallbackDeviceOrExposingKey()
    {
        string key=HubServer.CreateToken();await using var backend=new RustBackend();
        var error=await Assert.ThrowsAnyAsync<Exception>(()=>backend.StartAsync(Executable(),"not-a-real-device",key,null,new(),port:0,host:"127.0.0.1"));
        Assert.DoesNotContain(key,error.ToString());
    }
    [Fact] public async Task PartialPrivateControlMessageSurvivesWatchdogTicksAndPipeEofStopsChild()
    {
        var start=new ProcessStartInfo(Executable()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)};
        using var process=Process.Start(start)!;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{key=HubServer.CreateToken(),port=0,host="127.0.0.1",headless=true}));await process.StandardInput.FlushAsync();
            string ready=(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;Assert.Contains("ready",ready);
            await process.StandardInput.WriteAsync("{\"op\":\"sta");await process.StandardInput.FlushAsync();await Task.Delay(600);
            await process.StandardInput.WriteLineAsync("te\",\"requestId\":17}");await process.StandardInput.FlushAsync();
            using var reply=JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            Assert.Equal(17,reply.RootElement.GetProperty("requestId").GetInt32());Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
            process.StandardInput.Close();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,process.ExitCode);
        }
        finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
    }
    [Fact] public async Task UnexpectedSidecarExitIsReportedToTheWindowController()
    {
        await using var backend=new RustBackend();var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);backend.Failed+=()=>failed.TrySetResult();
        await backend.StartAsync(Executable(),null,HubServer.CreateToken(),null,new(),port:0,headless:true,host:"127.0.0.1");
        using var child=Process.GetProcessById(backend.ProcessId!.Value);child.Kill(entireProcessTree:true);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact] public async Task FragmentedHelloAndPcmReachRustWithoutChangingClientFormat()
    {
        string key=HubServer.CreateToken();await using var backend=new RustBackend();
        await backend.StartAsync(Executable(),null,key,null,new(),port:0,headless:true,host:"127.0.0.1");
        using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("X-Sound-Key",key);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{backend.Port}/v1/audio"),timeout.Token);
        byte[] hello=JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1,"fragmented Android-compatible PCM",new(44100,1,"pcm16"),Guid.NewGuid()),Json);
        await socket.SendAsync(hello.AsMemory(0,11),WebSocketMessageType.Text,false,timeout.Token);
        await socket.SendAsync(hello.AsMemory(11),WebSocketMessageType.Text,true,timeout.Token);
        await socket.ReceiveAsync(new byte[4096].AsMemory(),timeout.Token);
        byte[] pcm=new byte[8820];for(int i=0;i<pcm.Length;i+=2)BitConverter.TryWriteBytes(pcm.AsSpan(i,2),(short)1000);
        await socket.SendAsync(pcm.AsMemory(0,333),WebSocketMessageType.Binary,false,timeout.Token);
        await socket.SendAsync(pcm.AsMemory(333),WebSocketMessageType.Binary,true,timeout.Token);
        SourceState source;
        do{source=Assert.Single((await backend.RefreshAsync(timeout.Token)).Sources);await Task.Delay(5,timeout.Token);}while(source.Diagnostics?.ReceivedPackets!=1);
        Assert.Equal(new AudioFormat(44100,1,"pcm16"),source.Format);
        Assert.InRange(source.Diagnostics!.LastPacketAudioMs,99.9,100.1);
        Assert.Equal(0,source.DroppedFrames);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"done",timeout.Token);
    }
}
