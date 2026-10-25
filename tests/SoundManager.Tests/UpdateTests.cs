using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundManager.Updates;

namespace SoundManager.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly RSA key=RSA.Create(2048);
    private readonly string root=Path.Combine(Path.GetTempPath(),"SoundManager-update-tests-"+Guid.NewGuid().ToString("N"));
    private readonly byte[] payload="new application payload"u8.ToArray();
    private UpdateManifest Manifest(string version="0.4.2")=>new(1,version,
        new($"SoundManager-{version}-windows-x64.exe",Convert.ToHexString(SHA256.HashData(payload)),payload.Length),
        new($"SoundManager-{version}-android.apk",new string('a',64),123,9));
    private VerifiedUpdate Signed(UpdateManifest? manifest=null)
    {
        manifest??=Manifest();byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,UpdateManifest.Json);
        return new(manifest,bytes,Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)));
    }
    private string PublicKey=>Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    [Fact] public void SignedManifestRejectsTamperingAndWrongKey()
    {
        var signed=Signed();Assert.Equal("0.4.2",UpdateManifest.Verify(signed.ManifestBytes,signed.Signature,PublicKey).Version);
        signed.ManifestBytes[10]^=1;
        Assert.Throws<CryptographicException>(()=>UpdateManifest.Verify(signed.ManifestBytes,signed.Signature,PublicKey));
        var valid=Signed();Assert.Throws<CryptographicException>(()=>UpdateManifest.Verify(valid.ManifestBytes,valid.Signature));
    }
    [Fact] public void SignedManifestStillRejectsUnsafeAssetAndVersion()
    {
        var invalid=Signed(Manifest() with {Windows=Manifest().Windows with{File="../other.exe"}});
        Assert.Throws<InvalidDataException>(()=>UpdateManifest.Verify(invalid.ManifestBytes,invalid.Signature,PublicKey));
        foreach(string version in new[]{"v1.0.0","1.0.0-beta","1.00.0","../2.0.0"})Assert.Throws<InvalidDataException>(()=>UpdateManifest.ParseVersion(version));
        Assert.True(UpdateManifest.ParseVersion("0.10.0")>UpdateManifest.ParseVersion("0.9.0"));
    }
    [Fact] public async Task FeedSelectsNewestSignedReleaseAndSkipsLegacyPackages()
    {
        var signed=Signed();
        using var http=new HttpClient(new FakeHandler(request=>
        {
            string path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/releases"))return Content("""[{"draft":false,"tag_name":"v9.0.0","assets":[]},{"draft":false,"tag_name":"v0.4.2","assets":[{"name":"update.json"},{"name":"update.json.sig"},{"name":"SoundManager-0.4.2-windows-x64.exe"},{"name":"SoundManager-0.4.2-android.apk"}]}]""");
            return Content(path.EndsWith(".sig")?signed.Signature:Encoding.UTF8.GetString(signed.ManifestBytes));
        }));
        Assert.Equal("0.4.2",(await new UpdateFeed(http,PublicKey).CheckAsync(new Version(0,4,1),CancellationToken.None))!.Manifest.Version);
        Assert.Null(await new UpdateFeed(http,PublicKey).CheckAsync(new Version(9,0,0),CancellationToken.None));
    }
    [Fact] public async Task CorruptDownloadPreservesExistingInstaller()
    {
        Directory.CreateDirectory(root);string target=Path.Combine(root,"pending.exe");File.WriteAllText(target,"previous");
        using var http=new HttpClient(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[payload.Length])}));
        await Assert.ThrowsAsync<CryptographicException>(()=>new UpdateFeed(http).DownloadAsync(Signed(),target,null,CancellationToken.None));
        Assert.Equal("previous",File.ReadAllText(target));Assert.False(File.Exists(target+".download"));
    }
    [Fact] public async Task ValidDownloadIsVerifiedBeforeReplacement()
    {
        string target=Path.Combine(root,"pending.exe");
        using var http=new HttpClient(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)}));
        await new UpdateFeed(http).DownloadAsync(Signed(),target,null,CancellationToken.None);
        Assert.True(UpdateFeed.VerifyFile(target,Manifest().Windows));
    }
    [Fact] public async Task RedirectCannotLeaveTrustedHttpsHosts()
    {
        using var http=new HttpClient(new FakeHandler(_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("http://127.0.0.1/private")}}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new UpdateFeed(http).CheckAsync(new Version(0,4,1),CancellationToken.None));
        Assert.False(UpdateFeed.IsAllowedDownload(new Uri("https://github.com.evil.example/file")));
        Assert.False(UpdateFeed.IsAllowedDownload(new Uri("https://user@github.com/file")));
    }
    [Fact] public void InstalledPointerPreventsDowngradesAndPathEscape()
    {
        string installed=Path.Combine(root,"versions","0.4.2-abcdef");Directory.CreateDirectory(installed);File.WriteAllText(Path.Combine(installed,"SoundManager.Windows.exe"),"");
        InstalledVersion.Record(root,installed,"0.4.2");
        Assert.Equal(Path.Combine(installed,"SoundManager.Windows.exe"),InstalledVersion.NewerExecutable(root,new Version(0,4,1)));
        Assert.Null(InstalledVersion.NewerExecutable(root,new Version(0,4,2)));
        string old=Path.Combine(root,"versions","0.4.1-abcdef");Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"SoundManager.Windows.exe"),"");
        InstalledVersion.Record(root,old,"0.4.1");Assert.Contains("0.4.2",File.ReadAllText(Path.Combine(root,"current.json")));
        InstalledVersion.Record(root,old,"0.4.1",rollback:true);Assert.Null(InstalledVersion.NewerExecutable(root,new Version(0,4,1)));
        File.WriteAllText(Path.Combine(root,"current.json"),JsonSerializer.Serialize(new {Version="9.0.0",Directory=Path.GetTempPath()}));
        Assert.Null(InstalledVersion.NewerExecutable(root,new Version(0,4,1)));
        File.WriteAllText(Path.Combine(root,"current.json"),"null");Assert.Null(InstalledVersion.NewerExecutable(root,new Version(0,4,1)));
    }
    [Fact] public void HandoffRejectsMalformedTokensAndArguments()
    {
        Assert.Throws<ArgumentException>(()=>LauncherOptions.Parse(["--wait-for","0","--update-token",new string('a',32),"--fallback","old.exe"]));
        Assert.Throws<ArgumentException>(()=>LauncherOptions.Marker(root,"../escape","ready"));
        var options=LauncherOptions.Parse(["--wait-for","123","--update-token",new string('a',32),"--fallback","old.exe"]);Assert.Equal(123,options.WaitForPid);
    }
    private static HttpResponseMessage Content(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text)};
    private sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));}
    public void Dispose(){key.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
}
