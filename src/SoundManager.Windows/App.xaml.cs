using System.Windows;
using System.IO;
using SoundManager.Updates;

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
        bool updating=e.Args.Length==2&&e.Args[0] is "--updated" or "--update-fallback";
        var resume=updating?WindowsUpdates.Resume(e.Args[1]):null;
        string version=typeof(App).Assembly.GetName().Version!.ToString(3);
        bool rollback=updating&&e.Args[0]=="--update-fallback";
        if(resume is not null&&(rollback||resume.Version==version))
        {
            if(!resume.Hidden)window.Show();
            _=window.RestoreUpdateAsync(resume,rollback);
            InstalledVersion.Record(WindowsUpdates.Root,AppContext.BaseDirectory,version,rollback);
            Directory.CreateDirectory(Path.Combine(WindowsUpdates.Root,"updates"));
            File.WriteAllText(LauncherOptions.Marker(WindowsUpdates.Root,resume.Token,"ready"),version);
        }
        else
        {
            InstalledVersion.Record(WindowsUpdates.Root,AppContext.BaseDirectory,version);
            if (e.Args.Contains("--startup")) window.StartAutomatically();
            else window.Show();
        }
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
