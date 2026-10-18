using Microsoft.Playwright;
using Serilog;

namespace TrafficVista.Business;

public sealed record PlaywrightDriversSnapshot(int Configured, int Effective, int Created, int Initializing, int ActiveUvs);

/// <summary>Stable worker-to-driver mapping. Each UV still owns its independent browser.</summary>
public sealed class BusinessPlaywrightDrivers : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly HashSet<Task> _releases = new();
    private readonly Func<Task<IPlaywright>> _create;
    private readonly ILogger _logger;
    private int _configured = 1, _effective = 1, _users;
    private bool _disposed;
    private sealed class Entry(Func<Task<IPlaywright>> create)
    {
        internal readonly Lazy<Task<IPlaywright>> Driver = new(() => Task.Run(create));
        internal int Users;
    }

    public BusinessPlaywrightDrivers(ILogger logger) : this(logger, Playwright.CreateAsync) { }
    internal BusinessPlaywrightDrivers(ILogger logger, Func<Task<IPlaywright>> create)
    { _logger = logger.ForContext<BusinessPlaywrightDrivers>(); _create = create; }

    public void Configure(int configured, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(configured);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configured, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, 1024);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_users != 0) throw new InvalidOperationException("业务浏览器仍在使用Playwright驱动。");
            _configured = configured;
            _effective = configured == 0 ? workers : Math.Min(configured, workers);
        }
    }

    internal int DriverIdFor(int workerId)
    { lock (_sync) return workerId > 0 ? (workerId - 1) % _effective + 1 : 1; }

    internal sealed class Lease(IPlaywright driver, int id, Action release) : IDisposable
    {
        internal IPlaywright Driver { get; } = driver;
        internal int Id { get; } = id;
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal async Task<Lease> AcquireAsync(int workerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry entry;
        int id;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = DriverIdFor(workerId);
            if (!_entries.TryGetValue(id, out entry!))
                _entries.Add(id, entry = new Entry(async () =>
                {
                    var driver = await _create().ConfigureAwait(false);
                    try { _logger.Information("Playwright驱动已创建，DriverId={DriverId}", id); } catch { }
                    return driver;
                }));
            entry.Users++; _users++;
        }
        void Release()
        {
            lock (_sync)
            {
                entry.Users--; _users--;
                if (entry.Driver.IsValueCreated && entry.Driver.Value.IsFaulted
                    && _entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(id);
            }
        }
        try
        {
            // Creation has no cancellation API: retain ownership until it returns, then honor cancellation.
            var driver = await entry.Driver.Value.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(driver, id, Release);
        }
        catch { Release(); throw; }
    }

    public PlaywrightDriversSnapshot Snapshot
    {
        get
        {
            lock (_sync) return new(_configured, _effective,
                _entries.Values.Count(e => e.Driver.IsValueCreated && e.Driver.Value.IsCompletedSuccessfully),
                _entries.Values.Count(e => e.Driver.IsValueCreated && !e.Driver.Value.IsCompleted), _users);
        }
    }

    public async Task<bool> ReleaseIdleAsync()
    {
        Task release;
        lock (_sync)
        {
            if (_users != 0 || BrowserCloseCoordinator.Shared.Snapshot.InFlight != 0) return false;
            if (_entries.Count == 0) return true;
            var entries = _entries.Values.ToArray();
            _entries.Clear();
            release = Task.Run(() => DisposeEntriesAsync(entries));
            _releases.Add(release);
        }
        try { await release.ConfigureAwait(false); }
        finally { lock (_sync) _releases.Remove(release); }
        return true;
    }

    private async Task DisposeEntriesAsync(Entry[] entries)
    {
        await Task.WhenAll(entries.Where(e => e.Driver.IsValueCreated).Select(async entry =>
        {
            IPlaywright driver;
            try { driver = await entry.Driver.Value.ConfigureAwait(false); }
            catch { return; } // A failed creation is observed by its acquire caller.
            await Task.Run(driver.Dispose).ConfigureAwait(false);
        })).ConfigureAwait(false);
        if (entries.Length != 0) _logger.Information("业务Playwright驱动已释放，Count={Count}", entries.Length);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        Task[] releases;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
            releases = _releases.ToArray();
        }
        await BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.WhenAll(releases.Append(DisposeEntriesAsync(entries))).ConfigureAwait(false);
    }
}
