using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoundManager.Updates;

public sealed class UpdateFeed(HttpClient http, string publicKey = UpdateManifest.PublicKey)
{
    public const string ReleasesUrl = "https://api.github.com/repos/lunce0503/sound_manger/releases?per_page=20";

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<VerifiedUpdate?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse(await ReadAsync(ReleasesUrl, 2 * 1024 * 1024, cancellation));
        var releases = document.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean())
            .Select(r => (Release: r, Tag: r.GetProperty("tag_name").GetString() ?? ""))
            .Where(r => r.Tag.StartsWith('v') && TryVersion(r.Tag[1..], out var version) && version > current)
            .OrderByDescending(r => UpdateManifest.ParseVersion(r.Tag[1..]));
        foreach (var release in releases)
        {
            string version = release.Tag[1..];
            var names = release.Release.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();
            if (!names.Contains("update.json") || !names.Contains("update.json.sig")) continue;
            byte[] bytes = await ReadAsync(UpdateManifest.AssetUrl(version, "update.json"), 65536, cancellation);
            string signature = Encoding.UTF8.GetString(await ReadAsync(UpdateManifest.AssetUrl(version, "update.json.sig"), 4096, cancellation));
            var manifest = UpdateManifest.Verify(bytes, signature, publicKey);
            if (manifest.Version != version || !names.Contains(manifest.Windows.File) || !names.Contains(manifest.Android.File))
                throw new InvalidDataException("업데이트 버전 또는 파일이 일치하지 않습니다.");
            return new(manifest, bytes, signature);
        }
        return null;
    }

    private static bool TryVersion(string text, out Version version)
    {
        try { version = UpdateManifest.ParseVersion(text); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or OverflowException) { version = new(); return false; }
    }

    public async Task DownloadAsync(VerifiedUpdate update, string destination, IProgress<int>? progress, CancellationToken cancellation)
    {
        var asset = update.Manifest.Windows;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string temporary = destination + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await GetAsync(UpdateManifest.AssetUrl(update.Manifest.Version, asset.File), timeout.Token);
            if (response.Content.Headers.ContentLength is long size && size != asset.Size) throw new InvalidDataException("업데이트 크기가 일치하지 않습니다.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536]; long received = 0; int lastProgress = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, timeout.Token); if (count == 0) break;
                    received += count; if (received > asset.Size) throw new InvalidDataException("업데이트 파일이 너무 큽니다.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    int percent = (int)(received * 100 / asset.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("업데이트 파일 검증에 실패했습니다.");
                await output.FlushAsync(timeout.Token);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool VerifyFile(string path, UpdateAsset asset)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> ReadAsync(string url, int max, CancellationToken cancellation)
    {
        using var response = await GetAsync(url, cancellation);
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellation); if (count == 0) break;
            if (output.Length + count > max) throw new InvalidDataException("업데이트 응답이 너무 큽니다.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellation)
    {
        var uri = new Uri(url);
        for (int redirect = 0; redirect < 6; redirect++)
        {
            if (!IsAllowedDownload(uri)) throw new InvalidDataException("허용하지 않는 업데이트 주소");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SoundManager-Updater", "1"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("잘못된 업데이트 리다이렉트");
                uri = new Uri(uri, location); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("업데이트 리다이렉트가 너무 많습니다.");
    }

    public static bool IsAllowedDownload(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Port == 443 &&
        uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
}
