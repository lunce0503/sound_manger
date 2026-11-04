using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SoundManager.Core;

if (args.Length != 2 || args[0] is not ("--write" or "--verify"))
{
    Console.Error.WriteLine("Usage: SoundManager.Golden (--write|--verify) <fixture-directory>");
    return 2;
}

string directory = Path.GetFullPath(args[1]);
bool write = args[0] == "--write";
if (write) Directory.CreateDirectory(directory);
var scenarios = Scenarios.All().ToArray();
var manifest = new StringBuilder("Sound Manager C# DSP fixtures v1\n");
foreach (var scenario in scenarios)
{
    byte[] data = Generate(scenario);
    // Golden generation itself must be independent of timestamps and UUIDs.
    if (!data.AsSpan().SequenceEqual(Generate(scenario))) throw new InvalidOperationException($"Non-deterministic fixture: {scenario.Name}");
    Check(scenario.Name + ".smg", data);
    manifest.AppendLine($"{Convert.ToHexString(SHA256.HashData(data))}  {scenario.Name}.smg");
}
// Capture the current working-tree baseline, including uncommitted DSP changes.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
foreach (string name in new[] { "AudioSource.cs", "Biquad.cs", "Contracts.cs", "Mixer.cs", "RenderTiming.cs" })
{
    string path = Path.Combine(root, "src/SoundManager.Core", name);
    byte[] source = Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n"));
    manifest.AppendLine($"{Convert.ToHexString(SHA256.HashData(source))}  src/SoundManager.Core/{name}");
}
Check("manifest.txt", Encoding.UTF8.GetBytes(manifest.ToString().Replace("\r\n", "\n")));
Console.WriteLine($"{(write ? "Wrote" : "Verified")} {scenarios.Length} deterministic C# DSP fixtures.");
return 0;

void Check(string name, byte[] data)
{
    string path = Path.Combine(directory, name);
    if (write) File.WriteAllBytes(path, data);
    else if (!File.Exists(path) || !data.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
        throw new InvalidOperationException($"C# baseline changed: {name}. Review before regenerating with --write.");
}

static byte[] Generate(Scenario scenario)
{
    var mixer = new Mixer();
    var ids = new List<Guid>();
    using var bytes = new MemoryStream();
    using var output = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
    output.Write("SMG1"u8);
    output.Write((uint)scenario.Sources.Length);
    foreach (var source in scenario.Sources)
    {
        ids.Add(mixer.AddSource("fixture", source));
        output.Write((uint)source.SampleRate);
        output.Write((uint)source.Channels);
        output.Write(source.Encoding == "pcm16" ? 0u : 1u);
    }
    output.Write((uint)scenario.Events.Length);
    foreach (var action in scenario.Events)
    {
        switch (action)
        {
            case Push push:
                output.Write((byte)0); output.Write((uint)push.Source); output.Write((uint)push.Data.Length); output.Write(push.Data);
                mixer.Push(ids[push.Source], push.Data);
                break;
            case Render render:
                output.Write((byte)1); output.Write((uint)render.Samples);
                var pcm = new float[render.Samples]; mixer.Render(pcm);
                if (pcm.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1)) throw new InvalidOperationException("Invalid C# output");
                foreach (float value in pcm) output.Write(value);
                var state = mixer.Snapshot(); output.Write((ulong)state.ClippedSamples);
                foreach (var source in state.Sources)
                {
                    output.Write(source.BufferedMs); output.Write((ulong)source.DroppedFrames); output.Write((ulong)source.Underruns);
                    var d = source.Diagnostics!;
                    output.Write((ulong)d.ReceivedPackets); output.Write((ulong)d.LatencyTrims); output.Write((ulong)d.LatencyTrimmedFrames);
                }
                break;
            case Configure configure:
                output.Write((byte)2); output.Write((uint)configure.Source);
                output.Write(configure.Settings.Volume); output.Write(configure.Settings.Azimuth);
                output.Write(configure.Settings.Spatial); output.Write(configure.Settings.Muted);
                mixer.ConfigureSource(ids[configure.Source], configure.Settings);
                break;
            case Mix mix:
                output.Write((byte)3); output.Write(mix.Settings.MasterVolume); output.Write(mix.Settings.BassDb);
                output.Write(mix.Settings.TrebleDb); output.Write(mix.Settings.PresenceDb);
                mixer.Settings = mix.Settings;
                break;
            default: throw new InvalidOperationException("Unknown event");
        }
    }
    output.Flush();
    return bytes.ToArray();
}

record Scenario(string Name, AudioFormat[] Sources, object[] Events);
record Push(int Source, byte[] Data);
record Render(int Samples = 960);
record Configure(int Source, SourceSettings Settings);
record Mix(MixSettings Settings);

static class Scenarios
{
    private static AudioFormat Format(int rate = 48000, int channels = 2, string encoding = "pcm16") => new(rate, channels, encoding);

