using TrafficVista.Business;

namespace TrafficVista;

// DI 单例：所有实例共享启动并发额度，避免瞬间创建大量 Chrome 进程。
public sealed class BrowserLaunchLimiter : IDisposable
{
    private readonly object _sync = new();
    private SemaphoreSlim _slots;
    private int _users;
    private bool _disposed;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly Func<SystemPressureSample>? _pressure;
    private int _resourceStarts;
    private readonly LinkedList<object> _resourceWaiters = new();
    private long _lastStart;
    public int StartIntervalMilliseconds { get; }
    private int _activeStarts;
    private int _peakStarts;
    private int _waitingStarts;
    internal event Action<int>? WaitingStartsChanged;
    public int WaitingStarts { get { lock (_sync) return _waitingStarts; } }

    public BrowserLaunchLimiter(int maxConcurrentStarts, int startIntervalMilliseconds = 0)
        : this(maxConcurrentStarts, startIntervalMilliseconds, null) { }

    internal BrowserLaunchLimiter(int maxConcurrentStarts, int startIntervalMilliseconds, Func<SystemPressureSample>? pressure)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentStarts, 1);
        MaxConcurrentStarts = maxConcurrentStarts;
        ArgumentOutOfRangeException.ThrowIfNegative(startIntervalMilliseconds);
        StartIntervalMilliseconds = startIntervalMilliseconds;
        _slots = new SemaphoreSlim(maxConcurrentStarts, maxConcurrentStarts);
        _pressure = pressure;
    }

    public int MaxConcurrentStarts { get; private set; }
    public int PeakConcurrentStarts => Volatile.Read(ref _peakStarts);
    internal int EffectiveStartLimit => _pressure is null ? MaxConcurrentStarts : ResourceStartLimit(_pressure(), MaxConcurrentStarts);

    internal static int ResourceStartLimit(SystemPressureSample sample, int configured)
    {
        // Leave headroom for existing pages, the driver and process cleanup. Never cancel active UVs.
        const ulong reserve = 768UL * 1024 * 1024;
        const ulong startupBudget = 256UL * 1024 * 1024;
        var limit = configured;
        if (sample.TotalPhysicalBytes > 0)
        {
            var available = Math.Min(sample.AvailablePhysicalBytes, sample.AvailableCommitBytes);
            if (available < reserve + startupBudget) return 0;
            limit = (int)Math.Min((ulong)configured, (available - reserve) / startupBudget);
        }
        if (!sample.Available) return limit;
        if (sample.CpuPercent >= 93) return Math.Min(limit, 1);
        if (sample.CpuPercent >= 80) return Math.Min(limit, Math.Max(1, configured / 2));
        return limit;
    }

    private async Task ReserveResourceStartAsync(CancellationToken token)
    {
        LinkedListNode<object> waiter;
        lock (_sync) waiter = _resourceWaiters.AddLast(new object());
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (ReferenceEquals(_resourceWaiters.First, waiter) && _resourceStarts < EffectiveStartLimit)
                    { _resourceWaiters.Remove(waiter); _resourceStarts++; return; }
                }
                // Samples refresh once per second. FIFO prevents newer UVs overtaking resource waiters.
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally { lock (_sync) if (waiter.List is not null) _resourceWaiters.Remove(waiter); }
    }

    public async Task ExecuteAsync(Func<Task> start, CancellationToken cancellationToken, Action? queued = null)
    {
        SemaphoreSlim slots;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            slots = _slots;
            _users++;
            _waitingStarts++;
            WaitingStartsChanged?.Invoke(_waitingStarts);
        }
        var waiting = true;
        void Starting()
        {
            lock (_sync)
            {
                waiting = false;
                _waitingStarts--;
                WaitingStartsChanged?.Invoke(_waitingStarts);
            }
        }
        try
        {
            queued?.Invoke();
            await ExecuteCoreAsync(slots, start, Starting, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (waiting)
                {
                    _waitingStarts--;
                    WaitingStartsChanged?.Invoke(_waitingStarts);
                }
                _users--;
            }
        }
    }

    // Only change between runs, after all pending starts have exited.
    public void Configure(int maxConcurrentStarts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentStarts, 1);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_users != 0) throw new InvalidOperationException("浏览器仍在启动或等待启动。");
            _slots.Dispose();
            _slots = new SemaphoreSlim(maxConcurrentStarts, maxConcurrentStarts);
            MaxConcurrentStarts = maxConcurrentStarts;
            _peakStarts = 0;
            _lastStart = 0;
        }
    }

    private async Task ExecuteCoreAsync(SemaphoreSlim slots, Func<Task> start, Action starting, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reserved = false;
        var active = false;
        try
        {
            await ReserveResourceStartAsync(cancellationToken).ConfigureAwait(false);
            reserved = true;
            await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_lastStart != 0)
                {
                    var remaining = TimeSpan.FromMilliseconds(StartIntervalMilliseconds)
                        - System.Diagnostics.Stopwatch.GetElapsedTime(_lastStart);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _lastStart = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            finally { _pace.Release(); }
            var count = Interlocked.Increment(ref _activeStarts);
            active = true;
            starting();
            int previous;
            do { previous = Volatile.Read(ref _peakStarts); }
            while (count > previous && Interlocked.CompareExchange(ref _peakStarts, count, previous) != previous);
            cancellationToken.ThrowIfCancellationRequested();
            await start().ConfigureAwait(false);
        }
        finally
        {
            if (active) Interlocked.Decrement(ref _activeStarts);
            if (reserved) { lock (_sync) _resourceStarts--; }
            slots.Release();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_users != 0) throw new InvalidOperationException("浏览器仍在启动或等待启动。");
            _disposed = true;
            _slots.Dispose();
            _pace.Dispose();
        }
    }
}
