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

public sealed record AudioHello(int Version, string Name, AudioFormat Format, Guid? DeviceId = null);
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
    bool Spatial = false, bool Muted = false, string? DisplayName = null, bool AutoGain = false)
{
    [JsonIgnore] public bool IsValid => float.IsFinite(Volume) && Volume is >= 0 and <= 2 &&
        float.IsFinite(Azimuth) && Azimuth is >= 0 and < 360 &&
        (DisplayName is null || !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 64 && !DisplayName.Any(char.IsControl));
}

public sealed record SourceSettingsPatch(float? Volume = null, float? Azimuth = null,
    bool? Spatial = null, bool? Muted = null, string? DisplayName = null, bool ResetName = false, bool? AutoGain = null)
{
    public SourceSettings Apply(SourceSettings current) => new(Volume ?? current.Volume, Azimuth ?? current.Azimuth,
        Spatial ?? current.Spatial, Muted ?? current.Muted, ResetName ? null : DisplayName ?? current.DisplayName, AutoGain ?? current.AutoGain);
}

public sealed record ProcessingSettings(bool LimiterEnabled = true, bool ClockCorrection = true,
    float PeakCeiling = .98f, float TargetDbfs = -18, float MaxGainDb = 12,
    float AttackMs = 10, float ReleaseMs = 500, float GateDbfs = -60);

public sealed record SourceState(Guid Id, string Name, AudioFormat Format,
    SourceSettings Settings, double BufferedMs, long DroppedFrames, long Underruns,
    SourceDiagnostics? Diagnostics = null);
public sealed record MixerState(MixSettings Settings, SourceState[] Sources, long ClippedSamples,
    RenderDiagnostics? Diagnostics = null, ProcessingSettings? Processing = null);

public sealed record SourceDiagnostics(long ReceivedPackets, double LastPacketAudioMs,
    double? LastReceiveIntervalMs, double MaxReceiveIntervalMs, double MaxReceiveExcessMs,
    double? LastReceiveAgoMs, double LastIngestMs, double MaxIngestMs, double PeakBufferedMs,
    long LatencyTrims, long LatencyTrimmedFrames, double TargetBufferMs, double TrimThresholdMs);

public sealed record RenderDiagnostics(long RenderCalls, double LastRequestedAudioMs,
    double? LastRenderIntervalMs, double MaxRenderIntervalMs, double MaxRenderDelayMs,
    double? LastRenderAgoMs, double LastRenderWorkMs, double MaxRenderWorkMs, long LateRenderCalls);
