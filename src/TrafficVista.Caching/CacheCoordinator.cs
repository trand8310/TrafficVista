using System.Runtime.ExceptionServices;

namespace TrafficVista.Caching;

/// <summary>A hit or an origin-owner ticket. Dispose an owner after publishing or abandoning its response.</summary>
public sealed class CacheAccess : IDisposable
{
    private Action<Exception?>? _complete;
    private readonly HybridResourceCache? _cache;
    private readonly long _generation;
    internal CacheAccess(CacheReadLease? hit) { Hit = hit; Completion = Task.FromResult<Exception?>(null); }
    internal CacheAccess(HybridResourceCache cache, long generation, Task<Exception?> completion, Action<Exception?> complete)
    { _cache = cache; _generation = generation; IsOwner = true; Completion = completion; _complete = complete; }
    public bool IsOwner { get; }
    public CacheReadLease? Hit { get; private set; }
    internal Task<Exception?> Completion { get; }
    public Task StoreAsync(ResourceContent content, CancellationToken token = default)
        => _cache is null || _complete is null ? throw new InvalidOperationException("An active origin-owner ticket is required.")
            : _cache.StoreOwnedAsync(content, _generation, this, token);
    internal string? Key { get; init; }
    internal CacheReadLease? TakeHit() { var hit = Hit; Hit = null; return hit; }
    public void Fail(Exception error) => Interlocked.Exchange(ref _complete, null)?.Invoke(error);
    public void Dispose()
    {
        Interlocked.Exchange(ref _complete, null)?.Invoke(null);
        Hit?.Dispose(); Hit = null;
    }
}

/// <summary>One origin owner per key. Other keys proceed independently; canceling a waiter leaves the owner intact.</summary>
public sealed class CacheCoordinator
{
    private sealed class Flight(long generation)
    { internal long Generation { get; } = generation; internal readonly TaskCompletionSource<Exception?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private readonly HybridResourceCache _cache;
    private readonly object _sync = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private bool _closed;
    private int _waitingRequests;
    internal (int Origins, int Waiters) Current
    {
        get { lock (_sync) return (_flights.Count, _waitingRequests); }
    }
    internal CacheCoordinator(HybridResourceCache cache) => _cache = cache;

    public async Task<CacheAccess> AcquireAsync(string key, CancellationToken token = default)
    {
        var hit = await _cache.TryGetAsync(key, token).ConfigureAwait(false);
        if (hit is not null) return new(hit);
        Flight flight; bool owner;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            owner = !_flights.TryGetValue(key, out flight!);
            if (owner) _flights.Add(key, flight = new(_cache.Generation));
            else _waitingRequests++;
        }
        if (owner)
        {
            var access = new CacheAccess(_cache, flight.Generation, flight.Done.Task, error => Complete(key, flight, error)) { Key = key };
            try
            {
                // A previous owner may have published between the first lookup and acquiring the ticket.
                hit = await _cache.TryGetAsync(key, token).ConfigureAwait(false);
                if (hit is not null) { access.Dispose(); return new(hit); }
                _cache.CountOrigin();
                return access;
            }
            catch { access.Dispose(); throw; }
        }
        _cache.CountCoalesced();
        Exception? error;
        try { error = await flight.Done.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { lock (_sync) _waitingRequests--; }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return new(await _cache.TryGetAsync(key, token).ConfigureAwait(false));
    }

    public async Task<CacheReadLease?> GetOrCreateAsync(string key,
        Func<CancellationToken, Task<ResourceContent?>> factory, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var access = await AcquireAsync(key, token).ConfigureAwait(false);
        if (!access.IsOwner)
        { using (access) return access.TakeHit(); }
        IDisposable operation;
        try { operation = _cache.EnterOperation(); }
        catch { access.Dispose(); throw; }
        _ = Task.Run(async () =>
        {
            using (operation)
            using (access)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cache.ShutdownToken))
            {
                timeout.CancelAfter(_cache.FactoryTimeout);
                try
                {
                    var content = await factory(timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    if (content is not null)
                        await access.StoreAsync(content, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { access.Fail(ex); }
            }
        });
        var error = await access.Completion.WaitAsync(token).ConfigureAwait(false);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return await _cache.TryGetAsync(key, token).ConfigureAwait(false);
    }

    private void Complete(string key, Flight flight, Exception? error)
    {
        lock (_sync)
        {
            if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            flight.Done.TrySetResult(error);
        }
    }
    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
            foreach (var flight in _flights.Values) flight.Done.TrySetResult(new ObjectDisposedException(nameof(HybridResourceCache)));
            _flights.Clear();
        }
    }
    internal void InvalidateBefore(long generation)
    {
        lock (_sync)
            foreach (var item in _flights.Where(item => item.Value.Generation < generation).ToArray())
            { _flights.Remove(item.Key); item.Value.Done.TrySetResult(new OperationCanceledException("Cache cleared during origin fill.")); }
    }
}
