using System.Diagnostics;

namespace TrafficVista.Business;

public sealed record BusinessMetricsSnapshot(long SucceededUvs, long FailedUvs, long CanceledUvs,
    int ActiveBrowsers, int PeakBrowsers, double UvsPerMinute, double UvP50Ms, double UvP95Ms,
    long CacheHits, long CacheMisses, Dictionary<string, double> AverageStageMs)
{
    public double PeakSessionBrowserCommittedMiB { get; init; }
}

/// <summary>Bounded rolling latency samples; application resets counters before each scheduler run.</summary>
public sealed class BusinessMetrics
{
    private readonly object _sync = new();
    private readonly Queue<double> _latencies = new();
    private readonly Dictionary<string, (double Sum, long Count)> _stages = new();
    private long _started = Stopwatch.GetTimestamp();
    private long _succeeded, _failed, _canceled, _hits, _misses;
    private int _browsers, _peak;
    private ulong _peakBrowserMemory;
    public void BrowserMemory(ulong bytes) { lock (_sync) _peakBrowserMemory = Math.Max(_peakBrowserMemory, bytes); }
    internal double PeakSessionBrowserCommittedMiB { get { lock (_sync) return _peakBrowserMemory / 1048576.0; } }
    public void Reset()
    {
        lock (_sync)
        {
            if (_browsers != 0) throw new InvalidOperationException("仍有业务浏览器在运行，不能重置指标。");
            _succeeded = _failed = _canceled = _hits = _misses = 0;
            _peak = 0; _peakBrowserMemory = 0; _latencies.Clear(); _stages.Clear();
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
                double Percentile(double p) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(p * values.Length) - 1];
                return new(_succeeded, _failed, _canceled, _browsers, _peak,
                    _succeeded / Math.Max(Stopwatch.GetElapsedTime(_started).TotalMinutes, 1.0 / 60000),
                    Percentile(.5), Percentile(.95), _hits, _misses,
                    _stages.ToDictionary(pair => pair.Key, pair => pair.Value.Sum / pair.Value.Count))
                    { PeakSessionBrowserCommittedMiB = _peakBrowserMemory / 1048576.0 };
            }
        }
    }
}
