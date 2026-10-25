using System.Diagnostics;

namespace SoundManager.Updates;

public static class UpdateHandoff
{
    // Only the child started here is terminated on a failed readiness check.
    public static bool Launch(ProcessStartInfo application, ProcessStartInfo fallback, string readyMarker, TimeSpan timeout)
    {
        Process? child=null;
        bool healthy=false;
        try
        {
            child=Process.Start(application)??throw new IOException("업데이트한 앱을 시작하지 못했습니다.");
            var clock=Stopwatch.StartNew();
            while(!child.HasExited&&!File.Exists(readyMarker)&&clock.Elapsed<timeout)Thread.Sleep(100);
            if(!child.HasExited&&File.Exists(readyMarker)){healthy=true;return true;}
        }
        catch(Exception error) when(error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        finally
        {
            if(child is not null&&!healthy)
            {
                if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit(5000);}
                child.Dispose();child=null;
            }
            child?.Dispose();
        }
        using var previous=Process.Start(fallback)??throw new IOException("이전 앱을 시작하지 못했습니다.");
        return false;
    }
}
