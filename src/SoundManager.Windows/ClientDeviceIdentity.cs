using System.IO;

namespace SoundManager.Windows;

internal static class ClientDeviceIdentity
{
    public static Guid Get(string path)
    {
        if (File.Exists(path)) return Parse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Guid id = Guid.NewGuid();
            File.WriteAllText(temporary, id.ToString("D"));
            try { File.Move(temporary, path, overwrite: false); return id; }
            catch (IOException) when (File.Exists(path)) { return Parse(path); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Guid Parse(string path) => Guid.TryParse(File.ReadAllText(path), out var id) && id != Guid.Empty
        ? id : throw new InvalidDataException("저장된 기기 ID를 읽지 못했습니다. 원본 파일을 보존했습니다.");
}
