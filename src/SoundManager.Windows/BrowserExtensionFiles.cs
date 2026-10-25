using System.IO;
using System.Text.Json;

namespace SoundManager.Windows;

internal static class BrowserExtensionFiles
{
    public static string Prepare(string source, string destination)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "manifest.json")));
        if (manifest.RootElement.GetProperty("name").GetString() != "Sound Manager Tabs") throw new InvalidDataException("잘못된 브라우저 확장");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(f=>Path.GetFileName(f)=="manifest.json"?1:0))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target+".tmp", overwrite: true);
            File.Move(target+".tmp",target,overwrite:true);
        }
        return destination;
    }
}
