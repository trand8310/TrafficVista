namespace TrafficVista.Scheduling.Consumers;

/// <summary>Optional resource reservation BEFORE the task is durably consumed. No business dispatch in Prepare.</summary>
public interface IPreparedTaskConsumer<T> : ITaskConsumer<T>
{
    ValueTask<IPreparedTask> PrepareAsync(T item, CancellationToken token);
}

/// <summary>One-shot execution. Dispose must wait for real resource cleanup; never detach active work.</summary>
public interface IPreparedTask : IAsyncDisposable
{
    ValueTask<bool> ExecuteAsync(CancellationToken token);
}
