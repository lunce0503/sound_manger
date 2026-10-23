using System.IO;
using System.Text.Json;

namespace SoundManager.Windows;

internal sealed record SavedOutputDevice(string Id, string Name);

internal sealed class OutputDeviceStore(string path)
{
    public SavedOutputDevice? Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<SavedOutputDevice>(File.ReadAllText(path)) : null;

    public void Save(SavedOutputDevice device)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(device));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
