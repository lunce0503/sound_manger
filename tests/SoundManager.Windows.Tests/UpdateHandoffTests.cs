using System.Diagnostics;
using System.IO;
using SoundManager.Updates;

namespace SoundManager.Windows.Tests;

public sealed class UpdateHandoffTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"SoundManager-handoff-"+Guid.NewGuid().ToString("N"));
    private readonly string script;
    public UpdateHandoffTests()
    {
        Directory.CreateDirectory(root);script=Path.Combine(root,"probe.ps1");
        File.WriteAllText(script,"""
            param([string]$Mode,[string]$Marker)
            if ($Mode -eq 'ready') { [IO.File]::WriteAllText($Marker,'ready'); Start-Sleep -Seconds 2 }
            if ($Mode -eq 'crash') { exit 1 }
            if ($Mode -eq 'hang') { Start-Sleep -Seconds 30 }
            if ($Mode -eq 'fallback') { [IO.File]::WriteAllText($Marker,'restored') }
            """);
    }
    private ProcessStartInfo Probe(string mode,string marker)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(string argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Mode",mode,"-Marker",marker})start.ArgumentList.Add(argument);
        return start;
    }
    [Fact] public void HealthyNewProcessDoesNotStartFallback()
    {
        string ready=Path.Combine(root,"ready"),fallback=Path.Combine(root,"fallback");
        Assert.True(UpdateHandoff.Launch(Probe("ready",ready),Probe("fallback",fallback),ready,TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(fallback));
    }
    [Theory][InlineData("crash")][InlineData("hang")]
    public async Task CrashOrMissingReadinessRestartsPreviousApplication(string mode)
    {
        string ready=Path.Combine(root,"ready"),fallback=Path.Combine(root,"fallback");
        Assert.False(UpdateHandoff.Launch(Probe(mode,ready),Probe("fallback",fallback),ready,TimeSpan.FromSeconds(2)));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!File.Exists(fallback))await Task.Delay(50,timeout.Token);
        Assert.Equal("restored",File.ReadAllText(fallback));
    }
    public void Dispose(){Directory.Delete(root,true);}
}
