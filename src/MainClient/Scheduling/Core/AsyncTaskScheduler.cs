using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Producers;
using TrafficVista.Scheduling.Queues;
using TrafficVista.Scheduling.Persistence;

namespace TrafficVista.Scheduling;

/// <summary>
/// Bounded producer/worker scheduler with JSON.NET persistence. Source is called serially; execute is called concurrently.
/// Delegates must honor cancellation. No automatic execution retries, per-item Task.Run or UI events.
/// </summary>
public sealed class AsyncTaskScheduler<T> : IAsyncDisposable
{
    private readonly ITaskProducer<T> _producer;
    private readonly ITaskConsumer<T> _consumer;
    private readonly Func<SchedulerOptions, ITaskQueue<T>> _queueFactory;
    private readonly Func<T, string>? _cacheKeySelector;
    private readonly object _lifecycle = new();
    private SchedulerRun<T>? _run;
    private SchedulerState _state;
    private bool _disposeRequested;

    public AsyncTaskScheduler(Func<CancellationToken, ValueTask<IReadOnlyList<T>>> fetch,
        Func<T, CancellationToken, ValueTask<bool>> execute,
        Func<SchedulerOptions, ITaskQueue<T>>? queueFactory = null, Func<T, string>? cacheKeySelector = null)
        : this(new DelegateTaskProducer<T>(fetch), new DelegateTaskConsumer<T>(execute), queueFactory, cacheKeySelector)
    {
    }

