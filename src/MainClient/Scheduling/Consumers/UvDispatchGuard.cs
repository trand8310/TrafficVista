using TrafficVista.Business;

namespace TrafficVista.Scheduling.Consumers;

/// <summary>Task-local completion-order streak; cancels dispatch waits, never active UVs.</summary>
internal sealed class UvDispatchGuard(CancellationToken taskToken) : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _dispatch = CancellationTokenSource.CreateLinkedTokenSource(taskToken);
    private int _consecutive;
    private bool _stopped;
    private string? _reason;
    internal CancellationToken Token => _dispatch.Token;
    internal bool Stopped { get { lock (_sync) return _stopped; } }

    internal string? Reason { get { lock (_sync) return _reason; } }

    internal bool Record(bool success, Exception? error, bool canceled, bool hasProxy = false)
    {
        lock (_sync)
        {
            if (_stopped || canceled) return false;
            var proxyCode = !success && hasProxy ? ProxyUnavailableException.FindCode(error) : null;
            if (proxyCode is not null) _reason = "proxy-unavailable:" + proxyCode;
            else
            {
                _consecutive = !success && PageHttpStatusException.Contains644(error) ? _consecutive + 1 : 0;
                if (_consecutive < 3) return false;
                _reason = "consecutive-http-644";
            }
            _stopped = true;
        }
        _dispatch.Cancel();
        return true;
    }

    internal bool TryStart(Func<Task<bool>> start, out Task<bool>? running)
    {
        lock (_sync)
        {
            running = null;
            if (_stopped) return false;
            Token.ThrowIfCancellationRequested();
            running = start();
            return true;
        }
    }

    public void Dispose() => _dispatch.Dispose();
}
