using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoundManager.Core;
using SoundManager.Server;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class SourceListTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [Fact]
    public Task CompactRowsHaveOnlyNameMuteAndMenuAndDoNotSendChangesDuringRefresh() => BackgroundLifetimeTests.OnDesktop(() =>
    {
        var mixer = new Mixer();
        var id = mixer.AddSource("내 PC", new(48000, 2, "float32"), new(Muted: true));
        int changes = 0;
        SourceListView? view = null;
        view = new SourceListView((source, patch) =>
        {
            changes++; mixer.ConfigureSource(source, patch.Apply(mixer.GetSourceSettings(source)!));
            view!.Refresh(mixer.Snapshot().Sources); return SourceUpdateResult.Updated;
        });
        var window = new Window { Content = view, Width = 680, Height = 240, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            view.Refresh(mixer.Snapshot().Sources); window.Show(); window.UpdateLayout();
            Assert.Equal(0, changes); Assert.Empty(Descendants<Slider>(view));
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text == "내 PC");
            var mute = Assert.Single(Descendants<CheckBox>(view)); Assert.True(mute.IsChecked);
            var menu = Assert.Single(Descendants<Button>(view)); Assert.Equal("⋮", menu.Content);
            mute.IsChecked = false;
            Assert.Equal(1, changes); Assert.False(mixer.GetSourceSettings(id)!.Muted);
            view.Refresh(mixer.Snapshot().Sources); Assert.Equal(1, changes);
            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] {"음량 조절", "이름 변경", "360° 방향 설정"}, menu.ContextMenu.Items.Cast<MenuItem>().Select(item => item.Header));
            menu.ContextMenu.IsOpen = false;
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ServerVolumeAndRenameDialogsSubmitOnlyTheirChangedFields() => BackgroundLifetimeTests.OnDesktop(() =>
    {
        var mixer = new Mixer(); mixer.AddSource("원래 이름", new(48000, 2, "float32"), new(0.3f, 275, true, true));
        var submitted = new List<SourceSettingsPatch>(); SourceListView? view = null;
        view = new SourceListView((source, patch) =>
        {
            submitted.Add(patch); mixer.ConfigureSource(source, patch.Apply(mixer.GetSourceSettings(source)!));
            view!.Refresh(mixer.Snapshot().Sources); return SourceUpdateResult.Updated;
        });
        var window = new Window { Content = view, Width = 680, Height = 240, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            view.Refresh(mixer.Snapshot().Sources); window.Show(); window.UpdateLayout();
            var more = Assert.Single(Descendants<Button>(view));
            void Choose(int index, Action<Window> inspect)
            {
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var item = (MenuItem)more.ContextMenu.Items[index]; more.ContextMenu.IsOpen = false;
                Exception? failure = null;
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var dialog = window.OwnedWindows.Cast<Window>().Single();
                    try { inspect(dialog); }
                    catch (Exception error) { failure = error; }
                    finally { dialog.Close(); }
                });
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (failure is not null) throw failure;
            }
            Choose(0, dialog =>
            {
                var slider = Assert.Single(Descendants<Slider>(dialog)); Assert.InRange(slider.Value, 29.999, 30.001); slider.Value = 45;
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "적용")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(new SourceSettingsPatch(Volume: 0.45f), submitted.Single());
            Choose(1, dialog =>
            {
                Assert.Single(Descendants<TextBox>(dialog)).Text = "작업용 PC";
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "적용")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(new SourceSettingsPatch(DisplayName: "작업용 PC"), submitted.Last());
            Assert.Equal("작업용 PC", mixer.Snapshot().Sources.Single().Name);
            Assert.Equal(275, mixer.Snapshot().Sources.Single().Settings.Azimuth);
            Assert.True(mixer.Snapshot().Sources.Single().Settings.Muted);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
