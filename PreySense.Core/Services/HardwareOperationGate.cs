namespace PreySense.Core.Services;

// Sensor readers must not retain NVIDIA handles while the display adapter switches.
// A single gate also prevents the window and overlay from issuing overlapping batches.
public static class HardwareOperationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable? TryEnterTelemetry(int timeoutMs = 0) =>
        Gate.Wait(timeoutMs) ? new Lease() : null;

    public static async Task<IDisposable> EnterGpuBaselineCaptureAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        return new Lease();
    }

    /// <summary>
    /// iGPU Only fork: for background work that talks to NVIDIA's driver or services (Mode Extras, RTX guard).
    /// It must never overlap a display switch or an RTX restart, which freezes the system.
    /// </summary>
    public static IDisposable? TryEnterNvidiaWork(TimeSpan timeout) =>
        Gate.Wait(timeout) ? new Lease() : null;

    public static async Task<IDisposable> EnterDisplaySwitchAsync()
    {
        // iGPU Only fork: background NVIDIA work (Mode Extras) can hold the gate for a few seconds; wait for it
        // rather than failing the switch.
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Another GPU operation is still running. GPU switching was postponed.");
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release();
        }
    }
}
