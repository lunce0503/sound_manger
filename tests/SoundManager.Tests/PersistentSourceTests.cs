using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Tests;

public sealed class PersistentSourceTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SoundManager-sources-" + Guid.NewGuid());
    private readonly string key = HubServer.CreateToken();
    private string SettingsPath => Path.Combine(directory, "source-settings.json");
    private Mixer mixer = null!;
    private HubServer server = null!;
    private HttpClient http = null!;
    public Task InitializeAsync() => StartServerAsync();

    private async Task StartServerAsync(string? path = null)
    {
        mixer = new Mixer();
        server = new HubServer(mixer, key, 0, "127.0.0.1", path ?? SettingsPath);
        await server.StartAsync();
        http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-Sound-Key", key);
    }

    public async Task DisposeAsync()
    {
        http.Dispose(); await server.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task<(ClientWebSocket Socket, Guid Id)> ConnectAsync(Guid? deviceId, string name = "same device name")
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("X-Sound-Key", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new UriBuilder(http.BaseAddress!) { Scheme = "ws", Path = "/v1/audio" }.Uri, timeout.Token);
        byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, name, new(48000, 2, "pcm16"), deviceId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var ready = new byte[1024]; var result = await socket.ReceiveAsync(ready.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Guid id = JsonDocument.Parse(ready.AsMemory(0, result.Count)).RootElement.GetProperty("sourceId").GetGuid();
        return (socket, id);
    }

    private async Task ConfigureAsync(Guid id, SourceSettings settings)
    {
        using var response = await http.PutAsJsonAsync($"/v1/sources/{id}", settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task DisconnectAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        socket.Dispose();
    }

    private SourceState State(Guid id) => mixer.Snapshot().Sources.Single(s => s.Id == id);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectRestoresVolumePositionSpatialAndMuteBeforeFirstPcm(bool abruptDisconnect)
    {
        Guid device = Guid.NewGuid();
        var original = await ConnectAsync(device);
        var expected = new SourceSettings(0.35f, 275, Spatial: true, Muted: true);
        await ConfigureAsync(original.Id, expected);
        if (abruptDisconnect) { original.Socket.Abort(); original.Socket.Dispose(); }
        else await DisconnectAsync(original.Socket);
        var reconnected = await ConnectAsync(device, "renamed phone");
        using var socket = reconnected.Socket;
        Assert.NotEqual(original.Id, reconnected.Id);
        Assert.Equal(expected, State(reconnected.Id).Settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.SendAsync(Enumerable.Repeat((byte)0x22, 9600).ToArray().AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        while (State(reconnected.Id).BufferedMs == 0) await Task.Delay(10, timeout.Token);
        var output = new float[960]; mixer.Render(output);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task RestartRestoresEachDeviceIndependentlyEvenWithIdenticalNames()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var one = await ConnectAsync(a); var two = await ConnectAsync(b);
        var first = new SourceSettings(0.25f, 90, Spatial: true);
        var second = new SourceSettings(1.5f, 180, Muted: true);
        await ConfigureAsync(one.Id, first); await ConfigureAsync(two.Id, second);
        await DisconnectAsync(one.Socket);
        Assert.Equal(second, State(two.Id).Settings);
        await DisconnectAsync(two.Socket);
        http.Dispose(); await server.DisposeAsync();
        await StartServerAsync();
        var restoredOne = await ConnectAsync(a); var restoredTwo = await ConnectAsync(b);
        using var socketA = restoredOne.Socket; using var socketB = restoredTwo.Socket;
        Assert.Equal(first, State(restoredOne.Id).Settings);
        Assert.Equal(second, State(restoredTwo.Id).Settings);
        Assert.Equal(0.7f, mixer.Settings.MasterVolume);
    }

    [Fact]
    public async Task FastReconnectReplacesStaleConnectionWithoutRemovingItsNewSession()
    {
        Guid device = Guid.NewGuid();
        var old = await ConnectAsync(device); using var stale = old.Socket;
        var expected = new SourceSettings(0.4f, 225, true, true);
        await ConfigureAsync(old.Id, expected);
        var current = await ConnectAsync(device); using var live = current.Socket;
        Assert.Single(mixer.Snapshot().Sources);
        Assert.Equal(expected, State(current.Id).Settings);
        await ConfigureAsync(current.Id, expected with { Muted = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await stale.ReceiveAsync(new byte[1024].AsMemory(), timeout.Token); }
        catch (WebSocketException) { }
        Assert.Equal(expected with { Muted = false }, State(current.Id).Settings);
        Assert.Equal(new SourceSettings(0.4f, 225, true, false), new SourceSettingsStore(SettingsPath).Get(device));
    }

    [Fact]
    public async Task LegacyClientsRemainCompatibleAndDoNotBorrowSettingsByName()
    {
        var known = await ConnectAsync(Guid.NewGuid());
        await ConfigureAsync(known.Id, new(0.1f, 270, true, true));
        await DisconnectAsync(known.Socket);
        var legacy = await ConnectAsync(null); using var socket = legacy.Socket;
        Assert.Equal(new SourceSettings(), State(legacy.Id).Settings);
    }

    [Fact]
    public async Task FailedSaveDoesNotApplyOrAcknowledgeAnUnpersistedSetting()
    {
        http.Dispose(); await server.DisposeAsync();
        Directory.CreateDirectory(directory);
        string blocker = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocker, "block");
        await StartServerAsync(Path.Combine(blocker, "source-settings.json"));
        var connection = await ConnectAsync(Guid.NewGuid()); using var socket = connection.Socket;
        using var response = await http.PutAsJsonAsync($"/v1/sources/{connection.Id}", new SourceSettings(0.2f, 80, true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new SourceSettings(), State(connection.Id).Settings);
    }

    [Fact]
    public async Task ConcurrentUpdatesLeaveDiskAndActiveSettingsInAgreement()
    {
        Guid device = Guid.NewGuid(); var connection = await ConnectAsync(device); using var socket = connection.Socket;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => ConfigureAsync(connection.Id, new SourceSettings(i / 10f, i * 20, true))));
        Assert.Equal(State(connection.Id).Settings, new SourceSettingsStore(SettingsPath).Get(device));
        using var response = await http.PutAsJsonAsync($"/v1/sources/{connection.Id}", new SourceSettings(3));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(State(connection.Id).Settings, new SourceSettingsStore(SettingsPath).Get(device));
    }

    [Fact]
    public void CorruptStoreIsReportedWithoutOverwritingSavedData()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "broken settings");
        Assert.Throws<JsonException>(() => new SourceSettingsStore(SettingsPath));
        Assert.Equal("broken settings", File.ReadAllText(SettingsPath));
    }
}