    public AsyncTaskScheduler(ITaskProducer<T> producer, ITaskConsumer<T> consumer,
        Func<SchedulerOptions, ITaskQueue<T>>? queueFactory = null, Func<T, string>? cacheKeySelector = null)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _queueFactory = queueFactory ?? (options => new BoundedTaskQueue<T>(options.QueueCapacity, options.WorkerCount));
        _cacheKeySelector = cacheKeySelector;
    }

    /// <summary>Convenience entry for existing async Task methods, avoiding ambiguous Task/ValueTask overloads.</summary>
    public static AsyncTaskScheduler<T> FromMethods(
        Func<CancellationToken, Task<IReadOnlyList<T>>> producer,
        Func<T, CancellationToken, Task<bool>> consumer,
        Func<SchedulerOptions, ITaskQueue<T>>? queueFactory = null, Func<T, string>? cacheKeySelector = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(consumer);
        return new(token => new(producer(token)), (item, token) => new(consumer(item, token)), queueFactory, cacheKeySelector);
    }

    public bool IsRunning { get { lock (_lifecycle) return _run is { Finished.Task.IsCompleted: false }; } }
    public SchedulerSnapshot Snapshot { get { lock (_lifecycle) return _run?.Snapshot(_state) ?? new() { State = _state }; } }
    /// <summary>Completes only after every worker and producer exits. Fault details are in the returned snapshot.</summary>
    public Task<SchedulerSnapshot> Completion
    {
        get { lock (_lifecycle) return _run?.Finished.Task ?? Task.FromResult(new SchedulerSnapshot { State = _state }); }
    }

    public Task StartAsync(SchedulerOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.LocalCache is not null && _cacheKeySelector is null)
            throw new InvalidOperationException("启用本地缓存时必须传入稳定且唯一的 cacheKeySelector。");
        token.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_run is { Finished.Task.IsCompleted: false })
                throw new InvalidOperationException("上一轮调度仍在运行或停止中，请等待 Completion 后重启。");
            var queue = _queueFactory(options) ?? throw new InvalidOperationException("队列工厂返回了 null。");
            var run = new SchedulerRun<T>(options, queue, token);
            _run = run;
            _state = SchedulerState.Running;
            _ = Task.Run(() => SuperviseAsync(run));
        }
        return Task.CompletedTask;
    }

    /// <summary>Timeout/caller cancellation stops waiting only. The run stays owned until Completion.</summary>
    public async Task StopAsync(SchedulerStopMode mode = SchedulerStopMode.Cancel, CancellationToken token = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        SchedulerRun<T>? run;
        lock (_lifecycle)
        {
            run = _run;
            if (run is null || run.Finished.Task.IsCompleted) return;
            _state = SchedulerState.Stopping;
            RequestCancellation(run, mode);
        }
        await run.Finished.Task.WaitAsync(run.Options.StopTimeout, token).ConfigureAwait(false);
    }

    // The lock protects CTS lifetime and allows Drain to be escalated to Cancel.
    private void RequestCancellation(SchedulerRun<T> run, SchedulerStopMode mode)
    {
        if (!run.ProducerStopRequested)
        {
            run.ProducerStopRequested = true;
            run.ProducerCancelTask = run.ProducerCancellation.CancelAsync();
        }
        if (mode == SchedulerStopMode.Cancel && !run.WorkerStopRequested)
        {
            run.WorkerStopRequested = true;
            run.WorkerCancelTask = run.WorkerCancellation.CancelAsync();
        }
    }

    private async Task SuperviseAsync(SchedulerRun<T> run)
    {
        Exception? fatal = null;
        Task[] workers = [];
        try
        {
            if (run.Options.LocalCache is { } cacheOptions)
                run.Cache = await LocalTaskCache<T>.OpenAsync(cacheOptions, _cacheKeySelector!).ConfigureAwait(false);
            workers = Enumerable.Range(0, run.Options.WorkerCount)
                .Select(_ => Task.Run(() => SuperviseConsumerAsync(run))).ToArray();
            if (run.Options.LocalCache?.LoadOnStart == true)
            {
                foreach (var item in run.Cache!.ReplayItems())
                {
                    await run.Queue.EnqueueAsync(item, run.ProducerCancellation.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref run.Submitted);
                    Interlocked.Increment(ref run.Restored);
                }
            }
            await ProduceAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.ProducerCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            fatal = ex;
            Volatile.Write(ref run.LastError, ex.ToString());
            lock (_lifecycle) RequestCancellation(run, SchedulerStopMode.Cancel);
        }
        finally { run.Queue.Complete(); }

        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (Exception ex) { fatal ??= ex; Volatile.Write(ref run.LastError, ex.ToString()); }

        // Request cancellation even after natural completion so callback task ownership is fixed.
        Task producerCancel, workerCancel;
        lock (_lifecycle)
        {
            RequestCancellation(run, SchedulerStopMode.Cancel);
            producerCancel = run.ProducerCancelTask;
            workerCancel = run.WorkerCancelTask;
        }
        try { await Task.WhenAll(producerCancel, workerCancel).ConfigureAwait(false); }
        catch (Exception ex) { fatal ??= ex; Volatile.Write(ref run.LastError, ex.ToString()); }

        while (run.Queue.TryDequeue(out _)) Interlocked.Increment(ref run.Abandoned);
        run.StartLimiter?.Dispose();
        if (run.Cache is { } cache)
        {
            // Do not release ownership or allow a new run until already fetched tasks have been saved.
            // StopTimeout still bounds the caller's wait; the UI stays open so a disk issue can be corrected.
            while (true)
            {
                try { await cache.FlushAsync().ConfigureAwait(false); break; }
                catch (Exception ex)
                {
                    Volatile.Write(ref run.LastError, "本地缓存保存失败，正在重试：" + ex.Message);
                    lock (_lifecycle) _state = SchedulerState.Stopping;
                    await Task.Delay(run.Options.LocalCache!.CheckpointInterval).ConfigureAwait(false);
                }
            }
            try { await cache.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { fatal ??= ex; Volatile.Write(ref run.LastError, ex.ToString()); }
        }
        lock (_lifecycle)
        {
            run.ProducerCancellation.Dispose();
            run.WorkerCancellation.Dispose();
            _state = fatal is null ? SchedulerState.Stopped : SchedulerState.Faulted;
            run.Finished.TrySetResult(run.Snapshot(_state));
        }
    }

    private async Task ProduceAsync(SchedulerRun<T> run)
    {
        var token = run.ProducerCancellation.Token;
        var errors = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Avoid fetching another remote batch while the local queue is full.
                if (!await run.Queue.WaitToWriteAsync(token).ConfigureAwait(false)) return;
                if (_producer is ITaskFetchAdmission admission)
                    await admission.WaitToFetchAsync(token).ConfigureAwait(false);
                IReadOnlyList<T> batch;
                var fetchTimedOut = false;
                try
                {
                    using var timeout = CreateTimeout(run.Options.FetchTimeout, token);
                    batch = await _producer.ProduceAsync(timeout?.Token ?? token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("任务来源返回了 null 批次。");
                    fetchTimedOut = timeout?.IsCancellationRequested == true && !token.IsCancellationRequested;
                    errors = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref run.SourceErrors);
                    Volatile.Write(ref run.LastError, ex.ToString());
                    if (++errors >= run.Options.MaxConsecutiveSourceErrors)
                        throw new InvalidOperationException("任务来源连续失败，已停止本轮调度。", ex);
                    var delayMs = Math.Min(run.Options.MaxSourceRetryDelay.TotalMilliseconds,
                        run.Options.SourceRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(errors - 1, 30)));
                    // Bounded jitter reduces synchronized retries across clients.
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs * (0.8 + Random.Shared.NextDouble() * 0.2)), token).ConfigureAwait(false);
                    continue;
                }

                var sourceCount = batch.Count;
                if (run.Cache is { } cache)
                    batch = await cache.StageBatchAsync(batch).ConfigureAwait(false);
                if (fetchTimedOut) throw new TimeoutException("任务拉取超过 FetchTimeout；已返回的任务已保留在启用的本地缓存中。");
                foreach (var item in batch)
                {
                    await run.Queue.EnqueueAsync(item, token).ConfigureAwait(false);
                    Interlocked.Increment(ref run.Submitted);
                }
                if (sourceCount == 0 && run.Options.CompleteWhenSourceEmpty) return;
                var pause = batch.Count == 0 ? run.Options.EmptyPollInterval : run.Options.PollInterval;
                if (pause > TimeSpan.Zero) await Task.Delay(pause, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task SuperviseConsumerAsync(SchedulerRun<T> run)
    {
        try { await ConsumeAsync(run).ConfigureAwait(false); }
        catch
        {
            // Infrastructure/queue failure must also stop the producer, otherwise a full queue can deadlock it.
            lock (_lifecycle) RequestCancellation(run, SchedulerStopMode.Cancel);
            throw;
        }
    }

    private async Task ConsumeAsync(SchedulerRun<T> run)
    {
        var token = run.WorkerCancellation.Token;
        try
        {
            await foreach (var item in run.Queue.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested) { Interlocked.Increment(ref run.Abandoned); return; }
                await ConsumeItemAsync(run, item, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ConsumeItemAsync(SchedulerRun<T> run, T item, CancellationToken token)
    {
        IPreparedTask? prepared = null;
        var claimed = false;
        Interlocked.Increment(ref run.Preparing);
        try
        {
            if (_consumer is IPreparedTaskConsumer<T> preparing)
                prepared = await preparing.PrepareAsync(item, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("消费者准备阶段返回了 null。");
            if (run.StartLimiter is { } limiter)
            {
                Interlocked.Increment(ref run.WaitingForRateLimit);
                try { await limiter.WaitAsync(token).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref run.WaitingForRateLimit); }
            }
            token.ThrowIfCancellationRequested();
            if (run.Cache is { } cache && !await cache.ClaimAsync(cache.GetKey(item)).ConfigureAwait(false))
                return; // A duplicate claim never reaches external code.
            claimed = true;
            Interlocked.Increment(ref run.Claimed);
        }
        finally
        {
            Interlocked.Decrement(ref run.Preparing);
            if (!claimed)
            {
                Interlocked.Increment(ref run.Abandoned);
                if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
            }
        }

        Interlocked.Increment(ref run.Running);
        using var timeout = CreateTimeout(run.Options.ExecutionTimeout, token);
        try
        {
            token.ThrowIfCancellationRequested();
            var success = prepared is null
                ? await _consumer.ConsumeAsync(item, timeout?.Token ?? token).ConfigureAwait(false)
                : await prepared.ExecuteAsync(timeout?.Token ?? token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (timeout?.IsCancellationRequested == true) throw new OperationCanceledException(timeout.Token);
            Interlocked.Increment(ref run.Completed);
            if (!success) Interlocked.Increment(ref run.Failed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Interlocked.Increment(ref run.Canceled); }
        catch (Exception ex)
        {
            Interlocked.Increment(ref run.Completed);
            Interlocked.Increment(ref run.Failed);
            if (timeout?.IsCancellationRequested == true) Interlocked.Increment(ref run.TimedOut);
            Volatile.Write(ref run.LastError, ex.ToString());
        }
        finally
        {
            Interlocked.Decrement(ref run.Running);
            if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static CancellationTokenSource? CreateTimeout(TimeSpan duration, CancellationToken token)
    {
        if (duration == Timeout.InfiniteTimeSpan) return null;
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(duration);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle) _disposeRequested = true;
        await StopAsync().ConfigureAwait(false);
        lock (_lifecycle) _state = SchedulerState.Disposed;
    }
}
