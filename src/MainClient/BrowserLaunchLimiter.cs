namespace TrafficVista;

// DI 单例：所有实例共享启动并发额度，避免瞬间创建大量 Chrome 进程。
public sealed class BrowserLaunchLimiter : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private long _lastStart;
    public int StartIntervalMilliseconds { get; }
    private int _activeStarts;
    private int _peakStarts;

    public BrowserLaunchLimiter(int maxConcurrentStarts, int startIntervalMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentStarts, 1);
        MaxConcurrentStarts = maxConcurrentStarts;
        ArgumentOutOfRangeException.ThrowIfNegative(startIntervalMilliseconds);
        StartIntervalMilliseconds = startIntervalMilliseconds;
        _slots = new SemaphoreSlim(maxConcurrentStarts, maxConcurrentStarts);
    }

    public int MaxConcurrentStarts { get; }
    public int PeakConcurrentStarts => Volatile.Read(ref _peakStarts);

    public async Task ExecuteAsync(Func<Task> start, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        }
        catch { _slots.Release(); throw; }
        var active = Interlocked.Increment(ref _activeStarts);
        int previous;
        do
        {
            previous = Volatile.Read(ref _peakStarts);
        } while (active > previous && Interlocked.CompareExchange(ref _peakStarts, active, previous) != previous);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await start().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeStarts);
            _slots.Release();
        }
    }

    public void Dispose() { _slots.Dispose(); _pace.Dispose(); }
}
