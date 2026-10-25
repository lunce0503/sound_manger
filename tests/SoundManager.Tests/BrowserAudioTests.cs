using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Tests;

public sealed class BrowserAudioTests : IAsyncLifetime
{
    private readonly Mixer mixer = new();
    private readonly string key = HubServer.CreateToken();
    private HubServer server = null!;
    private Uri address = null!;
    public async Task InitializeAsync()
    { server = new(mixer, key, 0, "127.0.0.1"); await server.StartAsync(); address = new(server.Addresses.Single()); }
    public Task DisposeAsync() => server.DisposeAsync().AsTask();

    private async Task<ClientWebSocket> Socket(string? origin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/v1/browser-audio" }.Uri, timeout.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private async Task<Guid> Authenticate(ClientWebSocket socket, string? supplied = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, deviceId = Guid.NewGuid(), name = "Tab", format = new AudioFormat(48000, 2, "float32"), key = supplied ?? key });
        await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var response = new byte[1024]; var result = await socket.ReceiveAsync(response.AsMemory(), timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close) return Guid.Empty;
        return JsonDocument.Parse(response.AsMemory(0, result.Count)).RootElement.GetProperty("sourceId").GetGuid();
    }

    [Fact]
    public async Task BrowserTabsAuthenticateInTheFirstFrameAndHaveIndependentServerControls()
    {
        using var one = await Socket(); using var two = await Socket();
        var a = await Authenticate(one); var b = await Authenticate(two);
        Assert.NotEqual(a, b); Assert.Equal(2, mixer.Snapshot().Sources.Length);
        Assert.Equal(SourceUpdateResult.Updated, server.UpdateSource(a, new(Muted: true, DisplayName: "음악 탭")));
        Assert.Equal(SourceUpdateResult.Updated, server.UpdateSource(b, new(Volume: 0.5f)));
        byte[] pcm = new byte[4800 * 8];
        for (int i = 0; i < pcm.Length; i += 4) BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(i), BitConverter.SingleToInt32Bits(0.25f));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await one.SendAsync(pcm.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        await two.SendAsync(pcm.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        while (mixer.Snapshot().Sources.Any(s => s.BufferedMs < 40)) await Task.Delay(10, timeout.Token);
        var output = new float[960]; mixer.Render(output);
        Assert.InRange(output[100], 0.0874f, 0.0876f);
        Assert.Equal("음악 탭", mixer.Snapshot().Sources.Single(s => s.Id == a).Name);
        await one.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        await two.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
    }

    [Fact]
    public async Task InvalidFirstFrameCredentialsNeverRegisterASource()
    {
        using var socket = await Socket();
        Assert.Equal(Guid.Empty, await Authenticate(socket, new string('b', 48)));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.Empty(mixer.Snapshot().Sources);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("chrome-extension://invalid")]
    public async Task OrdinaryWebpagesCannotUseTheBrowserCaptureEndpoint(string? origin)
    { await Assert.ThrowsAsync<WebSocketException>(() => Socket(origin)); Assert.Empty(mixer.Snapshot().Sources); }
}
