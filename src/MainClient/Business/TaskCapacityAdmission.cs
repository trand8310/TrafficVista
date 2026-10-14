using TrafficVista.Configuration;

namespace TrafficVista.Business;

public sealed record TaskAdmissionSnapshot(int ReservedSlots, int WaitingTasks, int PendingUvs,
    int DispatchingTasks, int MemoryCapacity, double CpuPercent, double AvailableMemoryMiB, string Reason);

internal sealed record UvCapacityPlan(int Uvs, int PageViews, double MinimumDwellSeconds,
    double MaximumDwellSeconds, double PageDelaySeconds);

/// <summary>Reserve task-owned UV lanes before IP acquisition. Existing reservations never shrink under pressure.</summary>
internal sealed class TaskCapacityAdmission : IDisposable
{
    private const double MiB = 1048576;
    private readonly object _sync = new();
    private readonly int _hardLimit;
    private readonly Func<SystemPressureSample> _probe;
    private readonly BusinessMetrics _metrics;
    private readonly List<Reservation> _reservations = new();
    private readonly Queue<object> _waiting = new();
    private readonly Queue<double> _overheadPerView = new();
    private readonly Queue<double> _ipLifetimes = new();
    private readonly FairUvAdmission _rate = new(1);
    private int _reserved;
    private bool _pressureBlocked, _disposed;
    private TaskAdmissionSnapshot _snapshot = new(0, 0, 0, 0, 0, 0, 0, "等待任务");

    internal TaskCapacityAdmission(int hardLimit, BusinessMetrics metrics, Func<SystemPressureSample>? probe = null)
    { _hardLimit = hardLimit; _metrics = metrics; _probe = probe ?? (() => SystemPressureProbe.Shared.Latest); }
    internal TaskAdmissionSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    internal bool HasReservations { get { lock (_sync) return _reserved != 0 || _waiting.Count != 0; } }

