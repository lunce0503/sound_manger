namespace SoundManager.Core;

public sealed class Mixer
{
    public const int SampleRate = 48000;
    public const int MaxSources = 16;
    private readonly object sourcesGate = new();
    private AudioSource[] sources = [];
    private MixSettings settings = new();
    private MixSettings? applied;
    private Biquad[][] filters = [];
    private long clippedSamples;

    public MixSettings Settings
    {
        get => Volatile.Read(ref settings);
        set
        {
            if (!value.IsValid) throw new ArgumentException("EQ/음량 범위를 확인하세요.");
            Volatile.Write(ref settings, value);
        }
    }

    public Guid AddSource(string name, AudioFormat format)
    {
        format.Validate();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64) throw new ArgumentException("음원 이름은 1–64자입니다.");
        lock (sourcesGate)
        {
            if (sources.Length >= MaxSources) throw new InvalidOperationException("최대 16개 음원까지 연결할 수 있습니다.");
            var source = new AudioSource(Guid.NewGuid(), name, format);
            Volatile.Write(ref sources, [.. sources, source]);
            return source.Id;
        }
    }

    public void RemoveSource(Guid id)
    {
        lock (sourcesGate) Volatile.Write(ref sources, sources.Where(s => s.Id != id).ToArray());
    }

    public bool ConfigureSource(Guid id, SourceSettings options)
    {
        if (!options.IsValid) throw new ArgumentException("방향/음량 범위를 확인하세요.");
        var source = Volatile.Read(ref sources).FirstOrDefault(s => s.Id == id);
        if (source is null) return false;
        source.Settings = options;
        return true;
    }

    public void Push(Guid id, ReadOnlySpan<byte> pcm)
    {
        var source = Volatile.Read(ref sources).FirstOrDefault(s => s.Id == id);
        source?.Push(pcm);
    }

    public MixerState Snapshot() => new(Settings,
        Volatile.Read(ref sources).Select(s => s.Snapshot()).ToArray(), Interlocked.Read(ref clippedSamples));

    // Exactly one audio render thread calls Render; control and ingestion are thread safe.
    public void Render(Span<float> stereo)
    {
        if (stereo.Length % 2 != 0) throw new ArgumentException("스테레오 프레임 길이가 필요합니다.");
        stereo.Clear();
        foreach (var source in Volatile.Read(ref sources)) source.Mix(stereo);
        var options = Settings;
        if (applied != options)
        {
            filters = Enumerable.Range(0, 2).Select(_ => new[] {
                new Biquad(120, options.BassDb, 0), new Biquad(4000, options.TrebleDb, 1),
                new Biquad(2500, options.PresenceDb, 2) }).ToArray();
            applied = options;
        }
        for (int i = 0; i < stereo.Length; i++)
        {
            float value = stereo[i];
            foreach (var filter in filters[i % 2]) value = filter.Process(value);
            value *= options.MasterVolume;
            if (Math.Abs(value) > 1) Interlocked.Increment(ref clippedSamples);
            stereo[i] = float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }
    }
}
