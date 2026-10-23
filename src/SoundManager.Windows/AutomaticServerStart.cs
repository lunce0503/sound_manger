namespace SoundManager.Windows;

internal static class AutomaticServerStart
{
    // A login can precede Bluetooth, the audio service, or network readiness.
    public static async Task RunAsync(Func<CancellationToken, Task<bool>> tryStart,
        Action waiting, CancellationToken cancellation, TimeSpan? retryInterval = null)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (await tryStart(cancellation)) return;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { /* Keep the saved pairing and output device on transient failure. */ }
            waiting();
            await Task.Delay(retryInterval ?? TimeSpan.FromSeconds(5), cancellation);
        }
    }
}
