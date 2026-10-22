using System.Runtime.InteropServices;
using NAudio.Wave;
using SoundManager.Core;

namespace SoundManager.Windows;

internal sealed class MixerProvider(Mixer mixer) : IWaveProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Mixer.SampleRate, 2);
    public int Read(byte[] buffer, int offset, int count)
    {
        int bytes = count - count % 8;
        mixer.Render(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, bytes)));
        return bytes;
    }
}
