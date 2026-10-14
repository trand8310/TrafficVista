using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Producers;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Business;

namespace TrafficVista.Scheduling;

/// <summary>持续轮询调度入口。接口解析和浏览器业务分别接到生产、消费方法。</summary>
public sealed class TaskSchedulingService : ITaskSchedulerService
{
    private readonly AsyncTaskScheduler<TaskEnvelope> _engine;
    private readonly ILogger _logger;
    private readonly TrafficVistaSettings _settings;
    private readonly HttpTaskSource _source;
    private readonly ITaskConsumer<TaskEnvelope> _consumer;
    private readonly object _sync = new();
    private TrafficVistaOptions _runOptions = new();
    private readonly BusinessMetrics _metrics;

    public TaskSchedulingService(ILogger logger, TrafficVistaSettings settings, HttpTaskSource source,
        ITaskConsumer<TaskEnvelope> consumer, BusinessMetrics? metrics = null)
    {
        _logger = logger.ForContext<TaskSchedulingService>();
        _settings = settings;
        _source = source;
        _consumer = consumer;
        _metrics = metrics ?? new();
        _engine = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token => new(ProduceAsync(token)),
            token => _consumer is BrowserTaskConsumer browser ? browser.WaitForTaskFetchAsync(token) : Task.CompletedTask), _consumer,
            cacheKeySelector: task => task.DeliveryId);
    }

    public bool IsRunning => _engine.IsRunning;
    public SchedulerSnapshot Snapshot => _engine.Snapshot;
    public Task<SchedulerSnapshot> Completion => _engine.Completion;
    public BusinessMetricsSnapshot Performance => _metrics.Snapshot;
    public int ActiveUvSessions => (_consumer as BrowserTaskConsumer)?.ActiveUvCount ?? 0;
    public TaskAdmissionSnapshot? Admission => (_consumer as BrowserTaskConsumer)?.Admission;

    public Task StartAsync(SchedulerOptions options, CancellationToken token = default)
        => StartAsync(_settings.Value, options, token);

    public Task StartAsync(TrafficVistaOptions uiOptions, SchedulerOptions options, CancellationToken token = default)
    {
        uiOptions.Validate();
        options.Validate();
        lock (_sync)
        {
            if (IsRunning) throw new InvalidOperationException("上一轮调度尚未结束。");
            _runOptions = uiOptions with { };
            _source.Configure(_runOptions);
            if (_consumer is IConfigurableTaskConsumer configurable) configurable.Configure(_runOptions);
            _metrics.Reset();
            _logger.Information("启动持续轮询，TaskName={TaskName}，IntervalMs={IntervalMs}，Concurrency={Concurrency}",
                uiOptions.TaskName, uiOptions.TaskFetchInterval, uiOptions.MaxConcurrency);
            return _engine.StartAsync(options with { CompleteWhenSourceEmpty = false }, token);
        }
    }

    private async Task<IReadOnlyList<TaskEnvelope>> ProduceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var batch = await _source.FetchAsync(token).ConfigureAwait(false);
        var expandedBytes = (long)System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(batch)) * _runOptions.Multiple;
        if ((long)batch.Count * _runOptions.Multiple > _settings.Business.MaxExpandedBatchTasks
            || expandedBytes > _settings.Business.MaxExpandedBatchMegabytes * 1024L * 1024)
        {
            // Preserve the fetched payload for operator recovery; never silently discard oversized deliveries.
            var recovery = Path.GetFullPath(_settings.Business.RecoveryRoot, AppContext.BaseDirectory);
            Directory.CreateDirectory(recovery);
            await File.WriteAllTextAsync(Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(new { Multiple = _runOptions.Multiple, Tasks = batch }),
                CancellationToken.None).ConfigureAwait(false);
            throw new InvalidDataException("任务批次倍速展开超过 Business 的任务数量/字节容量上限，原批次已保存至 Business:RecoveryRoot。");
        }
        return RepeatForMultiple(batch, _runOptions.Multiple);
    }

    /// <summary>Multiple＝取一次任务当 Multiple 次任务用：在生产位置复制，每份都是独立消费单元，各自调度、各自获取代理。</summary>
    internal static IReadOnlyList<TaskEnvelope> RepeatForMultiple(IReadOnlyList<TaskEnvelope> batch, int multiple)
    {
        if (multiple < 1) throw new ArgumentOutOfRangeException(nameof(multiple));
        if (multiple == 1 || batch.Count == 0) return batch;
        return new RepeatedBatch(batch, multiple);
    }

    // O(batch size) backing storage even for high Multiple. Each copy has a stable unique delivery key.
    private sealed class RepeatedBatch(IReadOnlyList<TaskEnvelope> batch, int multiple) : IReadOnlyList<TaskEnvelope>
    {
        public int Count { get; } = checked(batch.Count * multiple);
        public TaskEnvelope this[int index] => index < 0 || index >= Count ? throw new ArgumentOutOfRangeException(nameof(index))
            : batch[index % batch.Count] with { DeliveryId = batch[index % batch.Count].DeliveryId + ":" + index / batch.Count };
        public IEnumerator<TaskEnvelope> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public Task StopAsync(SchedulerStopMode mode = SchedulerStopMode.Cancel, CancellationToken token = default)
        => _engine.StopAsync(mode, token);
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
