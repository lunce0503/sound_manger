using System.IO;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class BrowserExtensionFilesTests
{
    [Fact]
    public void UpdatingTheInstalledFolderPreservesItsPathAndPublicExtensionIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoundManager-extension-" + Guid.NewGuid());
        string source = Path.Combine(directory, "bundle"), target = Path.Combine(directory, "installed");
        Directory.CreateDirectory(source);
        try
        {
            string manifest = "{\"name\":\"Sound Manager Tabs\",\"key\":\"stable-public-key\",\"version\":\"1\"}";
            File.WriteAllText(Path.Combine(source, "manifest.json"), manifest);
            File.WriteAllText(Path.Combine(source, "popup.js"), "first version");
            Assert.Equal(target, BrowserExtensionFiles.Prepare(source, target));
            File.WriteAllText(Path.Combine(source, "popup.js"), "second version");
            Assert.Equal(target, BrowserExtensionFiles.Prepare(source, target));
            Assert.Equal("second version", File.ReadAllText(Path.Combine(target, "popup.js")));
            Assert.Equal(manifest, File.ReadAllText(Path.Combine(target, "manifest.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
