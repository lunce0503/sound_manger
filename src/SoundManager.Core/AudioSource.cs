using System.Buffers.Binary;

namespace SoundManager.Core;

internal sealed class AudioSource(Guid id, string name, AudioFormat format)
{
    private readonly object gate = new();
    private readonly float[] ring = new float[(format.SampleRate / 2 + 2) * 2];
    private readonly float[] delay = new float[64];
    private int head, count, delayIndex;
    private double phase;
    private float rearLow;
    private bool started;
    private long dropped, underruns;
    private SourceSettings settings = new();
    public Guid Id { get; } = id;
    public SourceSettings Settings { get => Volatile.Read(ref settings); set => Volatile.Write(ref settings, value); }
    private int Capacity => ring.Length / 2;

    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.Length % format.FrameBytes != 0) throw new ArgumentException("불완전한 PCM 프레임");
        lock (gate)
        {
            for (int offset = 0; offset < data.Length; offset += format.FrameBytes)
            {
                if (count == Capacity)
                {
                    head = (head + 1) % Capacity;
                    count--; dropped++; phase = 0;
                }
                int index = (head + count) % Capacity * 2;
                float left = Decode(data[offset..]);
                float right = format.Channels == 1 ? left : Decode(data[(offset + format.BytesPerSample)..]);
                ring[index] = left; ring[index + 1] = right;
                count++;
            }
        }
    }

    private float Decode(ReadOnlySpan<byte> bytes)
    {
        float value = format.Encoding == "pcm16"
            ? BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f
            : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }

    public SourceState Snapshot()
    {
        lock (gate) return new(Id, name, format, Settings, count * 1000d / format.SampleRate, dropped, underruns);
    }

    public void Mix(Span<float> output)
    {
        var options = Settings;
        double angle = options.Azimuth * Math.PI / 180;
        double side = Math.Sin(angle), rear = (1 - Math.Cos(angle)) / 2;
        int earDelay = (int)(Math.Abs(side) * 30);
        float leftGain = (float)(1 - Math.Max(0, side) * 0.65);
        float rightGain = (float)(1 + Math.Min(0, side) * 0.65);
        lock (gate)
        {
            // Rebuffer after an underrun; cap latency at 500 ms by discarding oldest frames.
            if (!started && count < format.SampleRate * 0.04) return;
            started = true;
            // Small bounded adjustment handles independent device clock drift.
            double correction = Math.Clamp((count - format.SampleRate * 0.06) / (format.SampleRate * 20), -0.002, 0.002);
            double step = format.SampleRate / (double)Mixer.SampleRate * (1 + correction);
            for (int i = 0; i < output.Length; i += 2)
            {
                if (count < 2)
                {
                    started = false; phase = 0; underruns++;
                    Array.Clear(delay); rearLow = 0;
                    break;
                }
                int a = head * 2, b = (head + 1) % Capacity * 2;
                float left = ring[a] + (ring[b] - ring[a]) * (float)phase;
                float right = ring[a + 1] + (ring[b + 1] - ring[a + 1]) * (float)phase;
                phase += step;
                int advance = (int)phase;
                int consumed = Math.Min(advance, count);
                head = (head + consumed) % Capacity; count -= consumed; phase -= advance;
                if (options.Muted) continue; // Muted streams still advance in real time.
                if (options.Spatial)
                {
                    float mono = (left + right) / 2;
                    rearLow += 0.18f * (mono - rearLow);
                    mono = mono * (1 - (float)rear * 0.65f) + rearLow * (float)rear * 0.65f;
                    delay[delayIndex] = mono;
                    float far = delay[(delayIndex - earDelay + delay.Length) % delay.Length];
                    delayIndex = (delayIndex + 1) % delay.Length;
                    left = (side > 0 ? far : mono) * leftGain;
                    right = (side < 0 ? far : mono) * rightGain;
                }
                output[i] += left * options.Volume;
                output[i + 1] += right * options.Volume;
            }
        }
    }
}
