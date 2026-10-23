using System.Windows;

namespace SoundManager.Windows;

public partial class App : Application
{
    private SingleInstance? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new SingleInstance(SingleInstance.CurrentSessionName);
        if (!instance.IsFirst)
        {
            // A delayed Windows login launch must not reveal an existing hidden window.
            if (e.Args.Contains("--startup")) instance.RequestStartup();
            else instance.RequestRestore();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        instance.Listen(() => Dispatcher.BeginInvoke(() =>
        {
            if (!Dispatcher.HasShutdownStarted) window.RestoreFromTray();
        }), () => Dispatcher.BeginInvoke(() =>
        {
            if (!Dispatcher.HasShutdownStarted) window.StartAutomatically();
        }));
        if (e.Args.Contains("--startup")) window.StartAutomatically();
        else window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.AllowSessionEnd();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
