using System.Text.Json;
using SoundManager.Updates;

if(args.Length!=2 || args[0] is not ("--local" or "--feed"))throw new ArgumentException("Usage: UpdateCheck (--local <release-dir>|--feed <download-dir>)");
if(args[0]=="--local")
{
    string folder=Path.GetFullPath(args[1]);
    var manifest=UpdateManifest.Verify(await File.ReadAllBytesAsync(Path.Combine(folder,"update.json")),await File.ReadAllTextAsync(Path.Combine(folder,"update.json.sig")));
    if(!UpdateFeed.VerifyFile(Path.Combine(folder,manifest.Windows.File),manifest.Windows) || !UpdateFeed.VerifyFile(Path.Combine(folder,manifest.Android.File),manifest.Android))throw new IOException("Release asset integrity failed");
    Console.WriteLine(JsonSerializer.Serialize(new{version=manifest.Version,signature="valid pinned release key",windowsIntegrity=true,androidIntegrity=true,androidVersionCode=manifest.Android.VersionCode}));
}
else
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));using var http=UpdateFeed.CreateHttpClient();var feed=new UpdateFeed(http);
    var found=await feed.CheckAsync(new Version(0,4,1),timeout.Token)??throw new IOException("No newer signed release found");
    if(found.Manifest.Version!="1.0.0")throw new IOException("Expected released version 1.0.0");
    string target=Path.Combine(Path.GetFullPath(args[1]),found.Manifest.Windows.File);await feed.DownloadAsync(found,target,null,timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new{version=found.Manifest.Version,signature="valid pinned release key",downloadVerified=UpdateFeed.VerifyFile(target,found.Manifest.Windows),bytes=found.Manifest.Windows.Size}));
}
