using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Business;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Serialization;

namespace TrafficVista.Scheduling.Consumers;

/// <summary>
/// 真实业务消费者：一项任务拆成 uv 个独立 UV 会话；同一 UV 内按 pv 重复访问；sleep 为停留秒数范围；
/// jstext 解析为 JObject 传给执行器。代理 IP 每个任务无条件重新申请一次，本任务全部 UV 共享，不与其他任务共享；
/// IpTtl 从代理领取时刻计时，并与供应商真实截止时刻取较早者；到期取消所有在途 UV，任务按失败结束
/// （IpTtl=0 只关闭界面时长限制，供应商有效期仍生效；非代理模式无 IP 限制）。设备按 UV 逐个发放。Multiple 的“取一次当多次用”在生产位置复制，消费者不感知轮次。
/// 任务 ID 不唯一标识一项任务，同一 ID 可被不同调度器调度：每次消费写入 <任务ID>/<运行标识>/ 的独立运行目录，
/// 按一次性消费处理，不读取也不复用其他消费的任何状态。
/// 全部 UV 会话清理完成后才结束消费；至少一个 UV 成功即任务成功，调度器不自动重试。
/// 代理获取失败（含供应商侧重试次数用尽）：报错保留日志，任务直接丢弃返回 false，调度器继续接下一个任务。
/// </summary>
public sealed class BrowserTaskConsumer : IPreparedTaskConsumer<TaskEnvelope>, IConfigurableTaskConsumer, IDisposable
{
    private readonly IBusinessDeviceProvider _devices;
    private readonly IBusinessProxyProvider _proxies;
    private readonly IBusinessUvRunner _runner;
    private readonly BusinessOptions _business;
    private readonly ILogger _logger;
    private readonly string _root;
    private int _pendingUvs;
    private readonly BusinessMetrics _metrics;
    private readonly SessionRetention _retention;
    private TrafficVistaOptions _options = new();
    private int _hiddenMode;
    private int _activeTasks;
    private int _preparedTasks;
    private int _activeUvs;
    private int _peak;
    public int ActiveUvCount => Volatile.Read(ref _activeUvs);
    public int PeakUvCount => Volatile.Read(ref _peak);
    public TaskDispatchSnapshot Dispatch => new(Volatile.Read(ref _pendingUvs), Volatile.Read(ref _activeTasks));
    internal BrowserTaskIntakeGate? IntakeGate { get; set; }
    internal int EstimatePendingUvs(TaskEnvelope task)
    {
        // Bad plans still reach the normal task validator and retain its failure classification.
        try { return ParsePlan(task).UvCount; }
        catch (InvalidDataException) { return 0; }
    }

    public BrowserTaskConsumer(IBusinessDeviceProvider devices, IBusinessProxyProvider proxies,
        IBusinessUvRunner runner, BusinessOptions business, ILogger logger, BusinessMetrics? metrics = null)
    {
        business.Validate();
        _devices = devices; _proxies = proxies; _runner = runner; _business = business;
        _logger = logger.ForContext<BrowserTaskConsumer>();
        _metrics = metrics ?? new BusinessMetrics();
        _retention = new SessionRetention(business, _logger);
        _root = business.ExportRunData ? Path.GetFullPath(business.SessionRoot, AppContext.BaseDirectory)
            : Path.Combine(Path.GetTempPath(), "TrafficVista", "uv");
    }

    public void Configure(TrafficVistaOptions options)
    {
        if (Volatile.Read(ref _activeTasks) != 0 || Volatile.Read(ref _preparedTasks) != 0)
            throw new InvalidOperationException("消费者仍有任务未退出。");
        options.Validate();
        _options = options with { };
        UpdateHiddenMode(options.IsHiddenMode);
        _peak = 0;
    }

    internal void UpdateHiddenMode(bool hidden) => Volatile.Write(ref _hiddenMode, hidden ? 1 : 0);

    public async ValueTask<bool> ConsumeAsync(TaskEnvelope task, CancellationToken token)
    {
        await using var prepared = await PrepareAsync(task, token).ConfigureAwait(false);
        return await prepared.ExecuteAsync(token).ConfigureAwait(false);
    }

