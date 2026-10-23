using System.ComponentModel;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SoundManager.Windows;

// Closing the listening window never owns the lifetime of the audio/server tasks.
internal sealed class BackgroundWindowLifetime : IDisposable
{
    private readonly Window window;
    private readonly Func<Task> stop;
    private readonly Action exit;
    private readonly Forms.NotifyIcon? tray;
    private readonly Forms.ContextMenuStrip? menu;
    private bool allowClose, notified, exiting, disposed;
    private Task? exitTask;

    public BackgroundWindowLifetime(Window window, Func<Task> stop, Action exit, bool createTray = true)
    {
        this.window = window;
        this.stop = stop;
        this.exit = exit;
        window.Closing += OnClosing;
        if (!createTray) return;
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Sound Manager 열기", null, (_, _) => Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("완전 종료 (서버 중지)", null, async (_, _) => await ExitAsync());
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Sound Manager · 백그라운드 대기",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => Restore();
    }

    public void SetStatus(string text)
    {
        string status = "Sound Manager · " + text;
        if (tray is not null && !exiting && tray.Text != status) tray.Text = status;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (exiting) return;
        window.Hide();
        if (notified || tray is null) return;
        notified = true;
        tray.ShowBalloonTip(4000, "Sound Manager가 계속 실행 중입니다",
            "서버와 음원 전송은 유지됩니다. 트레이 아이콘에서 열기 또는 완전 종료를 선택하세요.", Forms.ToolTipIcon.Info);
    }

    public void Restore()
    {
        if (exiting) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public Task ExitAsync() => exitTask ??= StopAndExitAsync();

    private async Task StopAndExitAsync()
    {
        exiting = true;
        window.IsEnabled = false;
        try { await stop(); }
        catch (Exception)
        {
            MessageBox.Show("종료 중 일부 연결을 정리하지 못했습니다. 앱을 종료합니다.",
                "Sound Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            allowClose = true;
            Dispose();
            window.Close();
            exit();
        }
    }

    // Windows logoff/shutdown must not be cancelled by close-to-tray behavior.
    public void AllowSessionEnd() { allowClose = true; Dispose(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        menu?.Dispose();
    }
}
