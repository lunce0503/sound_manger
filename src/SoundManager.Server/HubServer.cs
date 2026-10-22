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

public sealed class HubServer(Mixer mixer, string token, int port = 17842, string host = "0.0.0.0") : IAsyncDisposable
{
    public const int MaxMessageBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private WebApplication? app;
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
            if (!settings.IsValid) return Results.BadRequest("음량 0–2, 방향 0 이상 360 미만");
            return mixer.ConfigureSource(id, settings) ? Results.Ok() : Results.NotFound();
        });
        application.Map("/v1/audio", HandleAudioAsync);
        try { await application.StartAsync(cancellation); app = application; }
        catch { await application.DisposeAsync(); throw; }
    }

    private async Task HandleAudioAsync(HttpContext context)
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
            var hello = JsonSerializer.Deserialize<AudioHello>(buffer.AsSpan(0, first.Count), Json);
            if (hello is null || hello.Version != 1 || hello.Format is null)
                throw new ArgumentException("프로토콜 버전은 1입니다.");
            id = mixer.AddSource(hello.Name, hello.Format);
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
        finally { if (id.HasValue) mixer.RemoveSource(id.Value); }
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
