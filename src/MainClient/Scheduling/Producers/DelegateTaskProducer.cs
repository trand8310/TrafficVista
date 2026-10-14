namespace TrafficVista.Scheduling.Producers;

public sealed class DelegateTaskProducer<T> : ITaskProducer<T>, ITaskFetchAdmission
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<T>>> _produce;
    private readonly Func<CancellationToken, Task>? _waitToFetch;

    public DelegateTaskProducer(Func<CancellationToken, ValueTask<IReadOnlyList<T>>> produce,
        Func<CancellationToken, Task>? waitToFetch = null)
    { _produce = produce ?? throw new ArgumentNullException(nameof(produce)); _waitToFetch = waitToFetch; }

    public Task WaitToFetchAsync(CancellationToken token) => _waitToFetch?.Invoke(token) ?? Task.CompletedTask;

    public ValueTask<IReadOnlyList<T>> ProduceAsync(CancellationToken token) => _produce(token);
}
