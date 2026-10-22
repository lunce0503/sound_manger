using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Windows;

public partial class MainWindow : Window
{
    private readonly Mixer mixer = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HubServer? server;
    private WasapiOut? output;
    private MMDevice? outputDevice;
    private AudioSender? sender;
    private bool busy, closing;
    private record DeviceItem(string Id, string Name);

    public MainWindow()
    {
        InitializeComponent();
        RefreshDevices(this, new RoutedEventArgs());
        timer.Tick += (_, _) => RefreshState(); timer.Start(); Closing += OnClosing;
    }

    private void RefreshDevices(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            OutputDevices.ItemsSource = devices.Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToArray();
            foreach (var device in devices) device.Dispose();
            OutputDevices.SelectedIndex = 0;
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ToggleServer(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            if (server is not null) { await StopServer(); Status.Text = "서버 중지됨"; }
            else
            {
                if (this.sender is not null) throw new InvalidOperationException("먼저 음원 전송을 중지하세요.");
                if (OutputDevices.SelectedItem is not DeviceItem selected) throw new InvalidOperationException("출력 장치를 선택하세요.");
                outputDevice = enumerator.GetDevice(selected.Id);
                output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 60);
                output.Init(new MixerProvider(mixer));
                output.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception is not null) Dispatcher.BeginInvoke(async () =>
                    { await StopServer(); Status.Text = "출력 장치 연결 종료: " + args.Exception.Message; });
                };
                string key = HubServer.CreateToken();
                var candidate = new HubServer(mixer, key);
                try { await candidate.StartAsync(); server = candidate; output.Play(); }
                catch { await candidate.DisposeAsync(); throw; }
                PairingKey.Text = key; ServerButton.Content = "서버 중지"; OutputDevices.IsEnabled = false;
                var ips = Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork);
                ServerAddress.Text = string.Join("  /  ", ips.Select(a => $"http://{a}:17842"));
                Status.Text = "서버 실행 중 · Android 앱에 주소와 연결 키를 입력하세요.";
            }
        }
        catch (Exception error) { await StopServer(); Status.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task StopServer()
    {
        var current = server; server = null;
        output?.Stop(); output?.Dispose(); output = null;
        outputDevice?.Dispose(); outputDevice = null;
        if (current is not null) await current.DisposeAsync();
        ServerButton.Content = "서버 시작"; OutputDevices.IsEnabled = true; PairingKey.Clear();
    }

    private async void ToggleSender(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            if (this.sender is not null) { await StopSender(); Status.Text = "음원 전송 중지됨"; }
            else
            {
                if (server is not null) throw new InvalidOperationException("먼저 통합 서버를 중지하세요.");
                var candidate = new AudioSender();
                candidate.Failed += message => Dispatcher.BeginInvoke(async () =>
                { await StopSender(); Status.Text = "전송 종료: " + message; });
                try { await candidate.StartAsync(RemoteAddress.Text.Trim(), RemoteKey.Text.Trim(), Environment.MachineName); }
                catch { await candidate.DisposeAsync(); throw; }
                this.sender = candidate; SendButton.Content = "음원 전송 중지";
                Status.Text = "기본 출력 장치의 원본 PCM을 전송 중입니다.";
            }
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task StopSender()
    {
        var current = sender; sender = null;
        if (current is not null) await current.DisposeAsync();
        SendButton.Content = "음원 전송 시작";
    }

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { mixer.Settings = mixer.Settings with { MasterVolume = (float)e.NewValue / 100 }; }

    private void SetPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string name)
        { mixer.Settings = MixSettings.Preset(name, mixer.Settings.MasterVolume); Status.Text = $"EQ: {button.Content}"; }
    }

    private void RefreshState()
    {
        var state = mixer.Snapshot();
        if (!MasterVolume.IsMouseCaptureWithin) MasterVolume.Value = state.Settings.MasterVolume * 100;
        SourceSummary.Text = state.Sources.Length == 0 ? "연결 음원 없음" :
            string.Join("\n", state.Sources.Select(s => $"{s.Name} · {s.Format.SampleRate} Hz · {s.Format.Encoding} · {s.BufferedMs:F0} ms · 방향 {s.Settings.Azimuth:F0}°"));
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; IsEnabled = false; timer.Stop();
        try { await StopSender(); await StopServer(); }
        finally { enumerator.Dispose(); Close(); }
    }
}
