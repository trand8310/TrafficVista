using TrafficVista.Business;

namespace TrafficVista.DesktopIntegration;

internal sealed record DesktopStatusSnapshot(int LogicalCores, SystemPressureSample Resources,
    int? BrowserProcesses, string? ProcessError);

/// <summary>UI reads a snapshot; native resource and owned-job queries stay on a background worker.</summary>
internal sealed class DesktopStatusMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private DesktopStatusSnapshot _snapshot = new(0, new(false, 0, 0, 0, 0), null, null);
    private int _disposed;
    internal DesktopStatusSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal DesktopStatusMonitor()
    {
        _worker = Task.Run(async () =>
        {
            try
            {
                var cores = SystemPressureProbe.LogicalProcessorCount;
                while (!_stop.IsCancellationRequested)
                {
                    var resources = SystemPressureProbe.Shared.Latest;
                    int? processes = null; string? error = null;
                    try { processes = ChromeProcessJob.OwnedProcessCount; }
                    catch (Exception ex) { error = ex.Message; }
                    Volatile.Write(ref _snapshot, new(cores, resources, processes, error));
                    await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Volatile.Write(ref _snapshot, new(0, new(false, 0, 0, 0, 0)
                    { UnavailableReason = ex.Message }, null, ex.Message));
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = _worker.ContinueWith(task => { _ = task.Exception; _stop.Dispose(); }, TaskScheduler.Default);
    }
}
