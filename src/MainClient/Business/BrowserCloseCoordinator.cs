using System.Diagnostics;

namespace TrafficVista.Business;

public sealed record BrowserCloseSnapshot(int InFlight, int Deferred, int Capacity, double OldestDeferredSeconds);

/// <summary>Owns outstanding driver-close requests after the owned Chrome processes are gone.</summary>
internal sealed class BrowserCloseCoordinator(int capacity)
{
    internal static BrowserCloseCoordinator Shared { get; } = new(64);
    private readonly SemaphoreSlim _slots = new(capacity, capacity);
    private readonly object _sync = new();
    private readonly Dictionary<Task, long> _deferred = new();
    private int _inFlight;
    private TaskCompletionSource _idle = Completed();
    private static TaskCompletionSource Completed()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }

    internal sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal async Task<Lease> ReserveAsync(Action onBackpressure, TimeSpan timeout)
    {
        if (!_slots.Wait(0))
        {
            onBackpressure();
            if (!await _slots.WaitAsync(timeout).ConfigureAwait(false))
                throw new TimeoutException("等待Playwright关闭请求空位超时，后台收尾未完成。");
        }
        lock (_sync)
            if (_inFlight++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new Lease(() =>
        {
            lock (_sync) if (--_inFlight == 0) _idle.TrySetResult();
            _slots.Release();
        });
    }

    internal void Defer(Task close, Lease lease, Action<Exception?> completed)
    {
        lock (_sync) _deferred.Add(close, Stopwatch.GetTimestamp());
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            Exception? error = null;
            try { await close.ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
            finally
            {
                lock (_sync) _deferred.Remove(close);
                lease.Dispose();
            }
            try { completed(error); } catch { /* Diagnostics do not retain a close slot. */ }
        }
    }

    internal async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken token = default)
    {
        Task idle;
        lock (_sync) idle = _idle.Task;
        try { await idle.WaitAsync(timeout, token).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    internal BrowserCloseSnapshot Snapshot
    {
        get
        {
            lock (_sync) return new(_inFlight, _deferred.Count, capacity, _deferred.Count == 0 ? 0
                : _deferred.Values.Max(start => Stopwatch.GetElapsedTime(start).TotalSeconds));
        }
    }
}
