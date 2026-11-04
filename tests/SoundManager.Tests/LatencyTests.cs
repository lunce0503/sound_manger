using System.Buffers.Binary;
using SoundManager.Core;

namespace SoundManager.Tests;

public sealed class LatencyTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => timestamp;
        public void Advance(double milliseconds) => timestamp += (long)(milliseconds * 1000);
    }

    private static byte[] Pcm(int sampleRate, int milliseconds, short value)
    {
        var pcm = new byte[sampleRate * milliseconds / 1000 * 4];
        for (int i = 0; i < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), value);
        return pcm;
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(192000)]
    public void BacklogIsTrimmedToRecentAudioInOneRender(int sampleRate)
    {
        var mixer = new Mixer(); var id = mixer.AddSource("phone", new(sampleRate, 2, "pcm16"));
        mixer.Push(id, Pcm(sampleRate, 250, -16000));
        mixer.Push(id, Pcm(sampleRate, 75, 16000));
        var output = new float[960]; mixer.Render(output);
        var source = mixer.Snapshot().Sources.Single();
        Assert.InRange(source.BufferedMs, 59, 61);
        Assert.All(output, sample => Assert.InRange(sample, 0.33f, 0.35f));
        var d = Assert.IsType<SourceDiagnostics>(source.Diagnostics);
        Assert.Equal(1, d.LatencyTrims);
        Assert.Equal(source.DroppedFrames, d.LatencyTrimmedFrames);
        Assert.InRange(d.LatencyTrimmedFrames * 1000d / sampleRate, 253, 256);
        Assert.Equal(0, source.Underruns);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    public void TrimmingPreservesLargeOutputRequestAndReserve(int requestMs)
    {
        var mixer = new Mixer(); var id = mixer.AddSource("phone", new(48000, 2, "pcm16"));
        mixer.Push(id, Pcm(48000, 400, 16000));
        var output = new float[48000 * requestMs / 1000 * 2]; mixer.Render(output);
        var source = mixer.Snapshot().Sources.Single();
        Assert.Equal(0, source.Underruns);
        Assert.InRange(source.BufferedMs, 59, 61);
        Assert.All(output, sample => Assert.True(sample > 0.3f));
        Assert.Equal(1, source.Diagnostics!.LatencyTrims);
    }

    [Fact]
    public void NormalStreamingDoesNotRepeatedlyTrimOrUnderrun()
    {
        var clock = new ManualClock(); var mixer = new Mixer(clock);
        var id = mixer.AddSource("phone", new(48000, 2, "pcm16"));
        mixer.Push(id, Pcm(48000, 60, 16000));
        var packet = Pcm(48000, 20, 16000); var output = new float[1920];
        for (int i = 0; i < 500; i++)
        {
            clock.Advance(20); mixer.Push(id, packet); mixer.Render(output);
        }
        var state = mixer.Snapshot(); var source = state.Sources.Single();
        Assert.Equal(0, source.DroppedFrames); Assert.Equal(0, source.Underruns);
        Assert.Equal(0, source.Diagnostics!.LatencyTrims);
        Assert.Equal(0, state.Diagnostics!.LateRenderCalls);
        Assert.InRange(source.BufferedMs, 40, 65);
    }

    [Fact]
    public void ReceiveAndRenderGapsAreMeasuredIndependentlyOnMonotonicClock()
    {
        var clock = new ManualClock(); var mixer = new Mixer(clock);
        var id = mixer.AddSource("phone", new(48000, 2, "pcm16"));
        Assert.Null(mixer.Snapshot().Sources.Single().Diagnostics!.LastReceiveAgoMs);
        Assert.Null(mixer.Snapshot().Diagnostics!.LastRenderAgoMs);
        mixer.Push(id, Pcm(48000, 60, 16000)); mixer.Render(new float[960]); // 10 ms
        clock.Advance(10); mixer.Render(new float[1920]); // next interval should be 20 ms
        clock.Advance(20); mixer.Push(id, Pcm(48000, 20, 16000)); mixer.Render(new float[1920]);
        var regular = mixer.Snapshot();
        Assert.Equal(0, regular.Diagnostics!.LateRenderCalls);
        clock.Advance(180); mixer.Push(id, Pcm(48000, 20, 16000));
        mixer.Render(new float[960]);
        clock.Advance(5);
        var state = mixer.Snapshot(); var receive = state.Sources.Single().Diagnostics!;
        Assert.Equal(3, receive.ReceivedPackets);
        Assert.Equal(20, receive.LastPacketAudioMs);
        Assert.Equal(180, receive.LastReceiveIntervalMs);
        Assert.Equal(180, receive.MaxReceiveIntervalMs);
        Assert.Equal(160, receive.MaxReceiveExcessMs);
        Assert.Equal(5, receive.LastReceiveAgoMs);
        Assert.Equal(4, state.Diagnostics!.RenderCalls);
        Assert.Equal(180, state.Diagnostics.LastRenderIntervalMs);
        Assert.Equal(160, state.Diagnostics.MaxRenderDelayMs);
        Assert.Equal(1, state.Diagnostics.LateRenderCalls);
        Assert.Equal(5, state.Diagnostics.LastRenderAgoMs);
    }

    [Fact]
    public void UnderrunStillRebuffersAndMutedSourceStillRecoversLatency()
    {
        var mixer = new Mixer(); var id = mixer.AddSource("phone", new(48000, 2, "pcm16"), new(Muted: true));
        mixer.Push(id, Pcm(48000, 50, 16000)); mixer.Render(new float[9600]);
        Assert.Equal(1, mixer.Snapshot().Sources.Single().Underruns);
        mixer.Push(id, Pcm(48000, 10, 16000)); mixer.Render(new float[960]);
        Assert.InRange(mixer.Snapshot().Sources.Single().BufferedMs, 9, 11);
        mixer.Push(id, Pcm(48000, 320, 16000));
        var output = new float[960]; mixer.Render(output);
        Assert.All(output, sample => Assert.Equal(0, sample));
        Assert.InRange(mixer.Snapshot().Sources.Single().BufferedMs, 59, 61);
        Assert.Equal(1, mixer.Snapshot().Sources.Single().Diagnostics!.LatencyTrims);
    }
}
