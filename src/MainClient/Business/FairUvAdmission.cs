using System.Diagnostics;

namespace TrafficVista.Business;

/// <summary>FIFO UV permits and one global interval between UV starts. Canceled waiters never consume permits.</summary>
internal sealed class FairUvAdmission : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private readonly SemaphoreSlim _starts = new(1);
    private int _available;
    private long _lastStart;
    private bool _disposed;

    internal FairUvAdmission(int capacity) => _available = capacity;

    internal bool TryAcquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_available == 0 || _waiters.Count != 0) return false;
            _available--; return true;
        }
    }

    internal async Task WaitUntilStartDueAsync(int intervalMs, CancellationToken token)
    {
        while (true)
        {
            TimeSpan remaining;
            await _starts.WaitAsync(token).ConfigureAwait(false);
            try { remaining = _lastStart == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(_lastStart); }
            finally { _starts.Release(); }
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }

    internal async Task AcquireAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_available > 0) { _available--; return; }
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }
        using var registration = token.Register(() => waiter.TrySetCanceled(token));
        await waiter.Task.ConfigureAwait(false);
    }

    internal async Task StartAsync(int intervalMs, CancellationToken token)
    {
        await _starts.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_lastStart != 0)
            {
                var remaining = TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(_lastStart);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            _lastStart = Stopwatch.GetTimestamp();
        }
        finally { _starts.Release(); }
    }

    internal async Task RecordDispatchedAsync()
    {
        await _starts.WaitAsync().ConfigureAwait(false);
        try { _lastStart = Stopwatch.GetTimestamp(); }
        finally { _starts.Release(); }
    }

    internal void Release()
    {
        lock (_sync)
        {
            while (_waiters.TryDequeue(out var waiter))
                if (waiter.TrySetResult()) return;
            _available++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            while (_waiters.TryDequeue(out var waiter)) waiter.TrySetException(new ObjectDisposedException(nameof(FairUvAdmission)));
        }
        _starts.Dispose();
    }
}
