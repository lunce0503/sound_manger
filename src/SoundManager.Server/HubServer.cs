using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoundManager.Core;

namespace SoundManager.Server;

public enum SourceUpdateResult { Updated, NotFound, Invalid, SaveFailed }

public sealed class HubServer(Mixer mixer, string token, int port = 17842, string host = "0.0.0.0",
    string? sourceSettingsPath = null) : IAsyncDisposable
{
    public const int MaxMessageBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private WebApplication? app;
    private readonly SourceSettingsStore savedSettings = new(sourceSettingsPath);
    private readonly object sourcesGate = new();
    private readonly Dictionary<Guid, (Guid? DeviceId, WebSocket Socket)> sessions = [];
    public string[] Addresses => app?.Urls.ToArray() ?? [];
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (app is not null) throw new InvalidOperationException("서버가 이미 실행 중입니다.");
        if (token.Length < 24) throw new ArgumentException("연결 키는 24자 이상이어야 합니다.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4096);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        application.Use(async (context, next) =>
        {
            if (context.Request.Path == "/v1/browser-audio")
            {
                if (!context.WebSockets.IsWebSocketRequest || !IsBrowserOrigin(context.Request.Headers.Origin.ToString()))
                { context.Response.StatusCode = 403; return; }
                await next(context); return;
            }
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Sound-Key"].ToString());
            var expected = Encoding.UTF8.GetBytes(token);
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                context.Response.StatusCode = 401;
                return;
            }
            await next(context);
        });
        application.MapGet("/v1/state", () => mixer.Snapshot());
        application.MapPut("/v1/mix", (MixSettings settings) =>
        {
            if (!settings.IsValid) return Results.BadRequest("음량 0–1, EQ -12–12 dB");
            mixer.Settings = settings;
            return Results.Ok(mixer.Snapshot());
        });
        application.MapPut("/v1/sources/{id:guid}", (Guid id, SourceSettings settings) =>
        {
            if (!settings.IsValid) return UpdateResponse(SourceUpdateResult.Invalid);
            return UpdateResponse(ChangeSource(id, current => settings with { DisplayName = settings.DisplayName ?? current.DisplayName }));
        });
        application.MapPatch("/v1/sources/{id:guid}", (Guid id, SourceSettingsPatch changes) => UpdateResponse(UpdateSource(id, changes)));
        application.Map("/v1/audio", context => HandleAudioAsync(context));
        application.Map("/v1/browser-audio", context => HandleAudioAsync(context, browser: true));
        try { await application.StartAsync(cancellation); app = application; }
        catch { await application.DisposeAsync(); throw; }
    }

    public SourceUpdateResult UpdateSource(Guid id, SourceSettingsPatch changes)
    {
        if (changes.ResetName && changes.DisplayName is not null) return SourceUpdateResult.Invalid;
        return ChangeSource(id, changes.Apply);
    }

    private SourceUpdateResult ChangeSource(Guid id, Func<SourceSettings, SourceSettings> change)
    {
        lock (sourcesGate)
        {
            if (!sessions.TryGetValue(id, out var session) || mixer.GetSourceSettings(id) is not { } current) return SourceUpdateResult.NotFound;
            var updated = change(current);
            if (!updated.IsValid) return SourceUpdateResult.Invalid;
            try { if (session.DeviceId is Guid deviceId) savedSettings.Save(deviceId, updated); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return SourceUpdateResult.SaveFailed; }
            return mixer.ConfigureSource(id, updated) ? SourceUpdateResult.Updated : SourceUpdateResult.NotFound;
        }
    }

    private static IResult UpdateResponse(SourceUpdateResult result) => result switch
    {
        SourceUpdateResult.Updated => Results.Ok(),
        SourceUpdateResult.NotFound => Results.NotFound(),
        SourceUpdateResult.Invalid => Results.BadRequest("음량 0–2, 방향 0 이상 360 미만, 이름 1–64자"),
        _ => Results.Problem("기기 설정을 저장하지 못했습니다. 이전 설정을 유지합니다.", statusCode: 503)
    };

    private static bool IsBrowserOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == "chrome-extension" && uri.Host.Length == 32 && uri.Host.All(c => c is >= 'a' and <= 'p') &&
        uri.Port == -1 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath is "" or "/";

    private async Task HandleAudioAsync(HttpContext context, bool browser = false)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Guid? id = null;
        var buffer = new byte[MaxMessageBytes];
        try
        {
            var first = await ReceiveMessageAsync(socket, buffer, context.RequestAborted);
            if (first.Type != WebSocketMessageType.Text || first.Count > 4096)
                throw new ArgumentException("먼저 JSON 형식 정보를 보내세요.");
            if (browser)
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, first.Count));
                string supplied = document.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token)))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed", timeout.Token);
                    return;
                }
            }
            var hello = JsonSerializer.Deserialize<AudioHello>(buffer.AsSpan(0, first.Count), Json);
            if (hello is null || hello.Version != 1 || hello.Format is null)
                throw new ArgumentException("프로토콜 버전은 1입니다.");
            if (hello.DeviceId == Guid.Empty) throw new ArgumentException("기기 ID는 빈 GUID일 수 없습니다.");
            hello.Format.Validate();
            if (string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 64) throw new ArgumentException("잘못된 기기 이름");
            lock (sourcesGate)
            {
                // A reconnect may arrive before the previous network session times out.
                if (hello.DeviceId is Guid deviceId)
                {
                    var previous = sessions.FirstOrDefault(s => s.Value.DeviceId == deviceId);
                    if (previous.Key != Guid.Empty)
                    {
                        sessions.Remove(previous.Key);
                        mixer.RemoveSource(previous.Key);
                        previous.Value.Socket.Abort();
                    }
                }
                id = mixer.AddSource(hello.Name, hello.Format,
                    hello.DeviceId is Guid stableId ? savedSettings.Get(stableId) : null);
                sessions.Add(id.Value, (hello.DeviceId, socket));
            }
            var ready = JsonSerializer.SerializeToUtf8Bytes(new { sourceId = id }, Json);
            await socket.SendAsync(ready.AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, buffer, context.RequestAborted);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type == WebSocketMessageType.Text &&
                    buffer.AsSpan(0, message.Count).SequenceEqual("{\"type\":\"keepalive\"}"u8)) continue;
                if (message.Type != WebSocketMessageType.Binary || message.Count == 0 ||
                    message.Count % hello.Format.FrameBytes != 0)
                    throw new ArgumentException("완전한 프레임 PCM이 필요합니다.");
                mixer.Push(id.Value, buffer.AsSpan(0, message.Count));
            }
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid audio protocol or source limit", timeout.Token); }
                catch (Exception closeError) when (closeError is WebSocketException or OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            if (id.HasValue) lock (sourcesGate)
            {
                sessions.Remove(id.Value);
                mixer.RemoveSource(id.Value);
            }
        }
    }

    private static async Task<(WebSocketMessageType Type, int Count)> ReceiveMessageAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int count = 0;
        WebSocketMessageType? type = null;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return (result.MessageType, 0);
            type ??= result.MessageType;
            if (type != result.MessageType) throw new ArgumentException("메시지 유형 변경");
            count += result.Count;
            if (result.EndOfMessage) return (result.MessageType, count);
            if (count == buffer.Length) throw new ArgumentException("메시지가 64 KiB를 초과했습니다.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); app = null; }
    }
}