    internal async Task<Reservation> ReserveAsync(UvCapacityPlan plan, TrafficVistaOptions options, CancellationToken token)
    {
        var ticket = new object();
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _waiting.Enqueue(ticket); }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var memorySlots = RefreshPressure();
                    var duration = EstimateUvMs(plan);
                    var desired = Math.Min(plan.Uvs, options.UvExecutionInterval == 0 ? _hardLimit
                        : (int)Math.Clamp(Math.Ceiling(duration / options.UvExecutionInterval), 1, _hardLimit));
                    var slots = Math.Min(desired, memorySlots);
                    var dispatching = _reservations.Any(r => r.Undispatched > 0);
                    // Preserve FIFO tasks and let an IP-owning task dispatch its remaining UVs first.
                    if (ReferenceEquals(_waiting.Peek(), ticket) && !_pressureBlocked && !dispatching && slots > 0
                        && _reserved + slots <= _hardLimit)
                    {
                        _waiting.Dequeue(); _reserved += slots;
                        var ttl = options.IsProxyMode ? KnownLifetime(options.IpTtl) : null;
                        var predicted = EstimateCompletionMs(plan.Uvs, slots, duration, options.UvExecutionInterval);
                        var result = new Reservation(this, slots, Math.Min(plan.Uvs, memorySlots), plan.Uvs, duration, predicted,
                            ttl is { } seconds && predicted > seconds * 1000);
                        _reservations.Add(result);
                        UpdateSnapshot(memorySlots, "已预留执行名额");
                        return result;
                    }
                    if (!_pressureBlocked)
                        UpdateSnapshot(memorySlots, dispatching ? "优先投递已领 IP 的剩余 UV" : "等待执行名额");
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_waiting.Contains(ticket))
                {
                    var remaining = _waiting.Where(x => !ReferenceEquals(x, ticket)).ToArray();
                    _waiting.Clear(); foreach (var item in remaining) _waiting.Enqueue(item);
                    UpdateSnapshot(_snapshot.MemoryCapacity, _snapshot.Reason);
                }
            }
        }
    }

    internal async Task WaitForFetchAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var capacity = RefreshPressure();
                if (!_pressureBlocked && capacity > 0 && _reserved < _hardLimit && _waiting.Count == 0
                    && !_reservations.Any(r => r.Undispatched > 0)) return;
            }
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private int RefreshPressure()
    {
        var sample = _probe();
        // Many waiting Workers need only this scalar; avoid sorting latency samples/building a full UI snapshot per poll.
        var memoryPerUv = Math.Max(384, _metrics.PeakSessionBrowserCommittedMiB * 1.25) * MiB;
        if (!sample.Available)
        {
            _pressureBlocked = true;
            UpdateSnapshot(0, sample.UnavailableReason ?? "系统资源采样失败，暂停新任务"); return 0;
        }
        // Charge both physical and commit headroom conservatively; this is a budget, not a working-set measurement.
        var reserve = Math.Max(512 * MiB, sample.TotalPhysicalBytes * .1);
        var usable = Math.Min((double)sample.AvailablePhysicalBytes, sample.AvailableCommitBytes) - reserve
            - _reserved * memoryPerUv;
        var slots = (int)Math.Clamp(Math.Floor(usable / memoryPerUv), 0, _hardLimit);
        if (slots < 1 || sample.CpuPercent >= 85) _pressureBlocked = true;
        else if (sample.CpuPercent <= 70 && usable >= memoryPerUv * 1.25) _pressureBlocked = false;
        _snapshot = _snapshot with { CpuPercent = sample.CpuPercent, AvailableMemoryMiB = sample.AvailablePhysicalBytes / MiB };
        UpdateSnapshot(slots, _pressureBlocked ? "系统承压，暂停领取新 IP" : "资源可用");
        return slots;
    }

    private void UpdateSnapshot(int capacity, string reason) => _snapshot = _snapshot with
    {
        ReservedSlots = _reserved, WaitingTasks = _waiting.Count, MemoryCapacity = capacity,
        PendingUvs = _reservations.Sum(r => r.Undispatched),
        DispatchingTasks = _reservations.Count(r => r.Undispatched > 0), Reason = reason
    };

    private double EstimateUvMs(UvCapacityPlan plan)
    {
        var values = _overheadPerView.Order().ToArray();
        var overhead = values.Length == 0 ? 5000 : values[(int)Math.Ceiling(values.Length * .95) - 1];
        return Math.Max(1000, overhead * 1.2 * plan.PageViews + plan.MaximumDwellSeconds * 1000 * plan.PageViews
            + plan.PageDelaySeconds * 1000);
    }
    internal static double EstimateCompletionMs(int count, int slots, double durationMs, int intervalMs)
        => (count - 1L) / slots * Math.Max(durationMs, (double)slots * intervalMs)
            + (count - 1L) % slots * intervalMs + durationMs;
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
        var overhead = Math.Max(500, (elapsed.TotalMilliseconds - plan.MinimumDwellSeconds * 1000 * plan.PageViews
            - plan.PageDelaySeconds * 1000) / plan.PageViews);
        lock (_sync) { _overheadPerView.Enqueue(overhead); if (_overheadPerView.Count > 128) _overheadPerView.Dequeue(); }
    }
    private void Release(Reservation reservation)
    {
        lock (_sync)
        {
            if (!_reservations.Remove(reservation)) return;
            _reserved -= reservation.Slots;
            UpdateSnapshot(_snapshot.MemoryCapacity, "等待下一任务");
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_reserved != 0 || _waiting.Count != 0) throw new InvalidOperationException("准入任务尚未退出。");
            _disposed = true; _rate.Dispose();
        }
    }

    internal sealed class Reservation : IDisposable
    {
        private readonly TaskCapacityAdmission _owner;
        private readonly FairUvAdmission _lanes;
        private int _remaining;
        private int _disposed;
        internal int Slots { get; private set; }
        internal int InitialSlots { get; }
        private readonly int _maximumSlots;
        internal int Undispatched => Volatile.Read(ref _remaining);
        internal double EstimatedUvMs { get; }
        internal double EstimatedCompletionMs { get; }
        internal bool DeadlineAtRisk { get; }
        internal Reservation(TaskCapacityAdmission owner, int slots, int maximumSlots, int uvs, double duration, double completion, bool risk)
        { _owner = owner; Slots = InitialSlots = slots; _maximumSlots = maximumSlots; _remaining = uvs; _lanes = new(slots);
            EstimatedUvMs = duration; EstimatedCompletionMs = completion; DeadlineAtRisk = risk; }
        internal Task AcquireUvAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_lanes.TryAcquire()) return Task.CompletedTask;
            lock (_owner._sync)
            {
                // Underestimated durations must not leave safe, unreserved slots idle for an IP-owning task.
                // Expansion stays within the pre-IP memory budget and global hard cap; it never steals another reservation.
                if (Slots < _maximumSlots && _owner._reserved < _owner._hardLimit)
                {
                    Slots++; _owner._reserved++; _lanes.Release();
                    _owner.UpdateSnapshot(_owner._snapshot.MemoryCapacity, "已有 IP 优先扩展空闲名额");
                }
            }
            return _lanes.AcquireAsync(token);
        }
        internal Task WaitUntilStartDueAsync(int intervalMs, CancellationToken token) => _owner._rate.WaitUntilStartDueAsync(intervalMs, token);
        internal Task StartUvAsync(int intervalMs, CancellationToken token) => _owner._rate.StartAsync(intervalMs, token);
        internal Task RecordDispatchedAsync() => _owner._rate.RecordDispatchedAsync();
        internal void Dispatched()
        {
            lock (_owner._sync)
            {
                Interlocked.Decrement(ref _remaining);
                _owner.UpdateSnapshot(_owner._snapshot.MemoryCapacity, _owner._snapshot.Reason);
            }
        }
        internal void ReleaseUv() => _lanes.Release();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lanes.Dispose(); _owner.Release(this);
        }
    }
}
