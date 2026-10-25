using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Windows;

public partial class SourceListView : UserControl
{
    private readonly ObservableCollection<Row> rows = [];
    private readonly Func<Guid, SourceSettingsPatch, SourceUpdateResult> update;
    private bool refreshing;
    internal sealed class Row(Guid id) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; private set; } = "";
        public SourceSettings Settings { get; private set; } = new();
        public bool Muted => Settings.Muted;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(SourceState state)
        {
            Name = state.Name; Settings = state.Settings;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(Muted)));
        }
    }

    public SourceListView(Func<Guid, SourceSettingsPatch, SourceUpdateResult> update)
    {
        InitializeComponent(); this.update = update; List.ItemsSource = rows;
    }

    public void Refresh(SourceState[] sources)
    {
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

    private void MuteChanged(object sender, RoutedEventArgs e)
    {
        if (!refreshing && sender is CheckBox { DataContext: Row row } toggle)
            Submit(row, new(Muted: toggle.IsChecked == true));
    }

    private bool Submit(Row row, SourceSettingsPatch changes)
    {
        var result = update(row.Id, changes);
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

    private static void Buttons(Window window, StackPanel body, Func<bool> apply)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "취소", Padding = new(18, 8, 18, 8), Margin = new(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        var save = new Button { Content = "적용", Padding = new(18, 8, 18, 8), IsDefault = true };
        save.Click += (_, _) => { if (apply()) window.Close(); };
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
        reset.Click += (_, _) => { if (Submit(row, new(ResetName: true))) window.Close(); };
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
}
