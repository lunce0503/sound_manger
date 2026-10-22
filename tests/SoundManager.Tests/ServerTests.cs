using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly Mixer mixer = new();
    private readonly string token = HubServer.CreateToken();
    private HubServer server = null!;
    private HttpClient http = null!;
    private Uri address = null!;

    public async Task InitializeAsync()
    {
        server = new HubServer(mixer, token, 0, "127.0.0.1"); await server.StartAsync();
        address = new Uri(server.Addresses.Single());
        http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-Sound-Key", token);
    }
    public async Task DisposeAsync() { http.Dispose(); await server.DisposeAsync(); }

    [Fact]
    public async Task RequiresKeyForBothControlAndAudio()
    {
        using var anonymous = new HttpClient { BaseAddress = address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/state")).StatusCode);
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(AudioUri(), timeout.Token));
    }

    [Fact]
    public async Task ControlsValidateBeforeUpdatingMixer()
    {
        var valid = await http.PutAsJsonAsync("/v1/mix", new MixSettings(0.4f, 6));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal(6, mixer.Settings.BassDb);
        var invalid = await http.PutAsJsonAsync("/v1/mix", new MixSettings(2));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal(0.4f, mixer.Settings.MasterVolume);
        var missing = await http.PutAsJsonAsync($"/v1/sources/{Guid.NewGuid()}", new SourceSettings());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task StreamsFragmentedPcmAndRemovesDisconnectedSource()
    {
        using var socket = await ConnectAsync();
        var id = mixer.Snapshot().Sources.Single().Id;
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync($"/v1/sources/{id}", new SourceSettings(Azimuth: 90, Spatial: true))).StatusCode);
        var packet = Enumerable.Repeat((byte)0x22, 9600).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(packet.AsMemory(0, 3001), WebSocketMessageType.Binary, false, timeout.Token);
        await socket.SendAsync(packet.AsMemory(3001), WebSocketMessageType.Binary, true, timeout.Token);
        await UntilAsync(() => mixer.Snapshot().Sources.Single().BufferedMs > 0);
        var output = new float[960]; mixer.Render(output); Assert.Contains(output, x => x != 0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        await UntilAsync(() => mixer.Snapshot().Sources.Length == 0);
    }

    [Fact]
    public async Task TwoIndependentClientsMixAndKeepaliveDoesNotBecomeAudio()
    {
        using var one = await ConnectAsync();
        using var two = await ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packet = Enumerable.Repeat((byte)0x10, 9600).ToArray();
        await one.SendAsync("{\"type\":\"keepalive\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        await one.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        await two.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        await UntilAsync(() => mixer.Snapshot().Sources.All(s => s.BufferedMs >= 40));
        var output = new float[960]; mixer.Render(output);
        Assert.InRange(output[100], 0.17f, 0.18f);
        await one.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        await UntilAsync(() => mixer.Snapshot().Sources.Length == 1);
        await two.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
    }

    [Fact]
    public async Task RejectsPartialPcmFramesAndCleansUp()
    {
        using var socket = await ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(new byte[3].AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        var result = await socket.ReceiveAsync(new byte[1024].AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, socket.CloseStatus);
        await UntilAsync(() => mixer.Snapshot().Sources.Length == 0);
    }

    [Fact]
    public async Task RejectsOversizeMessageWithoutUnboundedAllocation()
    {
        using var socket = await ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(new byte[65536].AsMemory(), WebSocketMessageType.Binary, false, timeout.Token);
        var result = await socket.ReceiveAsync(new byte[1024].AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        await UntilAsync(() => mixer.Snapshot().Sources.Length == 0);
    }

    private Uri AudioUri() => new UriBuilder(address) { Scheme = "ws", Path = "/v1/audio" }.Uri;
    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("X-Sound-Key", token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(AudioUri(), timeout.Token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, "test", new(48000, 2, "pcm16")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // Handshake fragmentation is supported as well as PCM fragmentation.
        await socket.SendAsync(hello.AsMemory(0, 10), WebSocketMessageType.Text, false, timeout.Token);
        await socket.SendAsync(hello.AsMemory(10), WebSocketMessageType.Text, true, timeout.Token);
        var ready = new byte[1024]; var result = await socket.ReceiveAsync(ready.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(JsonDocument.Parse(ready.AsMemory(0, result.Count)).RootElement.TryGetProperty("sourceId", out _));
        return socket;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
