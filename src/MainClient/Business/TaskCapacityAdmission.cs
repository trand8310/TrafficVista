using TrafficVista.Configuration;
namespace TrafficVista.Business;

public sealed record TaskAdmissionSnapshot(int ReservedSlots, int WaitingTasks, int PendingUvs,
    int DispatchingTasks, int MemoryCapacity, double CpuPercent, double AvailableMemoryMiB, string Reason)
{
    public double MemoryPerUvMiB { get; init; }
    public double CommitAvailableMiB { get; init; }
    public int HardLimit { get; init; }
}
internal sealed record UvCapacityPlan(int Uvs, int PageViews, double MinimumDwellSeconds,
    double MaximumDwellSeconds, double PageDelaySeconds);
/// <summary>全局UV并发池；任务独立投递间隔，资源压力只暂停获取新任务。</summary>
internal sealed class TaskCapacityAdmission : IDisposable
{
    private readonly object _sync = new();
    private readonly int _hardLimit;
    private readonly SemaphoreSlim _uvSlots;
    private readonly Func<SystemPressureSample> _probe;
    private readonly BusinessMetrics _metrics;
    private readonly Action<bool, string>? _report;
    private readonly List<Reservation> _reservations = new();
    private readonly Queue<double> _overheadPerView = new(), _ipLifetimes = new();
    private int _reserved, _waiting;
    private bool _pressureBlocked, _disposed;
    private TaskAdmissionSnapshot _snapshot = new(0, 0, 0, 0, 0, 0, 0, "等待任务");
    internal TaskCapacityAdmission(int hardLimit, BusinessMetrics metrics, Func<SystemPressureSample>? probe = null,
        Action<bool, string>? report = null)
    {
        _hardLimit = hardLimit; _uvSlots = new(hardLimit, hardLimit); _metrics = metrics;
        _probe = probe ?? (() => SystemPressureProbe.Shared.Latest); _report = report;
    }
    internal TaskAdmissionSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    internal bool HasReservations { get { lock (_sync) return _reservations.Count != 0 || _waiting != 0; } }
    internal async Task<Reservation> ReserveAsync(UvCapacityPlan plan, TrafficVistaOptions options, CancellationToken token)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _waiting++; UpdateSnapshot(); }
        var acquired = false;
        try
        {
            // 只占首UV名额再领取IP，不按整项任务预留名额，不暂停已经取回的任务。
            await _uvSlots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var duration = EstimateUvMs(plan);
                var predicted = EstimateCompletionMs(plan.Uvs, Math.Min(plan.Uvs, _hardLimit), duration, options.UvExecutionInterval);
                var ttl = options.IsProxyMode ? KnownLifetime(options.IpTtl) : null;
                var result = new Reservation(this, plan.Uvs, duration, predicted, ttl is { } seconds && predicted > seconds * 1000);
                _reserved++; _reservations.Add(result); UpdateSnapshot(); acquired = false;
                return result;
            }
        }
        finally { if (acquired) _uvSlots.Release(); lock (_sync) { _waiting--; UpdateSnapshot(); } }
    }
    internal async Task WaitForFetchAsync(CancellationToken token)
    {
        var wasPaused = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool paused; string reason;
            lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); RefreshPressure(); paused = _pressureBlocked; reason = _snapshot.Reason; }
            if (paused && !wasPaused) _report?.Invoke(true, reason);
            if (!paused) { if (wasPaused) _report?.Invoke(false, "资源恢复，继续获取新任务"); return; }
            wasPaused = true;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }
    private void RefreshPressure()
    {
        const double mib = 1048576;
        var sample = _probe();
        var reserve = Math.Max(512 * mib, sample.TotalPhysicalBytes * .1);
        var available = Math.Min((double)sample.AvailablePhysicalBytes, sample.AvailableCommitBytes);
        if (!sample.Available) _pressureBlocked = true;
        else if (sample.CpuPercent >= 85 || available < reserve) _pressureBlocked = true;
        else if (sample.CpuPercent <= 70 && available >= reserve * 1.25) _pressureBlocked = false;
        _snapshot = _snapshot with
        {
            CpuPercent = sample.CpuPercent,
            AvailableMemoryMiB = sample.AvailablePhysicalBytes / mib,
            CommitAvailableMiB = sample.AvailableCommitBytes / mib,
            MemoryPerUvMiB = _metrics.PeakSessionBrowserCommittedMiB * 1.25,
            Reason = !sample.Available ? "资源采样不可用，暂停取任务" :
                _pressureBlocked ? "CPU或内存承压，暂停取任务（已取任务继续）" : "按设定并发执行"
        };
        UpdateSnapshot();
    }
    private void UpdateSnapshot() => _snapshot = _snapshot with
    {
        ReservedSlots = _reserved,
        WaitingTasks = _waiting,
        MemoryCapacity = _hardLimit - _reserved,
        HardLimit = _hardLimit,
        PendingUvs = _reservations.Sum(r => r.Undispatched),
        DispatchingTasks = _reservations.Count(r => r.Undispatched > 0)
    };
    private double EstimateUvMs(UvCapacityPlan plan)
    {
        var values = _overheadPerView.Order().ToArray();
        var overhead = values.Length == 0 ? 5000 : values[(int)Math.Ceiling(values.Length * .95) - 1];
        return Math.Max(1000, overhead * 1.2 * plan.PageViews + plan.MaximumDwellSeconds * 1000 * plan.PageViews + plan.PageDelaySeconds * 1000);
    }
    internal static double EstimateCompletionMs(int count, int slots, double durationMs, int intervalMs)
        => (count - 1L) / slots * Math.Max(durationMs, (double)slots * intervalMs) + (count - 1L) % slots * intervalMs + durationMs;
    private double? KnownLifetime(int configured)
    {
        var learned = _ipLifetimes.Count == 0 ? (double?)null : _ipLifetimes.Min();
        return configured > 0 ? Math.Min(configured, learned ?? configured) : learned;
    }
    internal void ObserveProxyLifetime(BusinessProxy? proxy, int ipTtl)
    {
        if (proxy?.Deadline(ipTtl) is not { } deadline) return;
        var seconds = (deadline - DateTimeOffset.UtcNow).TotalSeconds;
        if (seconds <= 0) return;
        lock (_sync) { _ipLifetimes.Enqueue(seconds); if (_ipLifetimes.Count > 32) _ipLifetimes.Dequeue(); }
    }
    internal void ObserveUv(UvCapacityPlan plan, TimeSpan elapsed, bool success)
    {
        if (!success) return;
        var overhead = Math.Max(500, (elapsed.TotalMilliseconds - plan.MinimumDwellSeconds * 1000 * plan.PageViews - plan.PageDelaySeconds * 1000) / plan.PageViews);
        lock (_sync) { _overheadPerView.Enqueue(overhead); if (_overheadPerView.Count > 128) _overheadPerView.Dequeue(); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_reserved != 0 || _waiting != 0 || _reservations.Count != 0) throw new InvalidOperationException("UV或任务尚未退出。");
            _disposed = true; _uvSlots.Dispose();
        }
    }
    internal sealed class Reservation : IDisposable
    {
        private readonly TaskCapacityAdmission _owner;
        private readonly FairUvAdmission _cadence = new(1);
        private int _remaining, _disposed, _firstAvailable = 1, _held = 1;
        internal int Slots { get; private set; } = 1;
        internal int InitialSlots => 1;
        internal int Undispatched => Volatile.Read(ref _remaining);
        internal double EstimatedUvMs { get; }
        internal double EstimatedCompletionMs { get; }
        internal bool DeadlineAtRisk { get; }
        internal Reservation(TaskCapacityAdmission owner, int uvs, double duration, double completion, bool risk)
        { _owner = owner; _remaining = uvs; EstimatedUvMs = duration; EstimatedCompletionMs = completion; DeadlineAtRisk = risk; }
        internal async Task AcquireUvAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _firstAvailable, 0) == 1) return;
            await _owner._uvSlots.WaitAsync(token).ConfigureAwait(false);
            lock (_owner._sync) { _owner._reserved++; _held++; Slots = Math.Max(Slots, _held); _owner.UpdateSnapshot(); }
        }
        internal Task WaitUntilStartDueAsync(int intervalMs, CancellationToken token) => _cadence.WaitUntilStartDueAsync(intervalMs, token);
        internal Task StartUvAsync(int intervalMs, CancellationToken token) => _cadence.StartAsync(intervalMs, token);
        internal Task RecordDispatchedAsync() => _cadence.RecordDispatchedAsync();
        internal void Dispatched() { lock (_owner._sync) { Interlocked.Decrement(ref _remaining); _owner.UpdateSnapshot(); } }
        internal void ReleaseUv()
        {
            lock (_owner._sync)
            {
                if (_held <= 0) throw new InvalidOperationException("UV名额重复释放。");
                _held--; _owner._reserved--; _owner.UpdateSnapshot(); _owner._uvSlots.Release();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Exchange(ref _firstAvailable, 0) == 1) ReleaseUv();
            lock (_owner._sync)
            {
                if (_held != 0) throw new InvalidOperationException("任务仍有UV未清理。");
                _owner._reservations.Remove(this); _owner.UpdateSnapshot();
            }
            _cadence.Dispose();
        }
    }
}
