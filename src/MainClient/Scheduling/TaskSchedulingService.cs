using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Producers;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Business;
using TrafficVista.Caching;

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
    private readonly BrowserLaunchLimiter? _launches;
    private readonly SharedStaticResourceCache? _resourceCache;
    private readonly BrowserTaskIntakeGate? _intake;
    private readonly BusinessPlaywrightDrivers? _drivers;
    private Persistence.LocalTaskCacheOptions? _runCache;

    public TaskSchedulingService(ILogger logger, TrafficVistaSettings settings, HttpTaskSource source,
        ITaskConsumer<TaskEnvelope> consumer, BusinessMetrics? metrics = null, BrowserLaunchLimiter? launches = null,
        SharedStaticResourceCache? resourceCache = null, BusinessPlaywrightDrivers? drivers = null)
    {
        _logger = logger.ForContext<TaskSchedulingService>();
        _settings = settings;
        _source = source;
        _consumer = consumer;
        _metrics = metrics ?? new();
        _launches = launches;
        _resourceCache = resourceCache;
        _drivers = drivers;
        if (launches is not null)
            _intake = new(launches, settings.Business.TaskIntakePauseBrowserQueue,
                settings.Business.TaskIntakeResumeBrowserQueue, _logger);
        if (consumer is BrowserTaskConsumer browserConsumer) browserConsumer.IntakeGate = _intake;
        _intake?.SetEnabled(settings.Value.TaskIntakeBackpressureEnabled);
        _engine = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token => new(ProduceAsync(token))), _consumer,
            queueFactory: options => new Queues.BoundedTaskQueue<TaskEnvelope>(options.QueueCapacity, options.WorkerCount,
                _intake is null ? null : _intake.WaitAsync,
                _intake is not null && consumer is BrowserTaskConsumer browser
                    ? task => _intake.TryReserve(task.DeliveryId, browser.EstimatePendingUvs(task)) : null),
            cacheKeySelector: task => task.DeliveryId, logger: _logger);
    }

    public bool IsRunning => _engine.IsRunning;
    public SchedulerSnapshot Snapshot => _engine.Snapshot;
    public Task<SchedulerSnapshot> Completion => _engine.Completion;
    public BusinessMetricsSnapshot Performance => _metrics.Snapshot;
    public int ActiveUvSessions => (_consumer as BrowserTaskConsumer)?.ActiveUvCount ?? 0;
    public TaskDispatchSnapshot? Dispatch => (_consumer as BrowserTaskConsumer)?.Dispatch;
    public TaskFetchSnapshot TaskFetch => _source.Snapshot;
    public CacheStatistics? ResourceCache => _resourceCache?.Statistics;
    public TaskIntakeSnapshot? TaskIntake => _intake?.Snapshot;
    public BrowserCloseSnapshot BrowserClosing => BrowserCloseCoordinator.Shared.Snapshot;
    public PlaywrightDriversSnapshot? PlaywrightDrivers => _drivers?.Snapshot;
    public int? EffectiveBrowserStarts => _launches?.EffectiveStartLimit;

    public void UpdateTaskIntakeBackpressure(bool enabled)
    {
        lock (_sync)
        {
            _runOptions = _runOptions with { TaskIntakeBackpressureEnabled = enabled };
            _intake?.SetEnabled(enabled);
            _logger.Information("任务领取背压：{Enabled}，关闭时不因启动需求暂停领取", enabled ? "开启" : "关闭");
        }
    }

    public void UpdateHiddenMode(bool hidden)
    {
        lock (_sync)
        {
            _runOptions = _runOptions with { IsHiddenMode = hidden };
            if (_consumer is BrowserTaskConsumer browser) browser.UpdateHiddenMode(hidden);
        }
    }

    public Task StartAsync(SchedulerOptions options, CancellationToken token = default)
        => StartAsync(_settings.Value, options, token);

    public Task StartAsync(TrafficVistaOptions uiOptions, SchedulerOptions options, CancellationToken token = default)
    {
        uiOptions.Validate();
        options.Validate();
        lock (_sync)
        {
            if (IsRunning) throw new InvalidOperationException("上一轮调度尚未结束。");
            _launches?.Configure(_settings.MaxConcurrentBrowserStarts);
            _drivers?.Configure(uiOptions.PlaywrightDriverCount, options.WorkerCount);
            _runOptions = uiOptions with { };
            _runCache = options.LocalCache;
            _intake?.SetEnabled(uiOptions.TaskIntakeBackpressureEnabled);
            _source.Configure(_runOptions);
            if (_consumer is IConfigurableTaskConsumer configurable) configurable.Configure(_runOptions);
            _metrics.Reset();
            _logger.Information("启动持续轮询，TaskName={TaskName}，IntervalMs={IntervalMs}，Workers={Workers}，BrowserStarts={BrowserStarts}，UvIntervalMs={UvIntervalMs}",
                uiOptions.TaskName, uiOptions.TaskFetchInterval, options.WorkerCount, _settings.MaxConcurrentBrowserStarts, uiOptions.UvExecutionInterval);
            _logger.Information("任务队列恢复模式={RecoveryMode}，运行数据导出={ExportRunData}，共享磁盘缓存={DiskCache}",
                options.LocalCache is null ? "丢弃（仅内存）" : "保存（恢复未开始任务）",
                _settings.Business.ExportRunData, uiOptions.IsCacheMode);
            _logger.Information("资源缓存模式：Enabled={Enabled}，JavaScriptMode={JavaScriptMode}", uiOptions.IsCacheMode,
                uiOptions.JavaScriptCacheMode);
            _logger.Information("Playwright驱动配置，Configured={Configured}，Effective={Effective}，Mode={Mode}，按工作者固定映射并按需创建",
                uiOptions.PlaywrightDriverCount, _drivers?.Snapshot.Effective ?? 1,
                uiOptions.PlaywrightDriverCount == 0 ? "跟随任务并发" : "指定数量");
            return StartRunAsync(options, token);
        }
    }

    private async Task StartRunAsync(SchedulerOptions options, CancellationToken token)
    {
        try
        {
            await _engine.StartAsync(options with { CompleteWhenSourceEmpty = false }, token).ConfigureAwait(false);
            var completion = _engine.Completion;
            _ = completion.ContinueWith(_ =>
            {
                lock (_sync)
                    if (ReferenceEquals(completion, _engine.Completion)) _metrics.StopRuntime();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { _metrics.StopRuntime(); throw; }
    }

    private async Task<IReadOnlyList<TaskEnvelope>> ProduceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<TaskEnvelope> batch;
        using (_metrics.Track("任务接口")) batch = await _source.FetchAsync(token).ConfigureAwait(false);
        var expandedBytes = (long)System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(batch)) * _runOptions.Multiple;
        if ((long)batch.Count * _runOptions.Multiple > _settings.Business.MaxExpandedBatchTasks
            || expandedBytes > _settings.Business.MaxExpandedBatchMegabytes * 1024L * 1024)
        {
            var save = _settings.Business.ExportRunData || _settings.Business.TaskRecoveryMode == Business.TaskRecoveryMode.Save;
            if (save)
            {
                var recovery = Path.GetFullPath(_settings.Business.RecoveryRoot, AppContext.BaseDirectory);
                Directory.CreateDirectory(recovery);
                await File.WriteAllTextAsync(Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { Multiple = _runOptions.Multiple, Tasks = batch }),
                    CancellationToken.None).ConfigureAwait(false);
            }
            throw new InvalidDataException("任务批次倍速展开超过容量上限，" + (save ? "原批次已保存供检查。" : "按丢弃模式处理。"));
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

    public async Task StopAsync(SchedulerStopMode mode = SchedulerStopMode.Cancel, CancellationToken token = default)
    {
        await _engine.StopAsync(mode, token).ConfigureAwait(false);
        if (!await BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
            _logger.Warning("业务浏览器已停止，Playwright收尾尚未全部完成，Deferred={Deferred}", BrowserClosing.Deferred);
        else if (_drivers is not null) await _drivers.ReleaseIdleAsync().ConfigureAwait(false);
    }

    internal async Task<string> PrepareRestartAsync()
    {
        _logger.Information("定时重启：暂停任务拉取及领取，暂存未执行任务，等待已领取任务完成");
        try { await _engine.StopAsync(SchedulerStopMode.Suspend).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _logger.Information("定时重启：已领取任务仍在运行，继续等待，不取消UV");
        }
        var completed = await _engine.Completion.ConfigureAwait(false);
        if (completed.State == SchedulerState.Faulted || completed.CacheError is not null)
            throw new InvalidOperationException("定时重启准备失败：" + (completed.CacheError ?? completed.LastError));
        while (!await BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            _logger.Information("定时重启：等待Playwright关闭收尾，InFlight={InFlight}", BrowserClosing.InFlight);
        if (_drivers is not null) await _drivers.ReleaseIdleAsync().ConfigureAwait(false);
        var resumeToken = Persistence.RestartQueueFiles.Publish(_runCache
            ?? throw new InvalidOperationException("当前运行未启用定时重启暂存。"));
        _logger.Information("定时重启：已领取任务全部结束，待恢复任务{Pending}项，浏览器及驱动清理完成", completed.Cached);
        return resumeToken;
    }
    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync().ConfigureAwait(false);
        if (!await BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            _logger.Warning("释放调度服务时仍有Playwright关闭任务，Deferred={Deferred}", BrowserClosing.Deferred);
        else if (_drivers is not null) await _drivers.ReleaseIdleAsync().ConfigureAwait(false);
        _intake?.Dispose();
    }
}
