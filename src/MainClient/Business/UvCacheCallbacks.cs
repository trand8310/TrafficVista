namespace TrafficVista.Business;

/// <summary>Admits and drains response/route callbacks before disposing their UV cancellation source.</summary>
internal sealed class UvCacheCallbacks : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly Action<Exception> _onError;
    private readonly HashSet<Task> _tasks = new();
    private bool _accepting = true;
    private bool _disposed;
    private Task _cancel = Task.CompletedTask;

    internal UvCacheCallbacks(CancellationToken token, Action<Exception> onError)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _stop.Token; // Read once while the source is alive; late handlers never access CTS.Token.
        _onError = onError;
    }

    internal Task RunAsync(Func<CancellationToken, Task> callback)
    {
        lock (_sync)
        {
            if (!_accepting || _token.IsCancellationRequested) return Task.CompletedTask;
            _tasks.RemoveWhere(task => task.IsCompleted);
            var operation = ObserveAsync(callback);
            _tasks.Add(operation);
            return operation;
        }
    }

    private async Task ObserveAsync(Func<CancellationToken, Task> callback)
    {
        try { await callback(_token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Event handlers must not throw into Playwright's shared connection dispatcher.
            try { _onError(ex); } catch { }
        }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (!_accepting) return;
            _accepting = false;
            _cancel = _stop.CancelAsync();
        }
    }

    internal async Task DrainAsync()
    {
        Stop();
        Task[] tasks;
        Task cancel;
        lock (_sync) { tasks = _tasks.ToArray(); cancel = _cancel; }
        try { await cancel.ConfigureAwait(false); }
        catch (Exception ex) { try { _onError(ex); } catch { } }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    internal Task WaitCurrentAsync()
    {
        lock (_sync) return Task.WhenAll(_tasks.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Dispose();
        }
    }
}
