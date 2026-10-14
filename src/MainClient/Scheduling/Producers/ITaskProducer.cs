namespace TrafficVista.Scheduling.Producers;

/// <summary>Returns one bounded batch per call. Called serially by the scheduler.</summary>
public interface ITaskProducer<T>
{
    ValueTask<IReadOnlyList<T>> ProduceAsync(CancellationToken token);
}
