using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SoundManager.Server;

// Owns only the child it started. No installation, firewall or router changes.
public sealed partial class ExternalTunnel : IAsyncDisposable
{
    public const string Version = "2026.10.3";
    public const string Sha256 = "76b2805253d0d6ffacb092a7acfbee99e7af6917835c0c5410e23f3ef5052da2";
    private Process? process;
    private bool disposing;
    private readonly TaskCompletionSource<string> endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? Stopped;

    public async Task<string> StartAsync(string executable, CancellationToken cancellation = default)
    {
        if (process is not null) throw new InvalidOperationException("외부 연결이 이미 실행 중입니다.");
        if (!File.Exists(executable)) throw new FileNotFoundException("배포 폴더의 cloudflared.exe가 필요합니다.", executable);
        await using (var stream = File.OpenRead(executable))
        {
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
            if (!digest.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("외부 연결 도구의 무결성 검증에 실패했습니다. 공식 배포 파일을 다시 받으세요.");
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--protocol", "http2", "--url", "http://127.0.0.1:17842" })
            start.ArgumentList.Add(arg);
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => ReadLine(args.Data);
        process.ErrorDataReceived += (_, args) => ReadLine(args.Data);
        process.Exited += (_, _) =>
        {
            endpoint.TrySetException(new IOException("외부 연결 도구가 종료되었습니다. 네트워크를 확인하세요."));
            if (!disposing) Stopped?.Invoke();
        };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return await endpoint.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
        }
        catch { await DisposeAsync(); throw; }
    }

    private void ReadLine(string? line)
    {
        if (line is null) return;
        var match = TunnelUrl().Match(line);
        if (match.Success) endpoint.TrySetResult(match.Value);
    }

    [GeneratedRegex(@"https://[a-z0-9]+(?:-[a-z0-9]+)*\.trycloudflare\.com\b", RegexOptions.CultureInvariant)]
    private static partial Regex TunnelUrl();

    public async ValueTask DisposeAsync()
    {
        disposing = true;
        var current = process; process = null;
        if (current is null) return;
        try
        {
            try { if (!current.HasExited) current.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await current.WaitForExitAsync();
        }
        catch (InvalidOperationException) { }
        finally { current.Dispose(); }
    }
}
