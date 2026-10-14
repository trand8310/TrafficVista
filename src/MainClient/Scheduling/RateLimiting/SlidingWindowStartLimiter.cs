using System.Diagnostics;

namespace TrafficVista.Scheduling.RateLimiting;

/// <summary>
/// Shared per-run rolling one-second admission limit. Serializes only permit acquisition, not execution.
/// Uses monotonic time; holds at most one active delay, irrespective of the number of waiting workers.
/// </summary>
internal sealed class SlidingWindowStartLimiter(int permitsPerSecond) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<long> _granted = new();

    internal async ValueTask WaitAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var now = Stopwatch.GetTimestamp();
                while (_granted.TryPeek(out var oldest) && now - oldest >= Stopwatch.Frequency)
                    _granted.Dequeue();

                if (_granted.Count < permitsPerSecond)
                {
                    _granted.Enqueue(now);
                    return;
                }

                var remaining = Stopwatch.Frequency - (now - _granted.Peek());
                // Round up and recheck after waking: timer resolution must not grant permits early.
                var milliseconds = Math.Max(1, (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
                await Task.Delay(milliseconds, token).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