    public ValueTask<IPreparedTask> PrepareAsync(TaskEnvelope task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _preparedTasks);
        return ValueTask.FromResult<IPreparedTask>(new PreparedConsumption(this, task));
    }

    private sealed class PreparedConsumption(BrowserTaskConsumer owner, TaskEnvelope task) : IPreparedTask
    {
        private readonly object _sync = new();
        private Task<bool>? _execution;
        private bool _disposed;
        public ValueTask<bool> ExecuteAsync(CancellationToken token)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_execution is not null) throw new InvalidOperationException("任务只能执行一次。");
                _execution = owner.ExecuteTaskAsync(task, token).AsTask();
                return new(_execution);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Task<bool>? execution;
            lock (_sync) { if (_disposed) return; _disposed = true; execution = _execution; }
            try
            {
                if (execution is not null)
                    try { await execution.ConfigureAwait(false); } catch { /* ExecuteAsync owns the outcome. */ }
            }
            finally { Interlocked.Decrement(ref owner._preparedTasks); }
        }
    }

    private async ValueTask<bool> ExecuteTaskAsync(TaskEnvelope task, CancellationToken token)
    {
        using var worker = WorkerExecutionContext.Enter(task.WorkerId);
        token.ThrowIfCancellationRequested();
        _logger.Information("业务任务开始，TaskId={TaskId}，DeliveryId={DeliveryId}", task.Id, task.DeliveryId);
        if (task.Id <= 0) throw new InvalidDataException("任务 ID 必须为正整数。");
        if (!Uri.TryCreate(task.Url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("任务 url 必须是完整 HTTP/HTTPS 地址。");
        (int uvCount, int pageViews, double minimumDwell, double maximumDwell) plan;
        JObject? jstext;
        try { plan = ParsePlan(task); jstext = ParseJstext(task); }
        catch (InvalidDataException ex)
        {
            if (_business.ExportRunData || _business.TaskRecoveryMode == TaskRecoveryMode.Save)
            {
                var recovery = Path.GetFullPath(_business.RecoveryRoot, AppContext.BaseDirectory);
                Directory.CreateDirectory(recovery);
                await File.WriteAllTextAsync(Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".task.json"),
                    JsonConvert.SerializeObject(new { Task = task, Error = ex.Message }), CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        var (uvCount, pageViews, minimumDwell, maximumDwell) = plan;
        // 每项任务开始执行时取最新模式；任务内全部UV使用同一份快照。
        var options = _options with { IsHiddenMode = Volatile.Read(ref _hiddenMode) != 0 };
        Interlocked.Increment(ref _activeTasks);
        try
        {
            if (_business.ExportRunData) await _retention.SweepAsync().ConfigureAwait(false);
            // 同一任务 ID 可能同时被多个调度器/多轮复制消费：每次消费独立运行目录，互不影响、互不复用。
            var runDirectory = Path.Combine(_root, task.Id.ToString(CultureInfo.InvariantCulture),
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
            if (_business.ExportRunData) Directory.CreateDirectory(runDirectory);
            using var runLease = _business.ExportRunData ? new FileStream(Path.Combine(runDirectory, "run.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None) : null;
            var started = Stopwatch.GetTimestamp();
            var succeeded = 0;
            var finished = 0;
            var dispatched = 0;
            _metrics.PlanUvs(uvCount);
            Interlocked.Add(ref _pendingUvs, uvCount);
            string? failure = null;
            var pending = new List<Task<bool>>();
            BusinessProxy? proxy = null;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var dispatchGuard = new UvDispatchGuard(lifetime.Token);
            try
            {
                if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(runDirectory, "task.json"),
                    BuildTaskSnapshot(task, uvCount, pageViews, null, jstext), token).ConfigureAwait(false);
                // 代理属于任务：每个任务无条件重新申请一次，全部 UV 共享这一个出口；其他任务各自独立申请。
                // 拿不到可用代理（含供应商侧重试次数用尽）：报错保留日志，直接丢弃本任务返回 false，
                // 调度器记为失败后继续接下一个任务，不重试、不派发任何 UV。调用方取消则原样冒泡。
                try
                {
                    var proxyStarted = Stopwatch.GetTimestamp();
                    using (_metrics.Track("代理/出口信息"))
                        proxy = await _proxies.AcquireAsync(task, options, token).ConfigureAwait(false);
                    _metrics.Stage("proxy", Stopwatch.GetElapsedTime(proxyStarted));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { throw; }
                catch (Exception ex)
                {
                    _metrics.Failure("代理/出口信息");
                    _logger.Warning(ex, "代理/IP信息获取失败（含重试次数用尽），TaskId={TaskId} 已丢弃，接下一个任务。原因={Reason}", task.Id, ex.Message);
                    failure = "proxy: " + ex.Message;
                    return false;
                }
                if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(runDirectory, "task.json"),
                    BuildTaskSnapshot(task, uvCount, pageViews, proxy, jstext), token).ConfigureAwait(false);
                // IpTtl 是代理 IP 在本任务全部 UV 执行期间的有效期：超时即取消在途会话并清理该任务占用的资源。
                if (proxy?.Deadline(options.IpTtl) is { } deadline)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) lifetime.Cancel();
                    else lifetime.CancelAfter(remaining);
                }
                var runToken = lifetime.Token;
                Exception? dispatchError = null;
                long lastDispatch = 0;
                try
                {
                    for (var index = 0; index < uvCount; index++)
                    {
                        dispatchGuard.Token.ThrowIfCancellationRequested();
                        // 仅按本任务的投递间隔等待，不等待之前的UV完成。
                        var rateStarted = Stopwatch.GetTimestamp();
                        if (lastDispatch != 0)
                        {
                            var remaining = TimeSpan.FromMilliseconds(options.UvExecutionInterval)
                                - Stopwatch.GetElapsedTime(lastDispatch);
                            if (remaining > TimeSpan.Zero)
                            {
                                using (_metrics.Track("UV间隔"))
                                    await Task.Delay(remaining, dispatchGuard.Token).ConfigureAwait(false);
                            }
                        }
                        _metrics.Stage("uvRateLimit", Stopwatch.GetElapsedTime(rateStarted));
                        // 只保留在途任务，已完成结果立即计入摘要，不随 uvCount 增长。
                        for (var p = pending.Count - 1; p >= 0; p--)
                            if (pending[p].IsCompleted)
                            {
                                if (await pending[p].ConfigureAwait(false)) succeeded++;
                                finished++;
                                pending.RemoveAt(p);
                            }
                        // 每个 UV 一台独立设备和独立浏览器会话；不复用其他 UV 的 Context 或 StorageState。
                        lastDispatch = Stopwatch.GetTimestamp();
                        if (!dispatchGuard.TryStart(() => RunUvAsync(task.Id, task.WorkerId, index + 1, target, runDirectory, proxy, options,
                            TimeSpan.FromSeconds(minimumDwell), TimeSpan.FromSeconds(maximumDwell),
                            pageViews, jstext, dispatchGuard, runToken, task.DeliveryId), out var uv)) break;
                        pending.Add(uv!);
                        dispatched++;
                        Interlocked.Decrement(ref _pendingUvs);
                    }
                }
                catch (Exception ex) { dispatchError = ex; }
                // 即使停止调度、IP 到期或派发出错，也必须等待已派发 UV 的浏览器关闭和资源释放。
                var results = await Task.WhenAll(pending).ConfigureAwait(false);
                succeeded += results.Count(result => result);
                finished += results.Length;
                pending.Clear();
                token.ThrowIfCancellationRequested();
                if (runToken.IsCancellationRequested)
                {
                    // 代理 IP 到期：停止剩余UV；此前已有成功UV则任务仍成功。
                    _logger.Warning("代理 IP 已达到有效截止时刻，TaskId={TaskId} 全部在途会话已清理，Succeeded={Succeeded}/{UvCount}。",
                        task.Id, succeeded, uvCount);
                    failure = "proxy-expired";
                    return succeeded > 0;
                }
                if (dispatchGuard.Stopped)
                {
                    failure = dispatchGuard.Reason;
                    _logger.Warning("任务已停止后续UV投递，已投递UV均已结束，TaskId={TaskId}，Reason={Reason}，Dispatched={Dispatched}/{UvCount}，Succeeded={Succeeded}",
                        task.Id, failure, dispatched, uvCount, succeeded);
                    return succeeded > 0;
                }
                if (dispatchError is not null)
                {
                    if (succeeded == 0) throw new InvalidOperationException("UV 派发失败，已等待在途会话清理。", dispatchError);
                    failure = dispatchError.Message;
                    _logger.Warning(dispatchError, "UV派发中断但已有成功UV，任务计成功，TaskId={TaskId}，Succeeded={Succeeded}", task.Id, succeeded);
                }
                var success = succeeded > 0;
                _logger.Information("业务任务完成，TaskId={TaskId}，Run={Run}，UvCount={UvCount}，Succeeded={Succeeded}",
                    task.Id, Path.GetFileName(runDirectory), uvCount, succeeded);
                return success;
            }
            catch (Exception ex) { failure = token.IsCancellationRequested ? "canceled" : ex.Message; throw; }
            finally
            {
                _metrics.Undispatched(uvCount - dispatched);
                Interlocked.Add(ref _pendingUvs, -(uvCount - dispatched));
                // Snapshot/result remain reviewable for proxy failures and cancellation as well as partial success.
                var remainingResults = await Task.WhenAll(pending).ConfigureAwait(false);
                succeeded += remainingResults.Count(result => result);
                finished += remainingResults.Length;
                if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(runDirectory, "summary.json"), new JObject
                {
                    ["taskId"] = task.Id, ["deliveryId"] = task.DeliveryId, ["workerId"] = task.WorkerId, ["plannedUvs"] = uvCount,
                    ["dispatchedUvs"] = dispatched, ["succeededUvs"] = succeeded,
                    ["unsuccessfulUvs"] = finished - succeeded, ["undispatchedUvs"] = uvCount - dispatched,
                    ["success"] = succeeded > 0 && !token.IsCancellationRequested,
                    ["failure"] = token.IsCancellationRequested ? "canceled" : succeeded > 0 ? null : failure ?? "uv-failed",
                    ["stopReason"] = failure,
                    ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ["finishedAt"] = DateTimeOffset.UtcNow
                }.ToString(), CancellationToken.None).ConfigureAwait(false);
                if (!_business.ExportRunData) RemoveTemporaryRun(runDirectory);
            }
        }
        finally { Interlocked.Decrement(ref _activeTasks); }
    }

    private async Task<bool> RunUvAsync(long taskId, int workerId, int uvIndex, Uri target, string runDirectory, BusinessProxy? proxy,
        TrafficVistaOptions options, TimeSpan dwell, TimeSpan maximumDwell, int pageViews, JObject? jstext,
        UvDispatchGuard dispatchGuard, CancellationToken token, string deliveryId)
    {
        using var demand = IntakeGate?.BeginUv(deliveryId);
        var active = Interlocked.Increment(ref _activeUvs);
        _metrics.DispatchUv();
        int observed;
        do { observed = _peak; if (active <= observed) break; }
        while (Interlocked.CompareExchange(ref _peak, active, observed) != observed);
        var started = Stopwatch.GetTimestamp();
        var success = false;
        var directory = Path.Combine(runDirectory, uvIndex.ToString(CultureInfo.InvariantCulture));
        var phase = "device";
        string? uvError = null;
        Exception? outcomeError = null;
        try
        {
            if (_business.ExportRunData) Directory.CreateDirectory(directory);
            var deviceStarted = Stopwatch.GetTimestamp();
            BusinessDeviceProfile device;
            using (_metrics.Track("设备接口"))
                device = await _devices.AcquireAsync(options, taskId, uvIndex, token).ConfigureAwait(false);
            _metrics.Stage("device", Stopwatch.GetElapsedTime(deviceStarted));
            var run = new BusinessUvRun(taskId, uvIndex, target, directory, device, proxy, options, dwell, pageViews, jstext)
                { WorkerId = workerId, MaximumDwell = maximumDwell, BrowserStartQueued = demand is null ? null : demand.Dispose };
            if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(directory, "device.json"),
                JToken.FromObject(device).ToString(), token).ConfigureAwait(false);
            phase = "execution";
            success = await _runner.RunAsync(run, token).ConfigureAwait(false);
            if (!success) _metrics.Failure("执行返回未成功");
            return success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { uvError = "canceled"; _logger.Information("业务 UV 已取消，TaskId={TaskId}，UvIndex={UvIndex}", taskId, uvIndex); return false; }
        catch (Exception ex)
        {
            outcomeError = ex; uvError = ex.Message;
            if (phase == "device") _metrics.Failure("设备接口");
            if (proxy?.HasProxy == true && ProxyUnavailableException.FindCode(ex) is { } proxyCode)
                _logger.Warning(ex, "业务 UV 代理不可用，TaskId={TaskId}，UvIndex={UvIndex}，Code={Code}", taskId, uvIndex, proxyCode);
            else _logger.Error(ex, "业务 UV 失败，TaskId={TaskId}，UvIndex={UvIndex}", taskId, uvIndex);
            return false;
        }
        finally
        {
            if (_business.ExportRunData && !success && Directory.Exists(directory) && !File.Exists(Path.Combine(directory, "result.json")))
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), new JObject
                    {
                        ["taskId"] = taskId, ["uvId"] = uvIndex, ["workerId"] = workerId, ["success"] = false,
                        ["canceled"] = token.IsCancellationRequested, ["failureStage"] = phase,
                        ["errors"] = new JArray(uvError ?? "runner-returned-false")
                    }.ToString(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.Warning(ex, "UV 失败摘要未写入，TaskId={TaskId}，UvIndex={UvIndex}", taskId, uvIndex); }
            }
            if (dispatchGuard.Record(success, outcomeError, token.IsCancellationRequested, proxy?.HasProxy == true))
                _logger.Warning("停止该任务后续UV投递，已投递UV继续完成，TaskId={TaskId}，UvIndex={UvIndex}，Reason={Reason}", taskId, uvIndex, dispatchGuard.Reason);
            _metrics.UvFinished(success, token.IsCancellationRequested, Stopwatch.GetElapsedTime(started));
            Interlocked.Decrement(ref _activeUvs);
        }
    }

    private void RemoveTemporaryRun(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TrafficVista", "uv")).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("临时会话目录不在本应用临时根目录内。");
        try
        {
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.Warning("临时浏览器目录暂未清理，Path={Path}，Reason={Reason}", target, ex.Message); }
    }

    private (int UvCount, int PageViews, double MinimumDwell, double MaximumDwell) ParsePlan(TaskEnvelope task)
    {
        if (task.Payload is not { } payload || !JsonData.TryGetInt32(payload["uv"], out var count)
            || count < 1 || count > _business.MaxUvPerTask)
            throw new InvalidDataException($"任务 uv 必须是 1～{_business.MaxUvPerTask} 的整数。");
        var pageViews = 1;
        if (payload.TryGetValue("pv", out var pv) && pv.Type is not (JTokenType.Null or JTokenType.Undefined))
        {
            if (!JsonData.TryGetInt32(pv, out pageViews) || pageViews < 1 || pageViews > _business.MaxPageViewsPerUv)
                throw new InvalidDataException($"任务 pv 必须是 1～{_business.MaxPageViewsPerUv} 的整数。");
        }
        double min = 0, max = 0;
        if (payload.TryGetValue("sleep", out var sleep))
        {
            var text = sleep.Type switch
            {
                JTokenType.String => sleep.Value<string>(),
                JTokenType.Integer or JTokenType.Float => sleep.ToString(Formatting.None),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parts = text!.Split('-');
                if (parts.Length == 1) parts = [parts[0], parts[0]];
                if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                    || !double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max < min || max > _business.MaxDwellSeconds)
                    throw new InvalidDataException($"sleep 必须是有效秒数范围，例如 5-5，上限 {_business.MaxDwellSeconds} 秒。");
            }
        }
        return (count, pageViews, min, max);
    }

    /// <summary>jstext 可缺省；有值时接收对象或对象字符串，解析后原样透传。</summary>
    private static JObject? ParseJstext(TaskEnvelope task) =>
        JsonData.ParseOptionalObject(task.Payload?["jstext"], "任务 jstext");

    internal static string BuildTaskSnapshot(TaskEnvelope task, int uvCount, int pageViews, BusinessProxy? proxy,
        JObject? jstext) =>
        new JObject
        {
            ["taskId"] = task.Id, ["deliveryId"] = task.DeliveryId, ["workerId"] = task.WorkerId, ["name"] = task.Name, ["url"] = task.Url,
            ["uv"] = uvCount, ["pv"] = pageViews,
            ["proxyServer"] = proxy?.Server,
            ["proxyProtocol"] = proxy?.Protocol, ["proxyAcquiredAt"] = proxy?.AcquiredAt,
            ["proxyRealIp"] = proxy?.RealIp, ["proxyRealIpSource"] = proxy?.RealIpSource,
            ["proxyExpiresAt"] = proxy?.ExpiresAt,
            // 经代理查到的真实出口信息完整 JSON（Net 模块校验通过的那份），随任务快照留档。
            ["ipinfo"] = proxy?.IpInfo?.DeepClone(),
            // 与执行器使用同一份解析结果；不再次解析可能为空的原始字段。
            ["jstext"] = jstext?.DeepClone(),
            ["payload"] = task.Payload?.DeepClone()
        }.ToString();

    public void Dispose()
    {
        if (Volatile.Read(ref _activeTasks) != 0 || Volatile.Read(ref _preparedTasks) != 0)
            throw new InvalidOperationException("消费者仍有任务未退出。");
    }
}
