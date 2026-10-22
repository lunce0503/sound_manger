using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

if (args.Length != 2 || args[0] != "--tunnel-exe")
{
    Console.Error.WriteLine("Usage: SoundManager.NetworkCheck --tunnel-exe <verified cloudflared.exe>");
    return 2;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
var mixer = new Mixer();
string key = HubServer.CreateToken();
await using var server = new HubServer(mixer, key, 17842, "127.0.0.1");
await server.StartAsync(deadline.Token);
await using var tunnel = new ExternalTunnel();
string address = await tunnel.StartAsync(Path.GetFullPath(args[1]), deadline.Token);
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(8) };
http.DefaultRequestHeaders.Add("X-Sound-Key", key);
HttpStatusCode status = 0;
for (int attempt = 0; attempt < 24; attempt++)
{
    try { using var response = await http.GetAsync("/v1/state", deadline.Token); status = response.StatusCode; }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { deadline.Token.ThrowIfCancellationRequested(); }
    if (status == HttpStatusCode.OK) break;
    await Task.Delay(1500, deadline.Token);
}
if (status != HttpStatusCode.OK) throw new IOException("External HTTPS state endpoint was not reachable.");
using var unauthenticated = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(address) };
using var forbidden = await unauthenticated.GetAsync("/v1/state", deadline.Token);
if (forbidden.StatusCode != HttpStatusCode.Unauthorized) throw new IOException("External endpoint did not require authentication.");
using var update = await http.PutAsJsonAsync("/v1/mix", new MixSettings(0.5f, 6), deadline.Token);
update.EnsureSuccessStatusCode();
using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("X-Sound-Key", key);
await socket.ConnectAsync(new UriBuilder(address) { Scheme = "wss", Path = "/v1/audio" }.Uri, deadline.Token);
var hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, "synthetic-network-check", new(48000, 2, "pcm16")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
var ready = new byte[1024];
var acknowledgement = await socket.ReceiveAsync(ready.AsMemory(), deadline.Token);
if (acknowledgement.MessageType != WebSocketMessageType.Text) throw new IOException("WSS audio handshake failed.");
var pcm = Enumerable.Repeat((byte)0x10, 19200).ToArray();
await socket.SendAsync(pcm.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
for (int attempt = 0; attempt < 40 && mixer.Snapshot().Sources.Single().BufferedMs < 40; attempt++)
    await Task.Delay(100, deadline.Token);
if (mixer.Snapshot().Sources.Single().BufferedMs < 40) throw new IOException("WSS audio did not arrive.");
var output = new float[960]; mixer.Render(output);
if (!output.Any(v => v > 0) || output.Any(v => !float.IsFinite(v))) throw new IOException("Received audio could not be mixed.");
await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check complete", deadline.Token);
Console.WriteLine(JsonSerializer.Serialize(new
{
    httpsStatus = (int)status, unauthenticatedStatus = (int)forbidden.StatusCode,
    webSocket = "wss", pcmBytesReceived = pcm.Length, renderedSamples = output.Length,
    mixControl = mixer.Settings.BassDb == 6 && mixer.Settings.MasterVolume == 0.5f,
    hardwarePlayback = false
}));
return 0;
