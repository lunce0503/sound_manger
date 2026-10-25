using System.Text.Json;
using SoundManager.Core;

namespace SoundManager.Server;

// Only device IDs and audio settings belong here. Pairing credentials have their own encrypted store.
public sealed class SourceSettingsStore
{
    private const int MaxDevices = 4096, MaxBytes = 1024 * 1024;
    private readonly string? filePath;
    private readonly object gate = new();
    private Dictionary<Guid, SourceSettings> devices = [];
    private sealed record Document(int Version, Dictionary<Guid, SourceSettings> Devices);

    public SourceSettingsStore(string? path = null)
    {
        filePath = path is null ? null : Path.GetFullPath(path);
        if (filePath is null || !File.Exists(filePath)) return;
        if (new FileInfo(filePath).Length > MaxBytes) throw new IOException("저장된 음원 설정 파일이 너무 큽니다.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(filePath));
        if (document is null || document.Version != 1 || document.Devices is null ||
            document.Devices.Count > MaxDevices || document.Devices.Any(p => p.Key == Guid.Empty || p.Value is null || !p.Value.IsValid))
            throw new JsonException("저장된 음원 설정을 확인하세요. 원본 파일을 보존했습니다.");
        devices = document.Devices;
    }

    public SourceSettings Get(Guid deviceId)
    {
        lock (gate) return devices.GetValueOrDefault(deviceId) ?? new();
    }

    public void Save(Guid deviceId, SourceSettings settings)
    {
        if (deviceId == Guid.Empty || !settings.IsValid) throw new ArgumentException("잘못된 기기 또는 음원 설정");
        lock (gate)
        {
            if (!devices.ContainsKey(deviceId) && devices.Count >= MaxDevices) throw new IOException("저장 가능한 기기 수를 초과했습니다.");
            var updated = new Dictionary<Guid, SourceSettings>(devices) { [deviceId] = settings };
            if (filePath is not null)
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Document(1, updated));
                if (data.Length > MaxBytes) throw new IOException("저장된 음원 설정 파일이 너무 큽니다.");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(data); stream.Flush(flushToDisk: true); }
                    File.Move(temporary, filePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            // A failed disk write must not acknowledge or apply an unpersisted update.
            devices = updated;
        }
    }
}
