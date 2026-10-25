using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Updates;

namespace SoundManager.Windows;

internal sealed record UpdatePreferences(bool Automatic = true, bool ApplyWhenIdle = true, string? FailedVersion = null);
internal sealed record UpdateResume(string Token, string Version, bool Server, bool Sender, bool External, bool Hidden, MixSettings Mix);

internal sealed class WindowsUpdates : IAsyncDisposable
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundManager");
    private readonly string directory;
    private readonly Version current;
    private readonly Action<string> report;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim check = new(1);
    private readonly HttpClient http = UpdateFeed.CreateHttpClient();
    private Task loop = Task.CompletedTask;
    private DateTime nextDownload;
    private VerifiedUpdate? available;
    private bool verifiedReady;
    private string? handoff;
    private bool committed;
    private bool preparing;
    public UpdatePreferences Preferences { get; private set; }
    public bool Ready => available is not null && verifiedReady;
    public string? AvailableVersion => available?.Manifest.Version;
    private string Installer => Path.Combine(directory, "pending.exe");
    public WindowsUpdates(Version current, Action<string> report, string? root = null)
    {
        this.current=current; this.report=report; directory=Path.Combine(root??Root,"updates");
        Preferences=Read<UpdatePreferences>(Path.Combine(directory,"preferences.json"))??new();
        try
        {
            string manifest=Path.Combine(directory,"pending.json"), signature=Path.Combine(directory,"pending.sig");
            if (File.Exists(manifest)&&File.Exists(signature))
            {
                var bytes=File.ReadAllBytes(manifest);var sig=File.ReadAllText(signature);var parsed=UpdateManifest.Verify(bytes,sig);
                if (UpdateManifest.ParseVersion(parsed.Version)>current && UpdateFeed.VerifyFile(Installer,parsed.Windows)) { available=new(parsed,bytes,sig); verifiedReady=true; }
            }
        }
        catch (Exception) { available=null; }
    }
    public void SetPreferences(bool automatic,bool idle,string? failed=null)
    { Preferences=new(automatic,idle,failed??Preferences.FailedVersion);Save(Path.Combine(directory,"preferences.json"),Preferences); }
    public void Start(Func<bool> canDownload)
    {
        loop=RunAsync();
        async Task RunAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30),stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    if (Preferences.Automatic) await CheckAsync(canDownload());
                    await Task.Delay(TimeSpan.FromHours(6),stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
    public async Task CheckAsync(bool download)
    {
        if (preparing||stop.IsCancellationRequested||!await check.WaitAsync(0)) return;
        try
        {
            report("새 버전을 확인하고 있습니다…");
            var found=await new UpdateFeed(http).CheckAsync(current,stop.Token);
            if (found is not null && (available is null || UpdateManifest.ParseVersion(found.Manifest.Version)>UpdateManifest.ParseVersion(available.Manifest.Version))) { available=found; verifiedReady=false; }
            if (available is null) {report("현재 버전이 최신입니다.");return;}
            if (download) await DownloadCoreAsync();
            else report($"{available.Manifest.Version} 업데이트 · 사용 중인 음원이 끝나면 다운로드합니다.");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { report("업데이트 확인에 실패했습니다. 현재 앱과 설정은 유지됩니다.");nextDownload=DateTime.UtcNow.AddMinutes(30); }
        finally {check.Release();}
    }
    public async Task DownloadWhenIdleAsync()
    {
        if (preparing||stop.IsCancellationRequested||available is null||Ready||DateTime.UtcNow<nextDownload||!Preferences.Automatic||!await check.WaitAsync(0)) return;
        try {await DownloadCoreAsync();}
        catch(OperationCanceledException) { }
        catch(Exception) {report("업데이트 다운로드에 실패했습니다. 나중에 다시 시도합니다.");nextDownload=DateTime.UtcNow.AddMinutes(30);}
        finally {check.Release();}
    }
    private async Task DownloadCoreAsync()
    {
        var candidate=available!;
        if (!UpdateFeed.VerifyFile(Installer,candidate.Manifest.Windows))
            await new UpdateFeed(http).DownloadAsync(candidate,Installer,new Progress<int>(value=>report($"업데이트 다운로드 {value}%")),stop.Token);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory,"pending.json"),candidate.ManifestBytes);
        File.WriteAllText(Path.Combine(directory,"pending.sig"),candidate.Signature);
        verifiedReady=true;
        report($"{candidate.Manifest.Version} 업데이트 준비 완료 · 사용하지 않을 때 적용합니다.");
    }
    public async Task<bool> PrepareAsync(UpdateResume resume, Func<bool> canCommit, Func<UpdateResume>? latestState = null)
    {
        if(preparing||!await check.WaitAsync(0))return false;
        preparing=true;
        try {return await PrepareCoreAsync(resume,canCommit,latestState);}
        finally {preparing=false;check.Release();}
    }
    private async Task<bool> PrepareCoreAsync(UpdateResume resume, Func<bool> canCommit, Func<UpdateResume>? latestState)
    {
        var candidate=available;
        if (candidate is null || !await Task.Run(()=>UpdateFeed.VerifyFile(Installer,candidate.Manifest.Windows),stop.Token)) {verifiedReady=false;return false;}
        if (!LauncherOptions.IsInstalledExecutable(Root,Environment.ProcessPath!)) throw new InvalidOperationException("배포 EXE로 실행한 앱에서 업데이트를 적용하세요.");
        handoff=resume.Token;Save(Path.Combine(directory,"resume.json"),resume);
        var start=new ProcessStartInfo(Installer){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory};
        foreach(var argument in new[]{"--wait-for",Environment.ProcessId.ToString(),"--update-token",resume.Token,"--fallback",Environment.ProcessPath!})start.ArgumentList.Add(argument);
        using var helper=Process.Start(start)??throw new IOException("업데이트 실행기를 시작하지 못했습니다.");
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromMinutes(2));
            string prepared=LauncherOptions.Marker(Root,resume.Token,"prepared");
            while(!File.Exists(prepared))
            {if(helper.HasExited)throw new IOException("업데이트를 준비하지 못했습니다.");await Task.Delay(100,timeout.Token);}
            if(File.ReadAllText(prepared)!=resume.Version||!canCommit()) {CancelHandoff();return false;}
            if(latestState is not null)Save(Path.Combine(directory,"resume.json"),latestState());
            committed=true;return true;
        }
        catch {CancelHandoff();throw;}
    }
    private void CancelHandoff()
    {
        if(handoff is not null&&!committed)File.WriteAllText(Path.Combine(directory,handoff+".cancel"),"");
        handoff=null;
    }
    public static UpdateResume? Resume(string token, string? root = null)
    {
        if(!LauncherOptions.IsToken(token))return null;
        var state=Read<UpdateResume>(Path.Combine(root??Root,"updates","resume.json"));
        return state?.Token==token&&state.Mix is {IsValid:true}&&!(state.Server&&state.Sender)?state:null;
    }
    public void MarkFailed(string version)
    {SetPreferences(Preferences.Automatic,Preferences.ApplyWhenIdle,version);report("업데이트한 앱을 시작하지 못해 이전 버전으로 복구했습니다.");}
    public bool MayApplyAutomatically=>Ready&&Preferences.Automatic&&Preferences.ApplyWhenIdle&&Preferences.FailedVersion!=AvailableVersion;
    public static void Save<T>(string path,T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(data));File.Move(temporary,path,overwrite:true);
    }
    private static T? Read<T>(string path)
    {try{return File.Exists(path)&&new FileInfo(path).Length<16384?JsonSerializer.Deserialize<T>(File.ReadAllText(path)):default;}catch(Exception){return default;}}
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();CancelHandoff();
        await loop;await check.WaitAsync();check.Release();http.Dispose();stop.Dispose();check.Dispose();
    }
}
