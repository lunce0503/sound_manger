using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SoundManager.Core;
using SoundManager.Server;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class BackgroundLifetimeTests
{
    [Fact]
    public Task ClosePreservesLiveControlAndAudioAndRestoreReusesTheWindow() => OnDesktop(async () =>
    {
        var mixer = new Mixer();
        string key = HubServer.CreateToken();
        await using var server = new HubServer(mixer, key, 0, "127.0.0.1");
        await server.StartAsync();
        var address = new Uri(server.Addresses.Single());
        using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Add("X-Sound-Key", key);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Sound-Key", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/v1/audio" }.Uri, timeout.Token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new AudioHello(1, "background test", new AudioFormat(48000, 2, "pcm16")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        await socket.ReceiveAsync(new byte[1024].AsMemory(), timeout.Token);

        var window = TestWindow();
        int stops = 0, exits = 0;
        using var lifetime = new BackgroundWindowLifetime(window, async () =>
        { stops++; await server.DisposeAsync(); }, () => exits++, createTray: false);
        try
        {
            window.Show();
            window.Close();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, stops);
            using var response = await http.PutAsJsonAsync("/v1/mix", new MixSettings(0.4f, 6), timeout.Token);
            response.EnsureSuccessStatusCode();
            Assert.Equal(6, mixer.Settings.BassDb);

            var pcm = Enumerable.Repeat((byte)0x10, 9600).ToArray();
            await socket.SendAsync(pcm.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            while (mixer.Snapshot().Sources.Single().BufferedMs < 40) await Task.Delay(10, timeout.Token);
            var output = new float[960];
            mixer.Render(output);
            Assert.Contains(output, sample => sample != 0);
            using var state = await http.GetAsync("/v1/state", timeout.Token);
            state.EnsureSuccessStatusCode();

            lifetime.Restore();
            Assert.True(window.IsVisible);
            Assert.Equal(0, stops);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            await lifetime.ExitAsync();
            Assert.False(window.IsVisible);
            Assert.Equal(1, stops);
            Assert.Equal(1, exits);
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/v1/state"));
        }
        finally { lifetime.AllowSessionEnd(); window.Close(); }
    });

    [Fact]
    public Task RepeatedExitWaitsForCleanupOnceAndCannotReopenDuringExit() => OnDesktop(async () =>
    {
        var window = TestWindow();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stops = 0, exits = 0, closed = 0;
        window.Closed += (_, _) => closed++;
        using var lifetime = new BackgroundWindowLifetime(window, () => { stops++; return cleanup.Task; }, () => exits++, false);
        try
        {
            window.Show(); window.Close();
            Task first = lifetime.ExitAsync();
            Assert.Same(first, lifetime.ExitAsync());
            lifetime.Restore(); window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, closed);
            Assert.Equal(0, exits);
            cleanup.SetResult();
            await first;
            Assert.Equal(1, closed);
            Assert.Equal(1, stops);
            Assert.Equal(1, exits);
        }
        finally { lifetime.AllowSessionEnd(); window.Close(); }
    });

    [Fact]
    public Task SecondLaunchSignalsTheHiddenOriginalInsteadOfOwningAnotherInstance() => OnDesktop(async () =>
    {
        string name = "Local\\SoundManager.Test." + Guid.NewGuid();
        using var first = new SingleInstance(name);
        Assert.True(first.IsFirst);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var window = TestWindow();
        using var lifetime = new BackgroundWindowLifetime(window, () => Task.CompletedTask, () => { }, false);
        try
        {
            window.Show(); window.Close();
            first.Listen(() => dispatcher.BeginInvoke(() => { lifetime.Restore(); restored.TrySetResult(); }));
            await Task.Run(() =>
            {
                using var second = new SingleInstance(name);
                Assert.False(second.IsFirst);
                second.RequestRestore();
            });
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(window.IsVisible);
            await lifetime.ExitAsync();
        }
        finally { lifetime.AllowSessionEnd(); window.Close(); }
    });

    [Fact]
    public Task WindowsSessionEndCanCloseWithoutBeingHidden() => OnDesktop(() =>
    {
        var window = TestWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        using var lifetime = new BackgroundWindowLifetime(window, () => Task.CompletedTask, () => { });
        window.Show();
        lifetime.AllowSessionEnd(); window.Close();
        Assert.True(closed);
        return Task.CompletedTask;
    });

    private static Window TestWindow() => new()
    { Width = 100, Height = 100, Left = -10000, Top = -10000, ShowInTaskbar = false };

    internal static Task OnDesktop(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
