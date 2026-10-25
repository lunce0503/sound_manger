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
using SoundManager.Updates;

namespace SoundManager.Windows;

public partial class MainWindow : Window
{
    private readonly Mixer mixer = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly BackgroundWindowLifetime lifetime;
    private readonly SourceListView sourceList;
    private readonly StartupRegistration startupRegistration = new();
    private readonly OutputDeviceStore deviceStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundManager", "output-device.json"));
    private SavedOutputDevice? savedOutput;
    private CancellationTokenSource? automaticStart;
    private Task automaticStartTask = Task.CompletedTask;
    private HubServer? server;
    private WasapiOut? output;
    private MMDevice? outputDevice;
    private AudioSender? sender;
    private ExternalTunnel? tunnel;
    private string? externalAddress;
    private CancellationTokenSource? tunnelStart;
    private readonly CancellationTokenSource shutdown = new();
    private readonly WindowsConnectionStore connections = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SoundManager","connections"));
    private bool busy, closing, refreshingDevices;
    private readonly WindowsUpdates updates;
    private bool updateApplying, updateTick;
    private DateTime nextApply;
    private record DeviceItem(string Id, string Name);

    public MainWindow()
    {
        InitializeComponent();
        sourceList = new SourceListView((id, changes) =>
        {
            var result = server?.UpdateSource(id, changes) ?? SourceUpdateResult.NotFound;
            sourceList!.Refresh(mixer.Snapshot().Sources);
            return result;
        });
        SourceListHost.Children.Add(sourceList);
        try
        {
            savedOutput = deviceStore.Load();
            LoginStartup.IsChecked = startupRegistration.IsEnabled;
            // Opening an update moves the login entry to this version's persistent app folder.
            if (LoginStartup.IsChecked == true) startupRegistration.Enable(Environment.ProcessPath!);
        }
        catch (Exception) { Status.Text = "시작 설정을 불러오지 못했습니다. 시작 설정과 출력 장치를 확인하세요."; }
        UpdateStartupOutput();
        RefreshDevices(this, new RoutedEventArgs());
        try
        {
            var saved=connections.Client();
            if (saved is not null) { RemoteAddress.Text=saved.Address; RemoteKey.Text=saved.Key; }
        }
        catch (Exception) { Status.Text="저장된 연결을 불러오지 못했습니다. 연결 설정에서 다시 인증하세요."; }
        lifetime = new BackgroundWindowLifetime(this, StopForExitAsync, () => Application.Current.Shutdown());
        updates=new WindowsUpdates(typeof(MainWindow).Assembly.GetName().Version!, message=>
        {
            UpdateStatus.Text=message; ApplyUpdateButton.IsEnabled=updates?.Ready==true;
        });
        AutomaticUpdates.IsChecked=updates.Preferences.Automatic; IdleUpdates.IsChecked=updates.Preferences.ApplyWhenIdle;
        ApplyUpdateButton.IsEnabled=updates.Ready;
        UpdateStatus.Text=updates.Ready?$"{updates.AvailableVersion} 업데이트 준비 완료":$"현재 버전 {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        updates.Start(()=>AudioIdle);
        string installedExtension=Path.Combine(WindowsUpdates.Root,"browser-extension");
        if (Directory.Exists(installedExtension))
            try { BrowserExtensionFiles.Prepare(Path.Combine(AppContext.BaseDirectory,"browser-extension"),installedExtension); }
            catch (Exception) { Status.Text="브라우저 확장 갱신을 미뤘습니다. 브라우저 탭 연결에서 다시 준비할 수 있습니다."; }
        timer.Tick += (_, _) => RefreshState(); timer.Start();
    }

