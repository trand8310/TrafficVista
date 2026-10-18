using System.Threading.Channels;

namespace TrafficVista.Caching;

/// <summary>Bounded LRU memory, persistent disk bodies, leased reads and ordered disk publication.</summary>
public sealed class HybridResourceCache : IDisposable, IAsyncDisposable
{
    private sealed record Write(DiskCacheStore.Staged? Staged, long Generation, CancellationToken Token,
        TaskCompletionSource Done);
    private readonly HybridCacheOptions _options;
    private readonly MemoryCacheStore _memory;
    private readonly DiskCacheStore _disk;
    private readonly KeyedAsyncLock _readLocks = new();
    private readonly SemaphoreSlim _diskIo;
    private readonly SemaphoreSlim _writeSlots;
    private readonly Channel<Write> _writes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _publish = new(), _lifetime = new();
    private readonly Task _initialize, _writer;
    private Task? _dispose;
    private bool _disposed;
    private int _operations, _pendingWrites;
    private TaskCompletionSource? _idle;
    private long _generation, _memoryHits, _diskHits, _misses, _originFills, _coalesced, _errors;
    public CacheCoordinator Coordinator { get; }
    public string DirectoryPath => _disk.Root;
    internal CancellationToken ShutdownToken => _shutdown.Token;
    internal TimeSpan FactoryTimeout => _options.FactoryTimeout;
    internal long Generation => Interlocked.Read(ref _generation);

    public HybridResourceCache(HybridCacheOptions options)
    {
        options.Validate();
        _options = options with { DirectoryPath = Path.GetFullPath(options.DirectoryPath) };
        _memory = new(_options); _disk = new(_options, Report);
        _diskIo = new(options.DiskConcurrency, options.DiskConcurrency);
        _writeSlots = new(checked(options.WriteQueueCapacity + 1), checked(options.WriteQueueCapacity + 1));
        _writes = Channel.CreateBounded<Write>(new BoundedChannelOptions(options.WriteQueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
        Coordinator = new(this);
        _initialize = Task.Run(() => _disk.InitializeAsync(_shutdown.Token));
        _writer = Task.Run(WriteLoopAsync);
    }

    public CacheStatistics Statistics
    {
        get
        {
            var memory = _memory.Size; var disk = _disk.Size;
            var coordination = Coordinator.Current;
            return new(Interlocked.Read(ref _memoryHits), Interlocked.Read(ref _diskHits), Interlocked.Read(ref _misses),
                Interlocked.Read(ref _originFills), Interlocked.Read(ref _coalesced), memory.Bytes, memory.Count,
                disk.Bytes, disk.Count, Volatile.Read(ref _pendingWrites), Interlocked.Read(ref _errors))
                { QueuedWrites = _writes.Reader.Count, ActiveOriginFills = coordination.Origins,
                    WaitingRequests = coordination.Waiters };
        }
    }
    internal void CountOrigin() => Interlocked.Increment(ref _originFills);
    internal void CountCoalesced() => Interlocked.Increment(ref _coalesced);

    public async Task<CacheReadLease?> TryGetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var operation = EnterOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (_memory.TryGet(key) is { } hot) { Interlocked.Increment(ref _memoryHits); return hot; }
        await _initialize.WaitAsync(token).ConfigureAwait(false);
        using var gate = await _readLocks.EnterAsync(key, token).ConfigureAwait(false);
        if (_memory.TryGet(key) is { } filled) { Interlocked.Increment(ref _memoryHits); return filled; }
        var disk = _disk.TryGet(key);
        if (disk is null) { Interlocked.Increment(ref _misses); return null; }
        var (lease, record) = disk.Value;
        Interlocked.Increment(ref _diskHits);
        if (!_memory.CanHold(record.Data.Length)) return lease;
        try
        {
            byte[] body;
            await _diskIo.WaitAsync(token).ConfigureAwait(false);
            try { body = await lease.ReadBodyAsync(token).ConfigureAwait(false); }
            finally { _diskIo.Release(); }
            lock (_publish)
                if (_disk.IsCurrent(record)) body = _memory.Set(key, body, lease.Metadata, lease.StoredAt,
                    lease.Metadata.DeduplicateBody ? record.Path : null);
            return new(MemoryCacheStore.Copy(lease.Metadata), lease.StoredAt, body, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); return null; }
        finally { lease.Dispose(); }
    }

    /// <summary>Force a leased disk read, including for resources that also fit in memory.</summary>
    public async Task<CacheReadLease?> TryGetFileAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var operation = EnterOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _initialize.WaitAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return _disk.TryGet(key)?.Lease;
    }

    public Task<CacheReadLease?> GetOrCreateAsync(string key, Func<CancellationToken, Task<ResourceContent?>> factory,
        CancellationToken token = default) => Coordinator.GetOrCreateAsync(key, factory, token);

