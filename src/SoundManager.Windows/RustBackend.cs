using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Windows;

// The WPF process only controls the server. Rust owns network audio, DSP and WASAPI.
internal sealed class RustBackend : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim write=new(1);
    private readonly ConcurrentDictionary<long,TaskCompletionSource<JsonElement>> replies=new();
    private readonly TaskCompletionSource<int> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(3)};
    private Process? process;
    private Task reader=Task.CompletedTask,errors=Task.CompletedTask,watcher=Task.CompletedTask;
    private MixerState snapshot=new(new(),[],0);
    private long requestId;
    private volatile bool stopping;
    public event Action? Failed;
    public int Port {get;private set;}
    public int? ProcessId=>process?.Id;
    public MixerState Snapshot()=>Volatile.Read(ref snapshot);
    public async Task StartAsync(string executable,string? outputDeviceId,string key,string? settingsPath,MixSettings mix,
        CancellationToken cancellation=default,int port=17842,bool headless=false,string host="0.0.0.0")
    {
        if(process is not null)throw new InvalidOperationException("서버가 이미 실행 중입니다.");
        if(!File.Exists(executable))throw new IOException("Rust 서버 실행 파일이 없습니다. 최신 배포 EXE를 확인하세요.");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8};
        process=Process.Start(start)??throw new IOException("Rust 서버를 시작하지 못했습니다.");
        reader=ReadMessagesAsync();errors=DrainErrorsAsync();watcher=WatchAsync();
        string configuration=JsonSerializer.Serialize(new{key,port,host,outputDeviceId,settingsPath,mix,headless},Json);
        await process.StandardInput.WriteLineAsync(configuration.AsMemory(),cancellation).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Port=await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        http.BaseAddress=new Uri($"http://127.0.0.1:{Port}");http.DefaultRequestHeaders.Add("X-Sound-Key",key);
        await RefreshAsync(cancellation).ConfigureAwait(false);
    }
    private async Task ReadMessagesAsync()
    {
        try
        {
            while(await process!.StandardOutput.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if(line.Length>262144)throw new IOException("Rust 서버 응답이 너무 큽니다.");
                using var document=JsonDocument.Parse(line);var value=document.RootElement;
                switch(value.GetProperty("event").GetString())
                {
                    case "ready":ready.TrySetResult(value.GetProperty("port").GetInt32());break;
                    case "reply":
                        if(value.TryGetProperty("requestId",out var id)&&id.TryGetInt64(out long number)&&replies.TryRemove(number,out var completion))completion.TrySetResult(value.Clone());
                        break;
                    case "error":ready.TrySetException(new IOException("Rust 오디오 서버를 시작하지 못했습니다. 출력 장치와 저장된 설정을 확인하세요."));break;
                }
            }
        }
        catch(Exception error) when(error is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {if(!stopping)ready.TrySetException(new IOException("Rust 서버 응답을 읽지 못했습니다."));}
        finally
        {
            ready.TrySetException(new IOException("Rust 서버가 종료되었습니다."));
            foreach(var pair in replies)if(replies.TryRemove(pair.Key,out var completion))completion.TrySetException(new IOException("Rust 서버가 종료되었습니다."));
        }
    }
    private async Task DrainErrorsAsync()
    {
        // Never copy raw child output into UI or a credential-bearing log.
        try{while(await process!.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is not null){}}
        catch(Exception error) when(error is IOException or OperationCanceledException){ }
    }
    private async Task WatchAsync()
    {
        await process!.WaitForExitAsync().ConfigureAwait(false);
        if(!stopping)Failed?.Invoke();
    }
    private async Task<JsonElement> RequestAsync(string op,bool requireIdle=true,CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        long id=Interlocked.Increment(ref requestId);var reply=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id]=reply;
        try
        {
            await write.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                string message=JsonSerializer.Serialize(new{op,requireIdle,requestId=id},Json);
                await process!.StandardInput.WriteLineAsync(message.AsMemory(),timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally{write.Release();}
            return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally{replies.TryRemove(id,out _);}
    }
    public async Task<MixerState> RefreshAsync(CancellationToken cancellation=default)
    {
        var message=await RequestAsync("state",cancellation:cancellation).ConfigureAwait(false);
        var value=message.GetProperty("state").Deserialize<MixerState>(Json)??throw new IOException("Rust 서버 상태를 확인하지 못했습니다.");
        Volatile.Write(ref snapshot,value);return value;
    }
    public async Task SetMixAsync(MixSettings settings,CancellationToken cancellation=default)
    {
        using var response=await http.PutAsJsonAsync("/v1/mix",settings,Json,cancellation).ConfigureAwait(false);response.EnsureSuccessStatusCode();
    }
    public async Task<SourceUpdateResult> UpdateSourceAsync(Guid id,SourceSettingsPatch patch)
    {
        try
        {
            using var response=await http.PatchAsJsonAsync($"/v1/sources/{id}",patch,Json,lifetime.Token).ConfigureAwait(false);
            if(response.IsSuccessStatusCode){await RefreshAsync(lifetime.Token).ConfigureAwait(false);return SourceUpdateResult.Updated;}
            return response.StatusCode switch{HttpStatusCode.NotFound=>SourceUpdateResult.NotFound,HttpStatusCode.BadRequest=>SourceUpdateResult.Invalid,_=>SourceUpdateResult.SaveFailed};
        }
        catch(Exception error) when(error is IOException or HttpRequestException or OperationCanceledException){return SourceUpdateResult.SaveFailed;}
    }
    public bool TryBeginUpdate(bool requireIdle)=>RequestAsync("beginUpdate",requireIdle).GetAwaiter().GetResult().GetProperty("ok").GetBoolean();
    public void CancelUpdate()=>RequestAsync("cancelUpdate").GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if(stopping)return;stopping=true;
        if(process is not null)
        {
            try
            {
                if(!process.HasExited){await RequestAsync("stop").ConfigureAwait(false);process.StandardInput.Close();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);}
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or TimeoutException or InvalidOperationException)
            {if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync().ConfigureAwait(false);}}
            finally{lifetime.Cancel();await Task.WhenAll(reader,errors,watcher).ConfigureAwait(false);process.Dispose();}
        }
        lifetime.Dispose();write.Dispose();http.Dispose();
    }
}
