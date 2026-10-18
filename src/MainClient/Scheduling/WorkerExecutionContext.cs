namespace TrafficVista.Scheduling;

/// <summary>Logical worker identity, preserved across async continuations; zero is non-worker work.</summary>
internal static class WorkerExecutionContext
{
    private static readonly AsyncLocal<int> Current = new();
    internal static int WorkerId => Current.Value;

    internal static IDisposable Enter(int workerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerId);
        var previous = Current.Value;
        Current.Value = workerId;
        return new Scope(previous);
    }

    private sealed class Scope(int previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = previous;
        }
    }
}