    public async Task StoreAsync(string key, ResourceMetadata metadata, ReadOnlyMemory<byte> body, CancellationToken token = default)
    {
        // The caller retains its buffer until this await completes. No large queued byte-array copies.
        using var input = new ReadOnlyMemoryStream(body);
        await StoreStreamAsync(key, metadata, input, token).ConfigureAwait(false);
    }
    internal async Task StoreOwnedAsync(ResourceContent content, long generation, CacheAccess access, CancellationToken token)
    {
        using var input = new ReadOnlyMemoryStream(content.Body);
        await StoreCoreAsync(access.Key!, content.Metadata, input, token, generation).ConfigureAwait(false);
    }
    public Task StoreStreamAsync(string key, ResourceMetadata metadata, Stream body, CancellationToken token = default)
        => StoreCoreAsync(key, metadata, body, token, null);
    private async Task StoreCoreAsync(string key, ResourceMetadata metadata, Stream body, CancellationToken token, long? expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(metadata.ContentType);
        if (metadata.StatusCode is < 100 or > 599) throw new ArgumentOutOfRangeException(nameof(metadata));
        using var operation = EnterOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _initialize.WaitAsync(linked.Token).ConfigureAwait(false);
        var generation = expectedGeneration ?? Interlocked.Read(ref _generation);
        await _writeSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        DiskCacheStore.Staged? staged = null;
        var queued = false;
        try
        {
            await _diskIo.WaitAsync(linked.Token).ConfigureAwait(false);
            try { staged = await _disk.StageAsync(key, metadata, body, linked.Token).ConfigureAwait(false); }
            finally { _diskIo.Release(); }
            var write = new Write(staged, generation, token, Completion());
            Interlocked.Increment(ref _pendingWrites);
            try { await _writes.Writer.WriteAsync(write, linked.Token).ConfigureAwait(false); queued = true; }
            catch { Interlocked.Decrement(ref _pendingWrites); throw; }
            // Once queued, the writer owns staging files even if the caller stops waiting.
            await write.Done.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (!queued)
            { if (staged is not null) DiskCacheStore.Cleanup(staged); _writeSlots.Release(); }
        }
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        using var operation = EnterOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _initialize.WaitAsync(linked.Token).ConfigureAwait(false);
        await _writeSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        var write = new Write(null, 0, token, Completion());
        Interlocked.Increment(ref _pendingWrites);
        try { await _writes.Writer.WriteAsync(write, linked.Token).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref _pendingWrites); _writeSlots.Release(); throw; }
        await write.Done.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                write.Token.ThrowIfCancellationRequested();
                if (write.Staged is not { } staged)
                {
                    lock (_publish)
                    {
                        Interlocked.Increment(ref _generation);
                        Coordinator.InvalidateBefore(Generation);
                        _memory.Clear();
                        _disk.Clear();
                    }
                }
                else
                {
                    DiskCacheStore.Record? published = null;
                    lock (_publish)
                        if (write.Generation == Interlocked.Read(ref _generation))
                        {
                            published = _disk.Publish(staged);
                            _memory.Remove(staged.Key);
                        }
                    if (published is not null && _memory.CanHold(published.Data.Length))
                    {
                        try
                        {
                            // Hold a disk lease while warming memory so Clear/eviction cannot remove this file.
                            var entry = _disk.TryGet(staged.Key);
                            if (entry is { } value)
                                using (value.Lease)
                                {
                                    var body = await value.Lease.ReadBodyAsync().ConfigureAwait(false);
                                    lock (_publish)
                                        if (_disk.IsCurrent(value.Record))
                                            _memory.Set(staged.Key, body, value.Lease.Metadata, value.Lease.StoredAt,
                                                value.Lease.Metadata.DeduplicateBody ? value.Record.Path : null);
                                }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); }
                    }
                }
                write.Done.TrySetResult();
            }
            catch (OperationCanceledException) { write.Done.TrySetCanceled(write.Token); }
            catch (Exception ex) { Report(ex); write.Done.TrySetException(ex); }
            finally
            {
                if (write.Staged is { } staged) DiskCacheStore.Cleanup(staged);
                Interlocked.Decrement(ref _pendingWrites);
                _writeSlots.Release();
            }
        }
    }

    internal IDisposable EnterOperation()
    {
        lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); _operations++; }
        return new Operation(() => { lock (_lifetime) if (--_operations == 0) _idle?.TrySetResult(); });
    }
    private sealed class Operation(Action end) : IDisposable
    { private Action? _end = end; public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke(); }
    private void Report(Exception ex)
    { Interlocked.Increment(ref _errors); try { _options.OnError?.Invoke(ex); } catch { /* Diagnostics cannot break cache ownership. */ } }
    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_operations == 0) _idle.SetResult();
            return new(_dispose = DisposeCoreAsync(_idle.Task));
        }
    }
    private async Task DisposeCoreAsync(Task idle)
    {
        Coordinator.Close();
        _writes.Writer.TryComplete();
        try { await _shutdown.CancelAsync().ConfigureAwait(false); } catch (Exception ex) { Report(ex); }
        await idle.ConfigureAwait(false);
        await _writer.ConfigureAwait(false);
        try { await _initialize.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { Report(ex); }
        _memory.Clear(); _diskIo.Dispose(); _writeSlots.Dispose(); _shutdown.Dispose();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static TaskCompletionSource Completion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion;
    }

    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> body) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        { var count = Math.Min(buffer.Length, body.Length - _position); body.Span.Slice(_position, count).CopyTo(buffer); _position += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