    private void RefreshDevices(object sender, RoutedEventArgs e)
    {
        try
        {
            string? previous = (OutputDevices.SelectedItem as DeviceItem)?.Id ?? savedOutput?.Id;
            var devices = ReadOutputDevices();
            refreshingDevices = true;
            try
            {
                OutputDevices.ItemsSource = devices;
                OutputDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == previous);
                if (OutputDevices.SelectedItem is null) OutputDevices.SelectedIndex = 0;
            }
            finally { refreshingDevices = false; }
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ToggleServer(object sender, RoutedEventArgs e)
    {
        automaticStart?.Cancel();
        if (busy || closing || updateApplying) return;
        busy = true;
        try
        {
            if (server is not null) { await StopServer(); Status.Text = "서버 중지됨"; }
            else
            {
                if (this.sender is not null) throw new InvalidOperationException("먼저 음원 전송을 중지하세요.");
                if (OutputDevices.SelectedItem is not DeviceItem selected) throw new InvalidOperationException("출력 장치를 선택하세요.");
                SaveOutputDevice(selected);
                await StartServerAsync(selected, shutdown.Token);
            }
        }
        catch (Exception error) { await StopServer(); Status.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task StartServerAsync(DeviceItem selected, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        outputDevice = enumerator.GetDevice(selected.Id);
        output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 60);
        output.Init(new MixerProvider(mixer));
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null) Dispatcher.BeginInvoke(async () =>
            { await StopServer(); Status.Text = "출력 장치 연결 종료: " + args.Exception.Message; });
        };
        string key = connections.ServerKey();
        var candidate = new HubServer(mixer, key, sourceSettingsPath: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundManager", "source-settings.json"));
        try
        {
            await candidate.StartAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            server = candidate; output.Play();
        }
        catch { await candidate.DisposeAsync(); throw; }
        PairingKey.Text = key; ServerButton.Content = "서버 중지"; OutputDevices.IsEnabled = false;
        RefreshPairAddresses();
        ExternalButton.IsEnabled = true;
        ServerAddress.Text = "같은 네트워크에서는 QR 인증의 주소를 선택하세요.";
        Status.Text = "서버 실행 중 · Android 앱에서 QR을 스캔하세요.";
    }

    private DeviceItem[] ReadOutputDevices()
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try { return devices.Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToArray(); }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    private void RefreshPairAddresses()
    {
        string? previous = PairAddress.SelectedItem as string;
        string[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"http://{a}:17842").Where(IsLocalPairAddress).Distinct().ToArray();
        }
        catch (SocketException) { addresses = []; }
        if (addresses.Length == 0) addresses = ["http://127.0.0.1:17842"];
        if (externalAddress is not null) addresses = [.. addresses, externalAddress];
        PairAddress.ItemsSource = addresses;
        PairAddress.SelectedItem = addresses.Contains(previous) ? previous : addresses[0];
    }

    private void PairingSettingsOpened(object sender, RoutedEventArgs e)
    { if (server is not null) RefreshPairAddresses(); }

    private void SaveOutputDevice(DeviceItem selected)
    {
        var saved = new SavedOutputDevice(selected.Id, selected.Name);
        deviceStore.Save(saved); savedOutput = saved; UpdateStartupOutput();
    }

