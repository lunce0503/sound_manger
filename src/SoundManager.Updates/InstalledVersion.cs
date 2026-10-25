using System.Text.Json;

namespace SoundManager.Updates;

public static class InstalledVersion
{
    private sealed record Pointer(string Version, string Directory);
    public static string? NewerExecutable(string root, Version current)
    {
        try
        {
            string marker = Path.Combine(root, "current.json"); if (!File.Exists(marker)) return null;
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(marker));
            if (pointer is null || pointer.Version is null || pointer.Directory is null) return null;
            var version = UpdateManifest.ParseVersion(pointer.Version);
            string directory = Path.GetFullPath(pointer.Directory), versions = Path.GetFullPath(Path.Combine(root, "versions")) + Path.DirectorySeparatorChar;
            if (version <= current || !directory.StartsWith(versions, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).StartsWith(pointer.Version + "-", StringComparison.Ordinal) == false) return null;
            string executable = Path.Combine(directory, "SoundManager.Windows.exe"); return File.Exists(executable) ? executable : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException) { return null; }
    }

    public static void Record(string root, string directory, string version, bool rollback = false)
    {
        string versions = Path.GetFullPath(Path.Combine(root, "versions")) + Path.DirectorySeparatorChar;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!directory.StartsWith(versions, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith(version + "-", StringComparison.Ordinal)) return;
        if (!rollback && NewerExecutable(root, UpdateManifest.ParseVersion(version)) is not null) return;
        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, "current.json.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Pointer(version, directory)));
        File.Move(temporary, Path.Combine(root, "current.json"), overwrite: true);
    }
}
