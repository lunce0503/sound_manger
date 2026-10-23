using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using SoundManager.Core;

namespace SoundManager.Windows;

internal sealed class AudioSender : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private WasapiLoopbackCapture? capture;
    private Task? sendTask;
    public event Action<string>? Failed;

    public async Task StartAsync(string address, string key, string name, CancellationToken cancellation = default)
    {
        var uri = new Uri(PairingLink.NormalizeAddress(address));
        var endpoint = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/v1/audio", Query = "" }.Uri;
        capture = new WasapiLoopbackCapture();
        var wave = capture.WaveFormat;
        bool isFloat = wave.Encoding == WaveFormatEncoding.IeeeFloat ||
            wave is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        string encoding = isFloat && wave.BitsPerSample == 32 ? "float32" :
            !isFloat && wave.BitsPerSample == 16 ? "pcm16" : throw new ArgumentException("현재 출력 장치는 16-bit PCM / 32-bit float 캡처를 지원하지 않습니다.");
        var format = new AudioFormat(wave.SampleRate, wave.Channels, encoding);
        format.Validate();
        socket.Options.SetRequestHeader("X-Sound-Key", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(endpoint, timeout.Token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, name, format), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        byte[] ready = new byte[4096];
        var response = await socket.ReceiveAsync(ready.AsMemory(), timeout.Token);
        if (response.MessageType != WebSocketMessageType.Text || !response.EndOfMessage)
            throw new InvalidOperationException("서버가 음원 연결을 거절했습니다.");
        capture.DataAvailable += (_, e) =>
        {
            int chunkSize = 16384 / format.FrameBytes * format.FrameBytes;
            for (int offset = 0; offset < e.BytesRecorded; offset += chunkSize)
                packets.Writer.TryWrite(e.Buffer.AsSpan(offset, Math.Min(chunkSize, e.BytesRecorded - offset)).ToArray());
        };
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Fail(e.Exception.Message); };
        sendTask = Task.WhenAll(SendAsync(), WatchAsync(), HeartbeatAsync());
        capture.StartRecording();
        stop.Token.ThrowIfCancellationRequested();
    }

    private async Task SendAsync()
    {
        try
        {
            await foreach (var packet in packets.Reader.ReadAllAsync(stop.Token))
            {
                if (packet.Length == 0)
                    await socket.SendAsync("{\"type\":\"keepalive\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, stop.Token);
                else await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        { if (!stop.IsCancellationRequested) Fail(e.Message); }
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try { while (await timer.WaitForNextTickAsync(stop.Token)) packets.Writer.TryWrite([]); }
        catch (OperationCanceledException) { }
    }

    private async Task WatchAsync()
    {
        try
        {
            var buffer = new byte[1024];
            await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
            if (!stop.IsCancellationRequested) Fail("서버가 연결을 종료했습니다.");
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        { if (!stop.IsCancellationRequested) Fail(e.Message); }
    }

    private void Fail(string message) { stop.Cancel(); Failed?.Invoke(message); }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); capture?.StopRecording(); capture?.Dispose();
        packets.Writer.TryComplete(); socket.Abort();
        if (sendTask is not null) await sendTask;
        socket.Dispose(); stop.Dispose();
    }
}
