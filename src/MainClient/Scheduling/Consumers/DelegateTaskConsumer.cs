namespace TrafficVista.Scheduling.Consumers;

public sealed class DelegateTaskConsumer<T> : ITaskConsumer<T>
{
    private readonly Func<T, CancellationToken, ValueTask<bool>> _consume;

    public DelegateTaskConsumer(Func<T, CancellationToken, ValueTask<bool>> consume)
        => _consume = consume ?? throw new ArgumentNullException(nameof(consume));

    public ValueTask<bool> ConsumeAsync(T item, CancellationToken token) => _consume(item, token);
}
