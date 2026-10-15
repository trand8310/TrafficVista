using System.Diagnostics;

namespace TrafficVista.Business;

public sealed record BusinessMetricsSnapshot(long SucceededUvs, long FailedUvs, long CanceledUvs,
    int ActiveBrowsers, int PeakBrowsers, double UvsPerMinute, double UvP50Ms, double UvP95Ms,
    long CacheHits, long CacheMisses, Dictionary<string, double> AverageStageMs)
{
    public double PeakSessionBrowserCommittedMiB { get; init; }
    public long PlannedUvs { get; init; }
    public long DispatchedUvs { get; init; }
    public long UndispatchedUvs { get; init; }
    public long FinishedUvs => SucceededUvs + FailedUvs + CanceledUvs;
    public double AttemptsPerMinute { get; init; }
    public double DispatchesPerMinute { get; init; }
    public Dictionary<string, (int Count, double OldestSeconds)> ActiveStages { get; init; } = new();
    public Dictionary<string, long> StageFailures { get; init; } = new();
}

/// <summary>Bounded rolling latency samples; application resets counters before each scheduler run.</summary>
public sealed class BusinessMetrics
{
    private readonly object _sync = new();
    private readonly Func<long> _dispatchSecond;
    private readonly SortedDictionary<long, long> _dispatchWindow = new();
    public BusinessMetrics() : this(() => Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }
    internal BusinessMetrics(Func<long> dispatchSecond) => _dispatchSecond = dispatchSecond;
    private readonly Queue<double> _latencies = new();
    private readonly Dictionary<string, (double Sum, long Count)> _stages = new();
    private long _started = Stopwatch.GetTimestamp();
    private long _succeeded, _failed, _canceled, _hits, _misses;
    private int _browsers, _peak;
    private ulong _peakBrowserMemory;
    private long _planned, _dispatched, _undispatched;
    private readonly Dictionary<Guid, (string Name, long Started)> _activeStages = new();
    private readonly Dictionary<string, long> _failures = new();
    public void PlanUvs(int count) { lock (_sync) _planned += count; }
    public void DispatchUv()
    {
        lock (_sync)
        {
            _dispatched++;
            var second = _dispatchSecond();
            PruneDispatchWindow(second);
            _dispatchWindow[second] = _dispatchWindow.GetValueOrDefault(second) + 1;
        }
    }
    private void PruneDispatchWindow(long second)
    {
        while (_dispatchWindow.Count > 0 && _dispatchWindow.First().Key <= second - 60)
            _dispatchWindow.Remove(_dispatchWindow.First().Key);
    }
    public void Undispatched(int count) { lock (_sync) _undispatched += count; }
    public void Failure(string stage) { lock (_sync) _failures[stage] = _failures.GetValueOrDefault(stage) + 1; }
    public IDisposable Track(string stage)
    {
        var id = Guid.NewGuid();
        lock (_sync) _activeStages[id] = (stage, Stopwatch.GetTimestamp());
        return new StageScope(this, id);
    }
    private sealed class StageScope(BusinessMetrics owner, Guid id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync)
                if (owner._activeStages.Remove(id, out var stage))
                    owner.Stage(stage.Name, Stopwatch.GetElapsedTime(stage.Started));
        }
    }
    public void BrowserMemory(ulong bytes) { lock (_sync) _peakBrowserMemory = Math.Max(_peakBrowserMemory, bytes); }
    internal double PeakSessionBrowserCommittedMiB { get { lock (_sync) return _peakBrowserMemory / 1048576.0; } }
    public void Reset()
    {
        lock (_sync)
        {
            if (_browsers != 0) throw new InvalidOperationException("仍有业务浏览器在运行，不能重置指标。");
            _succeeded = _failed = _canceled = _hits = _misses = 0;
            _peak = 0; _peakBrowserMemory = 0; _latencies.Clear(); _stages.Clear();
            _planned = _dispatched = _undispatched = 0; _activeStages.Clear(); _failures.Clear();
            _dispatchWindow.Clear();
            _started = Stopwatch.GetTimestamp();
        }
    }

    public void Stage(string name, TimeSpan elapsed)
    {
        lock (_sync)
        {
            var previous = _stages.GetValueOrDefault(name);
            _stages[name] = (previous.Sum + elapsed.TotalMilliseconds, previous.Count + 1);
        }
    }
    public void BrowserOpened() { lock (_sync) { _browsers++; _peak = Math.Max(_peak, _browsers); } }
    public void BrowserClosed() { lock (_sync) _browsers--; }
    public void CacheLookup(bool hit) { lock (_sync) { if (hit) _hits++; else _misses++; } }
    public void UvFinished(bool success, bool canceled, TimeSpan elapsed)
    {
        lock (_sync)
        {
            if (canceled) _canceled++; else if (success) _succeeded++; else _failed++;
            _latencies.Enqueue(elapsed.TotalMilliseconds);
            if (_latencies.Count > 1024) _latencies.Dequeue();
        }
    }
    public BusinessMetricsSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var values = _latencies.Order().ToArray();
                PruneDispatchWindow(_dispatchSecond());
                double Percentile(double p) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(p * values.Length) - 1];
                return new(_succeeded, _failed, _canceled, _browsers, _peak,
                    _succeeded / Math.Max(Stopwatch.GetElapsedTime(_started).TotalMinutes, 1.0 / 60000),
                    Percentile(.5), Percentile(.95), _hits, _misses,
                    _stages.ToDictionary(pair => pair.Key, pair => pair.Value.Sum / pair.Value.Count))
                    {
                        PeakSessionBrowserCommittedMiB = _peakBrowserMemory / 1048576.0,
                        PlannedUvs = _planned, DispatchedUvs = _dispatched, UndispatchedUvs = _undispatched,
                        DispatchesPerMinute = _dispatchWindow.Values.Sum(),
                        AttemptsPerMinute = (_succeeded + _failed + _canceled)
                            / Math.Max(Stopwatch.GetElapsedTime(_started).TotalMinutes, 1.0 / 60000),
                        ActiveStages = _activeStages.Values.GroupBy(stage => stage.Name).ToDictionary(group => group.Key,
                            group => (group.Count(), group.Max(stage => Stopwatch.GetElapsedTime(stage.Started).TotalSeconds))),
                        StageFailures = new(_failures)
                    };
            }
        }
    }
}
