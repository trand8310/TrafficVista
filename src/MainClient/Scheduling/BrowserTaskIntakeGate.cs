using System.Diagnostics;
using Serilog;

namespace TrafficVista.Scheduling;

public sealed record TaskIntakeSnapshot(bool Paused, int WaitingWorkers, int WaitingBrowserStarts,
    int PauseThreshold, int ResumeThreshold, double PausedSeconds)
{
    public long PendingUvs { get; init; }
    public long PreparingUvs { get; init; }
    public long Pressure => WaitingBrowserStarts + PendingUvs + PreparingUvs;
    public bool Enabled { get; init; } = true;
}

/// <summary>Hysteresis at task dequeue only. Already claimed tasks retain their UV dispatch cadence.</summary>
internal sealed class BrowserTaskIntakeGate : IDisposable
{
    private readonly object _sync = new();
    private readonly BrowserLaunchLimiter _launches;
    private readonly ILogger _logger;
    private int _pause, _resume, _browserQueue, _workers;
    private bool _paused;
    private bool _enabled = true;
    internal void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            Evaluate();
        }
    }
    private long _pausedAt;
    private TaskCompletionSource _opened = NewSignal();
    private readonly Dictionary<string, Demand> _demands = new();
    private long _pendingUvs, _preparingUvs;
    private sealed class Demand(int pending) { internal int Pending = pending; internal int Preparing; }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BrowserTaskIntakeGate(BrowserLaunchLimiter launches, int pause, int resume, ILogger logger)
    {
        if (pause < 1 || resume < 0 || resume >= pause) throw new ArgumentOutOfRangeException(nameof(resume));
        _launches = launches; _pause = pause; _resume = resume; _logger = logger;
        _launches.WaitingStartsChanged += Update;
        Update(launches.WaitingStarts);
    }

    private void Update(int count)
    {
        lock (_sync)
        {
            _browserQueue = count;
            Evaluate();
        }
    }

    // Called under the queue's dequeue lock, before another worker can inspect the next item.
    internal IDisposable? TryReserve(string deliveryId, int uvCount)
    {
        lock (_sync)
        {
            if (_paused) return null;
            var demand = new Demand(uvCount);
            _demands.Add(deliveryId, demand);
            _pendingUvs += uvCount;
            Evaluate();
            return new Lease(() =>
            {
                lock (_sync)
                {
                    _pendingUvs -= demand.Pending;
                    _preparingUvs -= demand.Preparing;
                    demand.Pending = demand.Preparing = 0;
                    _demands.Remove(deliveryId);
                    Evaluate();
                }
            });
        }
    }

    // A submitted UV retains pressure during device preparation, until it joins the startup queue.
    internal IDisposable? BeginUv(string deliveryId)
    {
        lock (_sync)
        {
            if (!_demands.TryGetValue(deliveryId, out var demand) || demand.Pending == 0) return null;
            demand.Pending--; _pendingUvs--; demand.Preparing++; _preparingUvs++;
            return new Lease(() =>
            {
                lock (_sync)
                {
                    if (demand.Preparing > 0) { demand.Preparing--; _preparingUvs--; }
                    Evaluate();
                }
            });
        }
    }

    private void Evaluate()
    {
        if (!_enabled)
        {
            _paused = false;
            _opened.TrySetResult();
            return;
        }
        var pressure = _browserQueue + _pendingUvs + _preparingUvs;
        if (!_paused && pressure >= _pause)
        {
            _paused = true; _pausedAt = Stopwatch.GetTimestamp(); _opened = NewSignal();
            Log("暂停领取新任务", pressure);
        }
        else if (_paused && pressure <= _resume)
        {
            _paused = false; _opened.TrySetResult();
            Log("恢复领取新任务", pressure);
        }
    }

    private void Log(string state, long pressure)
    {
        // Diagnostics must not interrupt a browser's semaphore ownership transition.
        try
        {
            _logger.Information("浏览器启动需求调节：{IntakeState}，Pressure={Pressure}，WaitingBrowserStarts={WaitingBrowserStarts}，PendingUvs={PendingUvs}，PreparingUvs={PreparingUvs}，PauseThreshold={PauseThreshold}，ResumeThreshold={ResumeThreshold}",
                state, pressure, _browserQueue, _pendingUvs, _preparingUvs, _pause, _resume);
        }
        catch { }
    }

    internal async ValueTask WaitAsync(CancellationToken token)
    {
        while (true)
        {
            Task opened;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!_paused) return;
                _workers++; opened = _opened.Task;
            }
            try { await opened.WaitAsync(token).ConfigureAwait(false); }
            finally { lock (_sync) _workers--; }
        }
    }

    internal TaskIntakeSnapshot Snapshot
    {
        get
        {
            lock (_sync) return new(_paused, _workers, _browserQueue, _pause, _resume,
                _paused ? Stopwatch.GetElapsedTime(_pausedAt).TotalSeconds : 0)
            { PendingUvs = _pendingUvs, PreparingUvs = _preparingUvs, Enabled = _enabled };
        }
    }

    public void Dispose() => _launches.WaitingStartsChanged -= Update;
}
