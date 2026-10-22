using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Tests;
public sealed class WindowsConnectionTests
{
    [Fact]
    public void ServerIdentityAndClientPairingSurviveNewStoreWithoutPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory=Path.Combine(Path.GetTempPath(),"sound-manager-secrets-"+Guid.NewGuid());
        try
        {
            var first=new WindowsConnectionStore(directory); string key=first.ServerKey();
            first.SaveClient(new PairingLink("https://example.trycloudflare.com",key));
            var second=new WindowsConnectionStore(directory);
            Assert.Equal(key,second.ServerKey()); Assert.Equal(key,second.Client()!.Key);
            Assert.DoesNotContain(key,System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory,"server-key.dpapi"))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}
