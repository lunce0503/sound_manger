using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundManager.Core;
using SoundManager.Server;
using QRCoder;

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
    private ExternalTunnel? tunnel;
    private string? externalAddress;
    private CancellationTokenSource? tunnelStart;
    private readonly CancellationTokenSource shutdown = new();
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
                var addresses = ips.Select(a => $"http://{a}:17842").Where(IsLocalPairAddress).Distinct().ToArray();
                if (addresses.Length == 0) addresses = ["http://127.0.0.1:17842"];
                PairAddress.ItemsSource = addresses; PairAddress.SelectedIndex = 0;
                ExternalButton.IsEnabled = true;
                ServerAddress.Text = "같은 네트워크에서는 아래 주소의 QR을 스캔하세요.";
                Status.Text = "서버 실행 중 · Android 앱에서 QR을 스캔하세요.";
            }
        }
        catch (Exception error) { await StopServer(); Status.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task StopServer()
    {
        tunnelStart?.Cancel();
        await StopExternal();
        var current = server; server = null;
        output?.Stop(); output?.Dispose(); output = null;
        outputDevice?.Dispose(); outputDevice = null;
        if (current is not null) await current.DisposeAsync();
        ServerButton.Content = "서버 시작"; OutputDevices.IsEnabled = true; PairingKey.Clear();
        PairAddress.ItemsSource = null; PairQr.Source = null; PairQr.Visibility = Visibility.Collapsed;
        ExternalButton.IsEnabled = false;
    }

    private void PairAddressChanged(object sender, SelectionChangedEventArgs e) => UpdateQr();

    private static bool IsLocalPairAddress(string address)
    {
        try { _ = PairingLink.NormalizeAddress(address); return true; }
        catch (ArgumentException) { return false; }
    }

    private void UpdateQr()
    {
        if (PairAddress.SelectedItem is not string address || PairingKey.Text.Length == 0)
        { PairQr.Source = null; PairQr.Visibility = Visibility.Collapsed; return; }
        using var data = QRCodeGenerator.GenerateQrCode(new PairingLink(address, PairingKey.Text).Encode(), QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        using var stream = new MemoryStream(code.GetGraphic(8));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PairQr.Source = bitmap; PairQr.Visibility = Visibility.Visible;
    }

    private void CopyPairing(object sender, RoutedEventArgs e)
    {
        try
        {
            if (server is null || PairAddress.SelectedItem is not string address)
                throw new InvalidOperationException("먼저 서버를 시작하세요.");
            Clipboard.SetText(new PairingLink(address, PairingKey.Text).Encode());
            Status.Text = "연결 코드를 복사했습니다. 연결할 기기에만 공유하세요.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void ImportPairing(object sender, RoutedEventArgs e)
    {
        try
        {
            var pair = PairingLink.Parse(PairingInput.Text);
            RemoteAddress.Text = pair.Address; RemoteKey.Text = pair.Key; PairingInput.Clear();
            Status.Text = "연결 정보를 적용했습니다. 음원 전송 시작을 누르세요.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ToggleExternal(object sender, RoutedEventArgs e)
    {
        if (tunnelStart is not null) { tunnelStart.Cancel(); return; }
        if (busy || server is null) return;
        if (tunnel is not null) { await StopExternal(); Status.Text = "외부 연결을 종료했습니다. LAN 연결을 사용할 수 있습니다."; return; }
        busy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        tunnelStart = cancellation;
        ExternalButton.Content = "외부 연결 준비 취소";
        Status.Text = "외부 HTTPS 주소를 준비하고 있습니다…";
        var candidate = new ExternalTunnel(); tunnel = candidate;
        candidate.Stopped += () => Dispatcher.BeginInvoke(async () =>
        {
            if (tunnel == candidate && tunnelStart is null)
            { await StopExternal(); Status.Text = "외부 연결이 종료되었습니다. 네트워크를 확인한 후 다시 켜세요."; }
        });
        try
        {
            string address = await candidate.StartAsync(Path.Combine(AppContext.BaseDirectory, "cloudflared.exe"), cancellation.Token);
            await WaitForExternalAsync(address, PairingKey.Text, cancellation.Token);
            externalAddress = address;
            var addresses = ((string[])PairAddress.ItemsSource).Append(address).ToArray();
            PairAddress.ItemsSource = addresses; PairAddress.SelectedItem = address;
            ExternalButton.Content = "외부 네트워크 연결 끄기";
            Status.Text = "외부 연결 준비 완료 · 휴대폰에서 새 QR을 스캔하세요. 모바일 데이터에서도 연결할 수 있습니다.";
        }
        catch (Exception error)
        {
            await StopExternal();
            Status.Text = error is OperationCanceledException ? "외부 연결 준비를 취소했습니다." : "외부 연결 실패: " + error.Message;
        }
        finally { tunnelStart = null; busy = false; }
    }

    private static async Task WaitForExternalAsync(string address, string key, CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-Sound-Key", key);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(address + "/v1/state", cancellation);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            { cancellation.ThrowIfCancellationRequested(); }
            await Task.Delay(1500, cancellation);
        }
        throw new IOException("외부 주소에 접속할 수 없습니다. 인터넷 연결을 확인하세요.");
    }

    private async Task StopExternal()
    {
        var current = tunnel; tunnel = null;
        if (current is not null) await current.DisposeAsync();
        if (externalAddress is not null && PairAddress.ItemsSource is string[] addresses)
        {
            PairAddress.ItemsSource = addresses.Where(a => a != externalAddress).ToArray();
            PairAddress.SelectedIndex = 0;
        }
        externalAddress = null; ExternalButton.Content = "외부 네트워크 연결 켜기";
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
        e.Cancel = true; closing = true; IsEnabled = false; timer.Stop(); shutdown.Cancel();
        try { await StopSender(); await StopServer(); }
        finally { enumerator.Dispose(); Close(); }
    }
}
