using System.Text.Json.Serialization;

namespace SoundManager.Core;

public sealed record AudioFormat(int SampleRate, int Channels, string Encoding)
{
    [JsonIgnore] public int BytesPerSample => Encoding == "pcm16" ? 2 : 4;
    [JsonIgnore] public int FrameBytes => Channels * BytesPerSample;
    public void Validate()
    {
        if (SampleRate is < 8000 or > 192000 || Channels is < 1 or > 2 ||
            Encoding is not ("pcm16" or "float32"))
            throw new ArgumentException("지원 형식: 8–192 kHz, 모노/스테레오, pcm16/float32 LE");
    }
}

public sealed record AudioHello(int Version, string Name, AudioFormat Format);
public sealed record MixSettings(float MasterVolume = 0.7f, float BassDb = 0,
    float TrebleDb = 0, float PresenceDb = 0)
{
    [JsonIgnore] public bool IsValid => float.IsFinite(MasterVolume) && MasterVolume is >= 0 and <= 1 &&
        float.IsFinite(BassDb) && BassDb is >= -12 and <= 12 &&
        float.IsFinite(TrebleDb) && TrebleDb is >= -12 and <= 12 &&
        float.IsFinite(PresenceDb) && PresenceDb is >= -12 and <= 12;

    public static MixSettings Preset(string name, float volume = 0.7f) => name switch
    {
        "bass" => new(volume, 6, 0, 0),
        "treble" => new(volume, 0, 6, 0),
        "soft" => new(volume, 2, -4, -2),
        "clear" => new(volume, -2, 3, 4),
        "flat" => new(volume),
        _ => throw new ArgumentException("알 수 없는 프리셋")
    };
}

public sealed record SourceSettings(float Volume = 1, float Azimuth = 0,
    bool Spatial = false, bool Muted = false)
{
    [JsonIgnore] public bool IsValid => float.IsFinite(Volume) && Volume is >= 0 and <= 2 &&
        float.IsFinite(Azimuth) && Azimuth is >= 0 and < 360;
}

public sealed record SourceState(Guid Id, string Name, AudioFormat Format,
    SourceSettings Settings, double BufferedMs, long DroppedFrames, long Underruns);
public sealed record MixerState(MixSettings Settings, SourceState[] Sources, long ClippedSamples);