    private void OutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingDevices || OutputDevices.SelectedItem is not DeviceItem selected) return;
        try { SaveOutputDevice(selected); }
        catch (Exception) { Status.Text = "출력 장치를 저장하지 못했습니다. 시작 설정을 확인하세요."; }
    }

    private void UpdateStartupOutput() => StartupOutput.Text = savedOutput is null
        ? "자동 시작에 사용할 출력 장치를 선택하세요." : "자동 시작 출력: " + savedOutput.Name;

    private void LoginStartupChanged(object sender, RoutedEventArgs e)
    {
        bool enable = LoginStartup.IsChecked == true;
        try
        {
            if (enable)
            {
                if (OutputDevices.SelectedItem is not DeviceItem selected) throw new InvalidOperationException("출력 장치를 먼저 선택하세요.");
                SaveOutputDevice(selected);
                startupRegistration.Enable(Environment.ProcessPath!);
                Status.Text = "다음 Windows 로그인부터 백그라운드에서 서버를 자동 시작합니다.";
            }
            else
            {
                startupRegistration.Disable(); automaticStart?.Cancel();
                Status.Text = "로그인 시 자동 실행을 해제했습니다. 현재 실행 중인 서버는 유지됩니다.";
            }
        }
        catch (Exception error) { LoginStartup.IsChecked = !enable; Status.Text = "시작 설정 저장 실패: " + error.Message; }
    }

    public void StartAutomatically(bool force = false)
    {
        if ((!force && LoginStartup.IsChecked != true) || closing || !automaticStartTask.IsCompleted) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        automaticStart = cancellation;
        automaticStartTask = RunAutomaticStartAsync(cancellation);
    }

    private async Task RunAutomaticStartAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await AutomaticServerStart.RunAsync(async token =>
            {
                if (server is not null) return true;
                if (busy || this.sender is not null || savedOutput is null) return false;
                var selected = ReadOutputDevices().FirstOrDefault(d => d.Id == savedOutput.Id);
                if (selected is null) return false;
                busy = true;
                try
                {
                    refreshingDevices = true;
                    try { OutputDevices.ItemsSource = new[] { selected }; OutputDevices.SelectedItem = selected; }
                    finally { refreshingDevices = false; }
                    await StartServerAsync(selected, token);
                    Status.Text = "로그인 후 서버를 자동 시작했습니다. 백그라운드에서 실행 중입니다.";
                    return true;
                }
                catch { await StopServer(); throw; }
                finally { busy = false; }
            }, () => Status.Text = "서버 자동 시작 대기 중 · 저장된 출력 장치와 연결 상태를 확인하며 재시도합니다.", cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (automaticStart == cancellation) automaticStart = null;
            cancellation.Dispose();
        }
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
        if (busy || closing || updateApplying || server is null) return;
        busy = true;
        if (tunnel is not null)
        {
            try { await StopExternal(); Status.Text = "외부 연결을 종료했습니다. LAN 연결을 사용할 수 있습니다."; }
            finally { busy = false; }
            return;
        }
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
        automaticStart?.Cancel();
        if (busy || closing || updateApplying) return;
        busy = true;
        try
        {
            if (this.sender is not null) { await StopSender(); Status.Text = "음원 전송 중지됨"; }
            else await StartSenderAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task StartSenderAsync()
    {
                if (server is not null) throw new InvalidOperationException("먼저 통합 서버를 중지하세요.");
                var candidate = new AudioSender();
                candidate.Failed += message => Dispatcher.BeginInvoke(async () =>
                { await StopSender(); Status.Text = "전송 종료: " + message; });
                try
                {
                    await candidate.StartAsync(RemoteAddress.Text.Trim(), RemoteKey.Text.Trim(), Environment.MachineName, shutdown.Token);
                    shutdown.Token.ThrowIfCancellationRequested();
                }
                catch { await candidate.DisposeAsync(); throw; }
                this.sender = candidate; SendButton.Content = "음원 전송 중지";
                connections.SaveClient(new PairingLink(RemoteAddress.Text.Trim(),RemoteKey.Text.Trim()));
                Status.Text = "기본 출력 장치의 원본 PCM을 전송 중입니다.";
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
        lifetime.SetStatus(server is not null ? "서버 실행 중" : sender is not null ? "음원 전송 중" : automaticStart is not null ? "자동 시작 대기 중" : "대기 중");
        var state = mixer.Snapshot();
        if (!MasterVolume.IsMouseCaptureWithin) MasterVolume.Value = state.Settings.MasterVolume * 100;
        sourceList.Refresh(state.Sources);
        _=UpdateOnIdleAsync();
    }

    private bool AudioIdle=>!busy&&!closing&&sender is null&&mixer.Snapshot().Sources.Length==0;
    private bool CanAutoApply=>AudioIdle&&!IsVisible&&tunnel is null&&tunnelStart is null&&Application.Current.Windows.Count<=1;
    private async Task UpdateOnIdleAsync()
    {
        if(updateTick||updateApplying||closing)return;
        updateTick=true;
        try
        {
            if(AudioIdle)await updates.DownloadWhenIdleAsync();
            ApplyUpdateButton.IsEnabled=updates.Ready;
            if(updates.MayApplyAutomatically&&CanAutoApply&&DateTime.UtcNow>=nextApply)await ApplyUpdateAsync(true);
        }
        catch(OperationCanceledException) { }
        finally {updateTick=false;}
    }
    private void UpdatePreferencesChanged(object sender,RoutedEventArgs e)
    {
        try {updates.SetPreferences(AutomaticUpdates.IsChecked==true,IdleUpdates.IsChecked==true);}
        catch(Exception) {UpdateStatus.Text="업데이트 설정을 저장하지 못했습니다.";}
    }
    private async void CheckUpdate(object sender,RoutedEventArgs e)
    { if(closing||updateApplying)return;await updates.CheckAsync(AudioIdle); ApplyUpdateButton.IsEnabled=updates.Ready; }
    private async void ApplyUpdate(object sender,RoutedEventArgs e)
    {
        if(!updates.Ready||busy||closing||updateApplying)return;
        string message="앱을 재시작해 업데이트합니다. 연결 및 기기 설정은 유지됩니다.";
        if(!AudioIdle)message+="\n현재 음원 연결과 전송은 잠시 중지됩니다.";
        if(tunnel is not null)message+="\n외부 연결 주소가 변경되므로 휴대폰에서 새 QR로 연결해야 합니다.";
        if(MessageBox.Show(this,message,"업데이트 적용",MessageBoxButton.OKCancel,MessageBoxImage.Information)==MessageBoxResult.OK)await ApplyUpdateAsync(false);
    }
    private async Task ApplyUpdateAsync(bool automatic)
    {
        if(updateApplying||closing||!updates.Ready||busy)return;
        updateApplying=true;
        try
        {
            var resume=new UpdateResume(Guid.NewGuid().ToString("N"),updates.AvailableVersion!,server is not null||automaticStart is not null,sender is not null,tunnel is not null,!IsVisible,mixer.Settings);
            bool prepared=await updates.PrepareAsync(resume,()=>!closing&&!busy&&(!automatic||CanAutoApply)&&(server?.TryBeginUpdate(automatic)??true),()=>resume with{Mix=mixer.Settings,Hidden=!IsVisible});
            if(prepared)await lifetime.ExitAsync();
        }
        catch(Exception) {server?.CancelUpdate();UpdateStatus.Text="업데이트를 적용하지 못했습니다. 현재 앱을 유지하며 다시 시도할 수 있습니다.";}
        finally {updateApplying=false;nextApply=DateTime.UtcNow.AddMinutes(5);}
    }
    internal async Task RestoreUpdateAsync(UpdateResume state,bool rollback)
    {
        if(rollback)updates.MarkFailed(state.Version);
        mixer.Settings=state.Mix; MasterVolume.Value=state.Mix.MasterVolume*100;
        if(state.Server)
        {
            StartAutomatically(force:true);
            if(state.External)
            {
                await automaticStartTask;
                if(server is not null&&!closing)ToggleExternal(this,new RoutedEventArgs());
            }
        }
        else if(state.Sender)
        {
            busy=true;
            try {await StartSenderAsync();}catch(Exception){Status.Text="업데이트 후 전송 연결을 복원하지 못했습니다. 저장된 연결로 다시 시작하세요.";}finally{busy=false;}
        }
    }

    private void OpenBrowserExtension(object sender, RoutedEventArgs e)
    {
        try
        {
            string target = BrowserExtensionFiles.Prepare(Path.Combine(AppContext.BaseDirectory, "browser-extension"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundManager", "browser-extension"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            Status.Text = "Chrome·Edge 확장 관리에서 개발자 모드를 켜고 열린 폴더를 로드하세요.";
        }
        catch (Exception) { Status.Text = "확장 파일을 준비하지 못했습니다. 최신 배포 EXE를 확인하세요."; }
    }

    public void RestoreFromTray() => lifetime.Restore();

    public void AllowSessionEnd() => lifetime.AllowSessionEnd();

    private async void ExitApplication(object sender, RoutedEventArgs e) => await lifetime.ExitAsync();

    private async Task StopForExitAsync()
    {
        closing = true;
        timer.Stop(); shutdown.Cancel();
        await updates.DisposeAsync();
        await automaticStartTask;
        // Starting a server/sender/tunnel may still be awaiting I/O. Cancel and join it
        // before disposing resources, otherwise it could come alive during exit.
        while (busy) await Task.Delay(25);
        try { await StopSender(); }
        finally
        {
            try { await StopServer(); }
            finally { enumerator.Dispose(); shutdown.Dispose(); }
        }
    }
}