    // Input bytes are shared verbatim with Rust; only C# computes test tones.
    private static byte[] Signal(AudioFormat format, int milliseconds, Func<int, int, float>? signal = null)
    {
        int frames = format.SampleRate * milliseconds / 1000;
        byte[] data = new byte[frames * format.FrameBytes];
        for (int frame = 0; frame < frames; frame++)
        for (int channel = 0; channel < format.Channels; channel++)
        {
            float value = signal?.Invoke(frame, channel) ?? (float)(0.2 * Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 1000) * frame / format.SampleRate));
            var destination = data.AsSpan(frame * format.FrameBytes + channel * format.BytesPerSample);
            if (format.Encoding == "pcm16") BinaryPrimitives.WriteInt16LittleEndian(destination, (short)(value * 32767));
            else BinaryPrimitives.WriteSingleLittleEndian(destination, value);
        }
        return data;
    }

    public static IEnumerable<Scenario> All()
    {
        foreach (int rate in new[] { 8000, 44100, 48000, 192000 })
        foreach (int channels in new[] { 1, 2 })
        foreach (string encoding in new[] { "pcm16", "float32" })
        {
            var format = Format(rate, channels, encoding);
            yield return new($"format-{rate}-{channels}-{encoding}", [format],
                [new Push(0, Signal(format, 100)), new Render(), new Render(1920), new Render(1440)]);
        }
        foreach (string preset in new[] { "flat", "bass", "treble", "soft", "clear" })
        {
            var f = Format();
            yield return new($"eq-{preset}", [f], [new Mix(MixSettings.Preset(preset, 1)), new Push(0, Signal(f, 100)),
                new Render(), new Render(), new Mix(new(0.3f, -12, 12, -12)), new Render()]);
        }
        foreach (float angle in new[] { 0f, 45f, 90f, 180f, 270f, 359f })
        {
            var f = Format();
            yield return new($"spatial-{angle:0}", [f], [new Push(0, Signal(f, 100)),
                new Configure(0, new(Azimuth: angle, Spatial: true)), new Render(), new Render()]);
        }
        var stereo = Format();
        yield return new("mute-volume-stereo", [stereo], [new Push(0, Signal(stereo, 100)), new Configure(0, new(Muted: true)),
            new Render(), new Configure(0, new(Volume: 0.25f)), new Render(), new Configure(0, new(Volume: 2)), new Render()]);
        yield return new("startup-rebuffer", [stereo], [new Push(0, Signal(stereo, 39)), new Render(),
            new Push(0, Signal(stereo, 11)), new Render(9600), new Push(0, Signal(stereo, 10)), new Render(),
            new Push(0, Signal(stereo, 50)), new Render()]);
        foreach (int rate in new[] { 8000, 44100, 48000, 192000 })
        {
            var f = Format(rate);
            yield return new($"latency-trim-{rate}", [f], [new Push(0, Signal(f, 250, (_, _) => -0.5f)),
                new Push(0, Signal(f, 75, (_, _) => 0.5f)), new Render(), new Render()]);
        }
        foreach (int requestMs in new[] { 60, 200 })
            yield return new($"large-request-{requestMs}", [stereo], [new Push(0, Signal(stereo, 400)), new Render(48000 * requestMs / 1000 * 2)]);
        yield return new("overflow", [stereo], [new Push(0, Signal(stereo, 1000)), new Render(), new Render()]);
        var floating = Format(48000, 1, "float32");
        float[] invalid = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1.5f, -1.5f, 0.25f];
        yield return new("float-sanitize", [floating], [new Push(0, Signal(floating, 100, (i, _) => invalid[i % invalid.Length])), new Render(), new Render()]);
        var sources = Enumerable.Range(0, 10).Select(i => Format(i % 2 == 0 ? 44100 : 48000, i % 3 == 0 ? 1 : 2, i % 2 == 0 ? "pcm16" : "float32")).ToArray();
        yield return new("ten-source-mix", sources, sources.Select((f, i) => (object)new Push(i, Signal(f, 100))).Concat([new Render(), new Render(), new Render()]).ToArray());
        var loud = Enumerable.Repeat(stereo, 4).ToArray();
        yield return new("hard-clip", loud, new object[] { new Mix(new(1)) }.Concat(loud.Select((f, i) => (object)new Push(i, Signal(f, 100, (_, _) => 0.9f))))
            .Concat([new Render(), new Mix(new(0)), new Render()]).ToArray());
        var streaming = new List<object> { new Push(0, Signal(stereo, 60)) };
        for (int i = 0; i < 100; i++) { streaming.Add(new Push(0, Signal(stereo, 20))); streaming.Add(new Render(1920)); }
        yield return new("steady-stream", [stereo], streaming.ToArray());
    }
}
