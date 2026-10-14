
using TrafficVista.Contracts;

namespace TrafficVista.Scheduling.Producers;

/// <summary>Bridges existing HTTP/demo sources to the producer extension point.</summary>
public sealed class TaskSourceProducer : ITaskProducer<TaskEnvelope>
{
    private readonly ITaskSource _source;

    public TaskSourceProducer(ITaskSource source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    public ValueTask<IReadOnlyList<TaskEnvelope>> ProduceAsync(CancellationToken token) => new(_source.FetchAsync(token));
}
