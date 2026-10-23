using System.Diagnostics;
using System.Security.Principal;

namespace SoundManager.Windows;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle restore;
    private readonly EventWaitHandle startup;
    private RegisteredWaitHandle? listener;
    private RegisteredWaitHandle? startupListener;
    public bool IsFirst { get; }

    public static string CurrentSessionName =>
        $"Local\\SoundManager.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";

    public SingleInstance(string name)
    {
        // Create the signal before publishing ownership, so concurrent launches cannot miss it.
        restore = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Restore");
        startup = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Startup");
        mutex = new Mutex(true, name, out bool created);
        IsFirst = created;
    }

    public void Listen(Action action, Action? onStartup = null)
    {
        listener = ThreadPool.RegisterWaitForSingleObject(
            restore, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);
        if (onStartup is not null) startupListener = ThreadPool.RegisterWaitForSingleObject(
            startup, (_, _) => onStartup(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void RequestRestore() => restore.Set();
    public void RequestStartup() => startup.Set();

    public void Dispose()
    {
        listener?.Unregister(null);
        startupListener?.Unregister(null);
        restore.Dispose();
        startup.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
