namespace SoundManager.Core;

// Monotonic server-clock measurements; no file I/O or allocations on the render thread.
internal sealed class RenderTiming(TimeProvider clock)
{
    private readonly object gate = new();
    private long calls, lastRender, lateCalls;
    private double requestedMs, maxIntervalMs, maxDelayMs, workMs, maxWorkMs;
    private double? intervalMs;

    public long Begin(double audioMs)
    {
        long now = clock.GetTimestamp();
        lock (gate)
        {
            if (calls > 0)
            {
                intervalMs = clock.GetElapsedTime(lastRender, now).TotalMilliseconds;
                maxIntervalMs = Math.Max(maxIntervalMs, intervalMs.Value);
                double delay = Math.Max(0, intervalMs.Value - requestedMs);
                maxDelayMs = Math.Max(maxDelayMs, delay);
                if (delay > 20) lateCalls++;
            }
            calls++; lastRender = now; requestedMs = audioMs;
        }
        return now;
    }

    public void End(long begin)
    {
        double elapsed = clock.GetElapsedTime(begin).TotalMilliseconds;
        lock (gate) { workMs = elapsed; maxWorkMs = Math.Max(maxWorkMs, elapsed); }
    }

    public RenderDiagnostics Snapshot()
    {
        lock (gate) return new(calls, requestedMs, intervalMs, maxIntervalMs, maxDelayMs,
            calls == 0 ? null : clock.GetElapsedTime(lastRender).TotalMilliseconds, workMs, maxWorkMs, lateCalls);
    }
}
