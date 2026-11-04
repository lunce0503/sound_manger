using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Windows;

public partial class SourceListView : UserControl
{
    private readonly ObservableCollection<Row> rows = [];
    private readonly Func<Guid, SourceSettingsPatch, Task<SourceUpdateResult>> update;
    private bool refreshing;
    private RenderDiagnostics? renderDiagnostics;
    internal sealed class Row(Guid id) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; private set; } = "";
        public SourceSettings Settings { get; private set; } = new();
        public SourceState? State { get; private set; }
        public bool Muted => Settings.Muted;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(SourceState state)
        {
            State = state; Name = state.Name; Settings = state.Settings;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(Muted)));
        }
    }

    public SourceListView(Func<Guid, SourceSettingsPatch, SourceUpdateResult> update)
        :this((id,patch)=>Task.FromResult(update(id,patch))) { }

    public SourceListView(Func<Guid, SourceSettingsPatch, Task<SourceUpdateResult>> update)
    {
        InitializeComponent(); this.update = update; List.ItemsSource = rows;
    }

    public void Refresh(SourceState[] sources, RenderDiagnostics? diagnostics = null)
    {
        renderDiagnostics = diagnostics;
        refreshing = true;
        try
        {
            foreach (var row in rows.Where(r => !sources.Any(s => s.Id == r.Id)).ToArray()) rows.Remove(row);
            foreach (var source in sources)
            {
                var row = rows.FirstOrDefault(r => r.Id == source.Id);
                if (row is null) { row = new(source.Id); row.Set(source); rows.Add(row); }
                else row.Set(source);
            }
            Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { refreshing = false; }
    }

    private async void MuteChanged(object sender, RoutedEventArgs e)
    {
        if (!refreshing && sender is CheckBox { DataContext: Row row } toggle)
            await Submit(row, new(Muted: toggle.IsChecked == true));
    }

    private async Task<bool> Submit(Row row, SourceSettingsPatch changes)
    {
        var result = await update(row.Id, changes);
        if (result == SourceUpdateResult.Updated) return true;
        MessageBox.Show(result switch
        {
            SourceUpdateResult.NotFound => "연결이 끊겼습니다. 다시 연결한 뒤 조절하세요.",
            SourceUpdateResult.Invalid => "이름과 조절 범위를 확인하세요.",
            _ => "설정을 저장하지 못했습니다. 이전 설정을 유지합니다."
        }, "기기 설정", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Row row } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        AddMenu(menu, "음량 조절", () => ShowVolume(row));
        AddMenu(menu, "이름 변경", () => ShowRename(row));
        AddMenu(menu, "360° 방향 설정", () => ShowDirection(row));
        AddMenu(menu, "통신·재생 진단", () => ShowDiagnostics(row));
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static void AddMenu(ContextMenu menu, string title, Action action)
    { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }

    private Window Dialog(string title, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(24) };
        var window = new Window
        {
            Title = title, Content = body, Width = 400, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(24, 34, 50)), Foreground = Brushes.White,
            FontFamily = new FontFamily("Malgun Gothic"), Owner = Window.GetWindow(this)
        };
        body.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.Bold, Margin = new(0, 0, 0, 14) });
        return window;
    }

    private static void Buttons(Window window, StackPanel body, Func<Task<bool>> apply)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "취소", Padding = new(18, 8, 18, 8), Margin = new(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        var save = new Button { Content = "적용", Padding = new(18, 8, 18, 8), IsDefault = true };
        save.Click += async (_, _) => { save.IsEnabled=false;try{if(await apply())window.Close();}finally{save.IsEnabled=true;} };
        buttons.Children.Add(cancel); buttons.Children.Add(save); body.Children.Add(buttons);
    }

    private void ShowVolume(Row row)
    {
        var window = Dialog("음량 조절", out var body);
        body.Children.Add(new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var value = new TextBlock { FontSize = 20 };
        var slider = new Slider { Minimum = 0, Maximum = 200, Value = row.Settings.Volume * 100, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(0, 12, 0, 0) };
        slider.ValueChanged += (_, _) => value.Text = $"{slider.Value:F0}%"; value.Text = $"{slider.Value:F0}%";
        body.Children.Add(value); body.Children.Add(slider);
        Buttons(window, body, () => Submit(row, new(Volume: (float)slider.Value / 100)));
        window.ShowDialog();
    }

    private void ShowRename(Row row)
    {
        var window = Dialog("이름 변경", out var body);
        var input = new TextBox { Text = row.Name, MaxLength = 64, Padding = new(10), Margin = new(0, 8, 0, 8) };
        body.Children.Add(input);
        var reset = new Button { Content = "원래 이름으로", Padding = new(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += async (_, _) => { if (await Submit(row, new(ResetName: true))) window.Close(); };
        body.Children.Add(reset);
        Buttons(window, body, () => Submit(row, new(DisplayName: input.Text.Trim())));
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        window.ShowDialog();
    }

    private void ShowDirection(Row row)
    {
        var window = Dialog("360° 방향 설정", out var body);
        body.Children.Add(new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var spatial = new CheckBox { Content = "가상 방향 사용", IsChecked = row.Settings.Spatial, Foreground = Brushes.White };
        body.Children.Add(spatial);
        var value = new TextBlock { FontSize = 24, Margin = new(0, 18, 0, 8) };
        var slider = new Slider { Minimum = 0, Maximum = 359, Value = row.Settings.Azimuth, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, _) => value.Text = $"{slider.Value:F0}°"; value.Text = $"{slider.Value:F0}°";
        body.Children.Add(value); body.Children.Add(slider);
        body.Children.Add(new TextBlock { Text = "0° 앞 · 90° 오른쪽 · 180° 뒤 · 270° 왼쪽", Margin = new(0, 10, 0, 0) });
        Buttons(window, body, () => Submit(row, new(Azimuth: (float)slider.Value, Spatial: spatial.IsChecked == true)));
        window.ShowDialog();
    }

    private void ShowDiagnostics(Row row)
    {
        var window = Dialog("통신·재생 진단", out var body);
        window.Width = 700; window.SizeToContent = SizeToContent.Manual; window.Height = 650;
        var text = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 465, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0)
        };
        body.Children.Add(text);
        void RefreshDiagnostics() => text.Text = rows.Contains(row) && row.State is { } state
            ? FormatDiagnostics(state, renderDiagnostics) : "연결이 끊겼습니다. 다시 연결한 기기의 진단을 여세요.";
        var copy = new Button { Content = "진단 복사", Padding = new(14, 8, 14, 8), Margin = new(0, 12, 0, 0) };
        copy.Click += (_, _) =>
        {
            if (rows.Contains(row)) Clipboard.SetText(text.Text + "\n\n" + JsonSerializer.Serialize(
                new { source = row.State, render = renderDiagnostics }, new JsonSerializerOptions { WriteIndented = true }));
        };
        body.Children.Add(copy);
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refresh.Tick += (_, _) => RefreshDiagnostics();
        window.Closed += (_, _) => refresh.Stop();
        RefreshDiagnostics(); refresh.Start(); window.ShowDialog();
    }

    internal static string FormatDiagnostics(SourceState source, RenderDiagnostics? render)
    {
        static string Ms(double? value) => value is { } number ? $"{number:F1} ms" : "측정 대기";
        var d = source.Diagnostics;
        string text = $"{source.Name} · {source.Format.SampleRate} Hz · {source.Format.Encoding}\n\n" +
            $"서버 재생 대기: {Ms(source.BufferedMs)} (네트워크 왕복 지연이 아닙니다)\n" +
            $"버퍼 부족: {source.Underruns}회 · 전체 폐기: {source.DroppedFrames} 프레임\n";
        if (d is not null) text +=
            $"목표: {Ms(d.TargetBufferMs)} · 축소 기준: {Ms(d.TrimThresholdMs)} + 큰 출력 요청 보정\n" +
            $"최대 대기: {Ms(d.PeakBufferedMs)} · 지연 축소: {d.LatencyTrims}회 / {d.LatencyTrimmedFrames} 프레임\n" +
            $"용량 초과 폐기: {source.DroppedFrames - d.LatencyTrimmedFrames} 프레임\n\n" +
            $"PCM 수신: {d.ReceivedPackets}개 · 최근 패킷 오디오: {Ms(d.LastPacketAudioMs)}\n" +
            $"수신 간격 최근 / 최대: {Ms(d.LastReceiveIntervalMs)} / {Ms(d.MaxReceiveIntervalMs)}\n" +
            $"패킷 길이를 초과한 최대 수신 공백: {Ms(d.MaxReceiveExcessMs)}\n" +
            $"마지막 수신 후: {Ms(d.LastReceiveAgoMs)}\n" +
            $"수신 처리 최근 / 최대: {Ms(d.LastIngestMs)} / {Ms(d.MaxIngestMs)}\n\n";
        if (render is not null) text +=
            $"서버 출력 요청: {render.RenderCalls}회 · 최근 요청 오디오: {Ms(render.LastRequestedAudioMs)}\n" +
            $"출력 요청 간격 최근 / 최대: {Ms(render.LastRenderIntervalMs)} / {Ms(render.MaxRenderIntervalMs)}\n" +
            $"이전 요청 분량을 초과한 최대 출력 공백: {Ms(render.MaxRenderDelayMs)}\n" +
            $"출력 공백 20 ms 초과: {render.LateRenderCalls}회 · 마지막 요청 후: {Ms(render.LastRenderAgoMs)}\n" +
            $"믹싱 처리 최근 / 최대: {Ms(render.LastRenderWorkMs)} / {Ms(render.MaxRenderWorkMs)}\n\n";
        return text + "수신 공백 증가는 송신·캡처·네트워크 경로, 출력 공백 증가는 서버·출력 장치 경로를 확인하는 단서입니다. " +
            "버퍼 부족·폐기·최대값은 연결 이후 누적이며 출력 계측은 앱 실행 이후 누적입니다. " +
            "송신 타임스탬프가 없어 캡처와 순수 네트워크 지연을 분리할 수 없고 Bluetooth 실제 청취 지연도 포함하지 않습니다.";
    }
}
