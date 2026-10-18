using System.Threading.Channels;

namespace TrafficVista.Scheduling.Queues;

/// <summary>In-memory FIFO with backpressure; queue order does not imply execution completion order.</summary>
public sealed class BoundedTaskQueue<T> : ITaskQueue<T>
{
    private readonly Channel<T> _channel;
    private readonly Func<CancellationToken, ValueTask>? _beforeDequeue;
    private readonly Func<T, IDisposable?>? _reserveDequeue;
    private readonly object _readSync = new();

    public BoundedTaskQueue(int capacity, int consumerCount, Func<CancellationToken, ValueTask>? beforeDequeue = null,
        Func<T, IDisposable?>? reserveDequeue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCount);
        _beforeDequeue = beforeDequeue;
        _reserveDequeue = reserveDequeue;
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
    public async IAsyncEnumerable<T> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        while (await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            if (_beforeDequeue is not null) await _beforeDequeue(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            T item = default!;
            IDisposable? reservation = null;
            bool read = false;
            lock (_readSync)
            {
                if (_channel.Reader.TryPeek(out var next))
                {
                    reservation = _reserveDequeue?.Invoke(next);
                    if (_reserveDequeue is null || reservation is not null)
                        read = _channel.Reader.TryRead(out item!);
                }
            }
            if (!read) { reservation?.Dispose(); continue; }
            // Keep the reservation until this worker finishes or abandons the item.
            using (reservation) yield return item;
        }
    }
    public bool TryDequeue(out T item) { lock (_readSync) return _channel.Reader.TryRead(out item!); }
    public void Complete() => _channel.Writer.TryComplete();
}
