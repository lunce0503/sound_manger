using System.Diagnostics;
using System.Security.Principal;

namespace SoundManager.Windows;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle restore;
    private RegisteredWaitHandle? listener;
    public bool IsFirst { get; }

    public static string CurrentSessionName =>
        $"Local\\SoundManager.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";

    public SingleInstance(string name)
    {
        // Create the signal before publishing ownership, so concurrent launches cannot miss it.
        restore = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Restore");
        mutex = new Mutex(true, name, out bool created);
        IsFirst = created;
    }

    public void Listen(Action action) => listener = ThreadPool.RegisterWaitForSingleObject(
        restore, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void RequestRestore() => restore.Set();

    public void Dispose()
    {
        listener?.Unregister(null);
        restore.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
