using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using SoundManager.Core;

namespace SoundManager.Windows;

internal sealed class AudioSender : IAsyncDisposable
{
    private static readonly byte[] Keepalive = "{\"type\":\"keepalive\"}"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private WasapiLoopbackCapture? capture;
    private Task? sendTask;
    private volatile bool connected;
    public event Action<string>? Failed;
    public event Action<bool>? ConnectionChanged;

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
        var deviceId = ClientDeviceIdentity.Get(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundManager", "client-device-id.txt"));
        var hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, name, format, deviceId), Json);

        capture.DataAvailable += (_, e) =>
        {
            if (!connected) return;
            int chunkSize = 16384 / format.FrameBytes * format.FrameBytes;
            for (int offset = 0; offset < e.BytesRecorded; offset += chunkSize)
                packets.Writer.TryWrite(e.Buffer.AsSpan(offset, Math.Min(chunkSize, e.BytesRecorded - offset)).ToArray());
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && !stop.IsCancellationRequested)
            {
                stop.Cancel();
                Failed?.Invoke("재생 오디오 캡처가 중단되었습니다.");
            }
        };
        await StartTransportAsync(endpoint,key,hello,cancellation);
        capture.StartRecording();
        stop.Token.ThrowIfCancellationRequested();
        cancellation.ThrowIfCancellationRequested();
    }

    internal async Task StartTransportAsync(Uri endpoint,string key,byte[] hello,CancellationToken cancellation)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation);
        var initial = await ConnectAsync(endpoint, key, hello, startup.Token);
        sendTask = RunAsync(initial, endpoint, key, hello);
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri endpoint, string key, byte[] hello, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("X-Sound-Key", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token);
            await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            byte[] ready = new byte[4096];
            var response = await socket.ReceiveAsync(ready.AsMemory(), timeout.Token);
            if (Rejected(socket.CloseStatus)) throw new RejectedException();
            if (response.MessageType != WebSocketMessageType.Text || !response.EndOfMessage)
                throw new IOException("서버가 음원 연결을 완료하지 못했습니다.");
            using var ack = JsonDocument.Parse(ready.AsMemory(0, response.Count));
            if (!ack.RootElement.TryGetProperty("sourceId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new RejectedException();
            return socket;
        }
        catch
        {
            bool denied = socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            socket.Dispose();
            if (denied) throw new RejectedException();
            throw;
        }
    }

    private async Task RunAsync(ClientWebSocket initial, Uri endpoint, string key, byte[] hello)
    {
        ClientWebSocket? socket = initial;
        int retrySeconds = 1;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (socket is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stop.Token).ConfigureAwait(false);
                    try { socket = await ConnectAsync(endpoint, key, hello, stop.Token).ConfigureAwait(false); }
                    catch (Exception error) when (IsTransportError(error))
                    {
                        retrySeconds = Math.Min(retrySeconds * 2, 15);
                        continue;
                    }
                }
                while (packets.Reader.TryRead(out _)) { }
                connected = true;
                ConnectionChanged?.Invoke(true);
                retrySeconds = 1;
                try { await RunSessionAsync(socket).ConfigureAwait(false); }
                finally
                {
                    connected = false;
                    socket.Dispose();
                    socket = null;
                    if (!stop.IsCancellationRequested) ConnectionChanged?.Invoke(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (RejectedException)
        {
            if (!stop.IsCancellationRequested) Failed?.Invoke("서버 인증·음원 형식·연결 한도를 확인하세요.");
        }
        finally { socket?.Dispose(); }
    }

    private async Task RunSessionAsync(ClientWebSocket socket)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Task send = SendAsync(socket, session.Token);
        Task heartbeat = HeartbeatAsync(session.Token);
        var receive = socket.ReceiveAsync(new byte[1024].AsMemory(), session.Token).AsTask();
        Task finished = await Task.WhenAny(send, heartbeat, receive).ConfigureAwait(false);
        try
        {
            await finished.ConfigureAwait(false);
            if (finished == receive && Rejected(socket.CloseStatus)) throw new RejectedException();
        }
        catch (Exception error) when (IsTransportError(error)) { }
        finally
        {
            session.Cancel();
            socket.Abort();
            try { await Task.WhenAll(send, heartbeat, receive).ConfigureAwait(false); }
            catch (Exception error) when (IsTransportError(error)) { }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        await foreach (var packet in packets.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            bool heartbeat = packet.Length == 0;
            await socket.SendAsync((heartbeat ? Keepalive : packet).AsMemory(),
                heartbeat ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false)) packets.Writer.TryWrite([]);
    }

    private static bool Rejected(WebSocketCloseStatus? status) => status is WebSocketCloseStatus.InvalidPayloadData or WebSocketCloseStatus.PolicyViolation;
    private static bool IsTransportError(Exception error) => error is WebSocketException or OperationCanceledException or IOException or JsonException;
    private sealed class RejectedException() : Exception("서버 인증·음원 형식·연결 한도를 확인하세요.");

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        capture?.StopRecording();
        capture?.Dispose();
        packets.Writer.TryComplete();
        if (sendTask is not null) await sendTask;
        stop.Dispose();
    }
}
