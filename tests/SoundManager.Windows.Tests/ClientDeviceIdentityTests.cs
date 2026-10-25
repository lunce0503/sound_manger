using System.IO;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class ClientDeviceIdentityTests
{
    [Fact]
    public async Task ConcurrentStartsAndLaterVersionsUseTheSameIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoundManager-identity-" + Guid.NewGuid());
        string path = Path.Combine(directory, "client-device-id.txt");
        try
        {
            var identities = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ClientDeviceIdentity.Get(path))));
            Assert.NotEqual(Guid.Empty, identities[0]);
            Assert.All(identities, id => Assert.Equal(identities[0], id));
            Assert.Equal(identities[0], ClientDeviceIdentity.Get(path));
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(path, "damaged");
            Assert.Throws<InvalidDataException>(() => ClientDeviceIdentity.Get(path));
            Assert.Equal("damaged", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
