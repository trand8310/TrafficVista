namespace TrafficVista.Scheduling.Queues;

/// <summary>
/// Per-run bounded queue, one writer and multiple readers. Enqueue must wait when full, never drop items.
/// All blocking operations must honor cancellation. Completion allows already admitted items to drain.
/// Count, TryDequeue and Complete must be thread-safe and nonthrowing for a valid queue instance.
/// </summary>
public interface ITaskQueue<T>
{
    int Count { get; }
    ValueTask<bool> WaitToWriteAsync(CancellationToken token);
    ValueTask EnqueueAsync(T item, CancellationToken token);
    IAsyncEnumerable<T> ReadAllAsync(CancellationToken token);
    bool TryDequeue(out T item);
    void Complete();
}
