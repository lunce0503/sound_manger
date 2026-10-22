using System.Buffers.Binary;
using SoundManager.Core;

namespace SoundManager.Tests;

public sealed class MixerTests
{
    private static byte[] Pcm(int frames, float left, float? right = null)
    {
        int channels = right.HasValue ? 2 : 1;
        byte[] data = new byte[frames * channels * 2];
        for (int i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * channels * 2), (short)(left * 32767));
            if (right.HasValue) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), (short)(right.Value * 32767));
        }
        return data;
    }

    [Fact]
    public void MixesDifferentRatesAndChannelsOnServer()
    {
        var mixer = new Mixer { Settings = new(1) };
        var mono = mixer.AddSource("phone", new(44100, 1, "pcm16"));
        var stereo = mixer.AddSource("pc", new(48000, 2, "pcm16"));
        mixer.Push(mono, Pcm(4410, 0.2f)); mixer.Push(stereo, Pcm(4800, 0.1f, 0.3f));
        var output = new float[960]; mixer.Render(output);
        Assert.InRange(output[100], 0.299f, 0.301f);
        Assert.InRange(output[101], 0.499f, 0.501f);
        Assert.True(mixer.Snapshot().Sources.All(s => s.BufferedMs < 100));
    }

    [Fact]
    public void StereoIsPreservedWhenSpatialModeIsDisabled()
    {
        var mixer = new Mixer { Settings = new(1) };
        var id = mixer.AddSource("stereo", new(48000, 2, "pcm16"));
        mixer.Push(id, Pcm(4800, 0.25f, -0.5f));
        var output = new float[960]; mixer.Render(output);
        Assert.InRange(output[100], 0.249f, 0.251f);
        Assert.InRange(output[101], -0.501f, -0.499f);
    }

    [Fact]
    public void MutingConsumesAudioAndDoesNotReplayBufferedSound()
    {
        var mixer = new Mixer { Settings = new(1) };
        var id = mixer.AddSource("mute", new(48000, 1, "pcm16"));
        mixer.Push(id, Pcm(4800, 0.5f));
        mixer.ConfigureSource(id, new(Muted: true));
        var output = new float[960]; mixer.Render(output);
        Assert.All(output, s => Assert.Equal(0, s));
        Assert.True(mixer.Snapshot().Sources[0].BufferedMs < 100);
        mixer.ConfigureSource(id, new()); mixer.Render(output);
        Assert.InRange(output[100], 0.499f, 0.501f);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(270, false)]
    public void SpatialDirectionUsesCorrectEar(float angle, bool right)
    {
        var mixer = new Mixer { Settings = new(1) };
        var id = mixer.AddSource("spatial", new(48000, 1, "pcm16"));
        mixer.Push(id, Pcm(4800, 0.5f)); mixer.ConfigureSource(id, new(Azimuth: angle, Spatial: true));
        var output = new float[960]; mixer.Render(output);
        if (right) Assert.True(output[401] > output[400] * 2);
        else Assert.True(output[400] > output[401] * 2);
    }

    [Fact]
    public void OverflowDropsOldestAndBoundsMemory()
    {
        var mixer = new Mixer(); var id = mixer.AddSource("slow", new(48000, 1, "pcm16"));
        mixer.Push(id, Pcm(48000, 0.1f));
        var state = mixer.Snapshot().Sources[0];
        Assert.InRange(state.BufferedMs, 499, 501); Assert.True(state.DroppedFrames > 23000);
    }

    [Fact]
    public void UnderrunReturnsSilenceThenRebuffers()
    {
        var mixer = new Mixer { Settings = new(1) };
        var id = mixer.AddSource("bursty", new(48000, 1, "pcm16"));
        mixer.Push(id, Pcm(2400, 0.5f));
        var output = new float[9600]; mixer.Render(output);
        Assert.InRange(output[100], 0.499f, 0.501f);
        Assert.All(output.Skip(6000), s => Assert.Equal(0, s));
        Assert.Equal(1, mixer.Snapshot().Sources[0].Underruns);
        mixer.Push(id, Pcm(480, 0.5f)); mixer.Render(output);
        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void NonFiniteFloatAudioCannotPoisonOutput()
    {
        var mixer = new Mixer { Settings = new(1) };
        var id = mixer.AddSource("invalid", new(48000, 1, "float32"));
        byte[] data = new byte[4800 * 4];
        for (int i = 0; i < data.Length; i += 4)
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i), BitConverter.SingleToInt32Bits(float.NaN));
        mixer.Push(id, data); var output = new float[960]; mixer.Render(output);
        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void MasterVolumeAndClippingBoundOutput()
    {
        var mixer = new Mixer { Settings = new(1) };
        for (int i = 0; i < 4; i++)
        {
            var id = mixer.AddSource($"loud{i}", new(48000, 1, "pcm16")); mixer.Push(id, Pcm(4800, 0.9f));
        }
        var output = new float[960]; mixer.Render(output);
        Assert.All(output, s => Assert.Equal(1, s)); Assert.Equal(960, mixer.Snapshot().ClippedSamples);
        mixer.Settings = new(0); mixer.Render(output); Assert.All(output, s => Assert.Equal(0, s));
    }

    private static double ToneRms(int frequency, string preset, float angle = 0, bool spatial = false)
    {
        var mixer = new Mixer { Settings = MixSettings.Preset(preset, 1) };
        var id = mixer.AddSource("tone", new(48000, 1, "pcm16"));
        mixer.ConfigureSource(id, new(Azimuth: angle, Spatial: spatial));
        byte[] data = new byte[16000 * 2];
        for (int i = 0; i < 16000; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2),
            (short)(4000 * Math.Sin(2 * Math.PI * frequency * i / 48000)));
        mixer.Push(id, data); var output = new float[20000]; mixer.Render(output);
        return Math.Sqrt(output.Skip(4000).Select(x => (double)x * x).Average());
    }

    [Fact]
    public void EqualizerChangesIntendedFrequencyBands()
    {
        Assert.True(ToneRms(60, "bass") > ToneRms(60, "flat") * 1.7);
        Assert.True(ToneRms(10000, "treble") > ToneRms(10000, "flat") * 1.7);
        Assert.True(ToneRms(10000, "soft") < ToneRms(10000, "flat") * 0.75);
        Assert.True(ToneRms(2500, "clear") > ToneRms(2500, "flat") * 1.4);
    }

    [Fact]
    public void RearDirectionHasDifferentSpectralCueThanFront()
    {
        Assert.True(ToneRms(10000, "flat", 180, true) < ToneRms(10000, "flat", 0, true) * 0.65);
    }

    [Fact]
    public void RejectsInvalidFormatsControlsAndPartialFrames()
    {
        var mixer = new Mixer();
        Assert.Throws<ArgumentException>(() => mixer.AddSource("bad", new(48000, 6, "float32")));
        Assert.Throws<ArgumentException>(() => mixer.Settings = new(MasterVolume: float.NaN));
        Assert.Throws<ArgumentException>(() => mixer.Settings = new(BassDb: 13));
        var id = mixer.AddSource("valid", new(48000, 2, "pcm16"));
        Assert.Throws<ArgumentException>(() => mixer.Push(id, new byte[3]));
        Assert.Throws<ArgumentException>(() => mixer.ConfigureSource(id, new(Azimuth: 360)));
        mixer.RemoveSource(id); Assert.Empty(mixer.Snapshot().Sources);
    }

    [Fact]
    public void SourceLimitCanBeReusedAfterDisconnect()
    {
        var mixer = new Mixer();
        Guid first = Guid.Empty;
        for (int i = 0; i < Mixer.MaxSources; i++)
        {
            var id = mixer.AddSource($"source{i}", new(48000, 1, "pcm16"));
            if (i == 0) first = id;
        }
        Assert.Throws<InvalidOperationException>(() => mixer.AddSource("overflow", new(48000, 1, "pcm16")));
        mixer.RemoveSource(first); mixer.AddSource("replacement", new(48000, 1, "pcm16"));
        Assert.Equal(Mixer.MaxSources, mixer.Snapshot().Sources.Length);
    }

    [Fact]
    public async Task ConcurrentIngestControlAndRenderRemainFinite()
    {
        var mixer = new Mixer(); var id = mixer.AddSource("concurrent", new(48000, 1, "pcm16"));
        var packet = Pcm(960, 0.2f);
        await Task.WhenAll(Task.Run(() => { for (int i=0; i<200; i++) mixer.Push(id, packet); }),
            Task.Run(() => { for (int i=0; i<200; i++) { mixer.ConfigureSource(id, new(Azimuth: i, Spatial: true)); _ = mixer.Snapshot(); } }),
            Task.Run(() => { var output = new float[960]; for (int i=0; i<200; i++) { mixer.Render(output); Assert.All(output, x => Assert.True(float.IsFinite(x))); } }));
    }
}
