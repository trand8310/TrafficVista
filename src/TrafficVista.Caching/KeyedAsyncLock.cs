namespace TrafficVista.Caching;

internal sealed class KeyedAsyncLock
{
    private sealed class Gate { internal readonly SemaphoreSlim Semaphore = new(1, 1); internal int Users; }
    private readonly object _sync = new();
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    internal async Task<IDisposable> EnterAsync(string key, CancellationToken token)
    {
        Gate gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(key, out gate!)) _gates.Add(key, gate = new());
            gate.Users++;
        }
        try { await gate.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { Release(key, gate, false); throw; }
        return new Scope(() => Release(key, gate, true));
    }
    private void Release(string key, Gate gate, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) gate.Semaphore.Release();
            if (--gate.Users == 0) { _gates.Remove(key); gate.Semaphore.Dispose(); }
        }
    }
    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
