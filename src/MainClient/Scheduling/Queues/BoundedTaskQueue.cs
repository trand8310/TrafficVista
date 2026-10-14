using System.Threading.Channels;

namespace TrafficVista.Scheduling.Queues;

/// <summary>In-memory FIFO with backpressure; queue order does not imply execution completion order.</summary>
public sealed class BoundedTaskQueue<T> : ITaskQueue<T>
{
    private readonly Channel<T> _channel;

    public BoundedTaskQueue(int capacity, int consumerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCount);
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = consumerCount == 1,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public int Count => _channel.Reader.Count;
    public ValueTask<bool> WaitToWriteAsync(CancellationToken token) => _channel.Writer.WaitToWriteAsync(token);
    public ValueTask EnqueueAsync(T item, CancellationToken token) => _channel.Writer.WriteAsync(item, token);
    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken token) => _channel.Reader.ReadAllAsync(token);
    public bool TryDequeue(out T item) => _channel.Reader.TryRead(out item!);
    public void Complete() => _channel.Writer.TryComplete();
}
