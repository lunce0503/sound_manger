using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using SoundManager.Core;
using SoundManager.Server;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class StartupTests
{
    [Fact]
    public void LoginRegistrationQuotesPathsUpdatesVersionsAndRemovesOnlyItsOwnValue()
    {
        string registryPath = @"Software\SoundManager.Tests\" + Guid.NewGuid();
        string directory = Path.Combine(Path.GetTempPath(), "Sound Manager startup test " + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string exe = Path.Combine(directory, "SoundManager.Windows.exe");
            File.WriteAllBytes(exe, []);
            var registration = new StartupRegistration(registryPath);
            Assert.False(registration.IsEnabled);
            registration.Enable(exe);
            Assert.True(new StartupRegistration(registryPath).IsEnabled);
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true)!;
            Assert.Equal($"\"{exe}\" --startup", key.GetValue("SoundManager"));
            key.SetValue("OtherApplication", "preserve this");

            string update = Path.Combine(directory, "updated app.exe");
            File.WriteAllBytes(update, []);
            registration.Enable(update);
            Assert.Equal($"\"{update}\" --startup", key.GetValue("SoundManager"));
            Assert.Throws<ArgumentException>(() => registration.Enable("relative.exe"));
            Assert.Equal($"\"{update}\" --startup", key.GetValue("SoundManager"));

            registration.Disable(); registration.Disable();
            Assert.False(registration.IsEnabled);
            Assert.Equal("preserve this", key.GetValue("OtherApplication"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavedBluetoothEndpointSurvivesAppRecreationAndRejectsCorruption()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoundManager-device-test-" + Guid.NewGuid());
        string path = Path.Combine(directory, "output-device.json");
        try
        {
            var store = new OutputDeviceStore(path);
            Assert.Null(store.Load());
            var device = new SavedOutputDevice("{0.0.0.00000000}.test-endpoint", "내 Bluetooth 이어폰");
            store.Save(device);
            Assert.Equal(device, new OutputDeviceStore(path).Load());
            Assert.False(File.Exists(path + ".tmp"));
            File.WriteAllText(path, "broken settings");
            Assert.Throws<JsonException>(() => store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public Task LoginWaitsForTheDeviceAndTransientFailureThenStartsServerWithoutShowingWindow() =>
        BackgroundLifetimeTests.OnDesktop(async () =>
        {
            var window = new Window(); // Login startup never calls Show or flashes a window.
            var mixer = new Mixer();
            string key = HubServer.CreateToken();
            await using var server = new HubServer(mixer, key, 0, "127.0.0.1");
            int attempts = 0, waits = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AutomaticServerStart.RunAsync(async token =>
            {
                attempts++;
                if (attempts == 1) return false; // Saved Bluetooth endpoint not present yet.
                if (attempts == 2) throw new IOException("Audio service not ready");
                await server.StartAsync(token);
                return true;
            }, () => waits++, cancellation.Token, TimeSpan.FromMilliseconds(10));
            Assert.Equal(3, attempts);
            Assert.Equal(2, waits);
            Assert.False(window.IsVisible);
            using var http = new HttpClient { BaseAddress = new Uri(server.Addresses.Single()) };
            http.DefaultRequestHeaders.Add("X-Sound-Key", key);
            using var response = await http.GetAsync("/v1/state", cancellation.Token);
            response.EnsureSuccessStatusCode();
            window.Close();
        });

    [Fact]
    public Task DelayedLoginLaunchSignalsStartupWithoutRestoringTheWindow() => BackgroundLifetimeTests.OnDesktop(async () =>
    {
        // Use a dedicated name so this test never touches the user's running application.
        string name = "Local\\SoundManager.DelayedStartup.Test." + Guid.NewGuid();
        using var owner = new SingleInstance(name);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int restores = 0;
        owner.Listen(() => Interlocked.Increment(ref restores), () => started.TrySetResult());
        await Task.Run(() =>
        {
            using var login = new SingleInstance(name);
            Assert.False(login.IsFirst);
            login.RequestStartup();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, restores);
    });

    [Fact]
    public async Task DisableOrExitCancelsADeviceWaitAndPreventsFurtherStartAttempts()
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        Task start = AutomaticServerStart.RunAsync(_ => { attempts++; return Task.FromResult(false); },
            () => waiting.TrySetResult(), cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExitCancellationInterruptsAnInFlightServerStart()
    {
        using var cancellation = new CancellationTokenSource();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopped = false;
        Task start = AutomaticServerStart.RunAsync(async token =>
        {
            starting.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
            return true;
        }, () => throw new InvalidOperationException("Cancelled start must not retry"), cancellation.Token);
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(stopped);
    }
}
