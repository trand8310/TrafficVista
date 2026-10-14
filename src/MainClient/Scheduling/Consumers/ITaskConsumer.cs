namespace TrafficVista.Scheduling.Consumers;

/// <summary>Handles one item. May be called concurrently up to WorkerCount; must honor cancellation.</summary>
public interface ITaskConsumer<T>
{
    ValueTask<bool> ConsumeAsync(T item, CancellationToken token);
}
