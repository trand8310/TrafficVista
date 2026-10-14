using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.LocalTesting;
using TrafficVista.Serialization;

namespace TrafficVista.Scheduling.Consumers;

/// <summary>一项任务拆成多个本地 UV 测试会话，全部返回后才结束消费。</summary>
public sealed class LocalUvTaskConsumer : ITaskConsumer<TaskEnvelope>, IConfigurableTaskConsumer, IDisposable
{
    private readonly IUvDeviceProvider _devices;
    private readonly ILocalTaskProxyProvider _proxies;
    private readonly IUvBrowserRunner _runner;
    private readonly LocalTestingOptions _local;
    private readonly ILogger _logger;
    private readonly string _root;
    private SemaphoreSlim _slots = new(1, 1);
    private TrafficVistaOptions _options = new();
    private int _activeTasks;
    private int _activeUvs;
    private int _peak;
    public int ActiveUvCount => Volatile.Read(ref _activeUvs);
    public int PeakUvCount => Volatile.Read(ref _peak);

    public LocalUvTaskConsumer(IUvDeviceProvider devices, ILocalTaskProxyProvider proxies, IUvBrowserRunner runner,
        LocalTestingOptions local, ILogger logger)
    {
        local.Validate();
        _devices = devices; _proxies = proxies; _runner = runner; _local = local;
        _logger = logger.ForContext<LocalUvTaskConsumer>();
        _root = Path.GetFullPath(local.SessionRoot, AppContext.BaseDirectory);
    }

    public void Configure(TrafficVistaOptions options)
    {
        if (Volatile.Read(ref _activeTasks) != 0) throw new InvalidOperationException("消费者仍有任务未退出。");
        options.Validate();
        _slots.Dispose();
        _slots = new(options.MaxConcurrency, options.MaxConcurrency);
        _options = options with { };
        _peak = 0;
    }

    public async ValueTask<bool> ConsumeAsync(TaskEnvelope task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = LocalEndpointPolicy.RequireLoopback(task.Url, "测试页面");
        if (task.Id <= 0) throw new InvalidDataException("任务 ID 必须为正整数。");
        var (uvCount, minimumDwell, maximumDwell) = ParsePlan(task);
        var options = _options;
        var taskDirectory = Path.Combine(_root, task.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(taskDirectory);
        // 同任务跨进程排他；锁一直持有到全部 UV 清理完成。
        using var taskLock = new FileStream(Path.Combine(taskDirectory, "task.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var previous = Directory.EnumerateDirectories(taskDirectory).Select(Path.GetFileName)
            .Select(name => long.TryParse(name, out var value) && value > 0 ? value : 0).DefaultIfEmpty().Max();
        var firstUv = checked(previous + 1);
        _ = checked(previous + uvCount);
        var pending = new List<Task<bool>>(uvCount);
        Exception? dispatchError = null;
        Interlocked.Increment(ref _activeTasks);
        try
        {
            // 只获取一次，全任务共享同一不可变代理描述。
            var proxy = await _proxies.AcquireAsync(options, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(taskDirectory, "task.json"), JsonConvert.SerializeObject(new
            {
                task.Id, task.Name, task.Url, UvCount = uvCount, FirstUvId = firstUv, ProxyServer = proxy?.Server,
                Payload = task.Payload
            }, Formatting.Indented), token).ConfigureAwait(false);
            long lastStart = 0;
            try
            {
                for (var index = 0; index < uvCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (lastStart != 0)
                    {
                        var remaining = TimeSpan.FromMilliseconds(options.UvExecutionInterval) - Stopwatch.GetElapsedTime(lastStart);
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                    }
                    await _slots.WaitAsync(token).ConfigureAwait(false);
                    var uvId = checked(firstUv + index);
                    lastStart = Stopwatch.GetTimestamp();
                    pending.Add(RunUvAsync(task.Id, uvId, target, taskDirectory, proxy, options,
                        TimeSpan.FromSeconds(minimumDwell + Random.Shared.NextDouble() * (maximumDwell - minimumDwell)), token));
                }
            }
            catch (Exception ex) { dispatchError = ex; }
            // 即使停止调度/派发出错，也必须等待已派发 UV 的浏览器关闭和资源释放。
            var results = await Task.WhenAll(pending).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (dispatchError is not null) throw new InvalidOperationException("UV 派发失败，已等待在途会话清理。", dispatchError);
            var success = results.Length == uvCount && results.All(result => result);
            _logger.Information("本地任务完成，TaskId={TaskId}，UvCount={UvCount}，Succeeded={Succeeded}",
                task.Id, uvCount, results.Count(result => result));
            return success;
        }
        finally { Interlocked.Decrement(ref _activeTasks); }
    }

    private async Task<bool> RunUvAsync(long taskId, long uvId, Uri target, string parent, LocalTaskProxy? proxy,
        TrafficVistaOptions options, TimeSpan dwell, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _activeUvs);
        int observed;
        do { observed = _peak; if (active <= observed) break; }
        while (Interlocked.CompareExchange(ref _peak, active, observed) != observed);
        try
        {
            var directory = Path.Combine(parent, uvId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var device = await _devices.AcquireAsync(taskId, uvId, token).ConfigureAwait(false);
            // 标准模拟设备独立对象；不复用上一个 UV 的 Context 或 StorageState。
            var run = new UvBrowserRun(taskId, uvId, target, directory, device, proxy, options, dwell);
            await File.WriteAllTextAsync(Path.Combine(directory, "device.json"), JsonConvert.SerializeObject(device), token).ConfigureAwait(false);
            return await _runner.RunAsync(run, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _logger.Information("本地 UV 已取消，TaskId={TaskId}，UvId={UvId}", taskId, uvId); return false; }
        catch (Exception ex)
        { _logger.Error(ex, "本地 UV 失败，TaskId={TaskId}，UvId={UvId}", taskId, uvId); return false; }
        finally { Interlocked.Decrement(ref _activeUvs); _slots.Release(); }
    }

    private (int Count, double MinimumDwell, double MaximumDwell) ParsePlan(TaskEnvelope task)
    {
        if (task.Payload is not { } payload || !JsonData.TryGetInt32(payload["uv"], out var count)
            || count < 1 || count > _local.MaxUvPerTask)
            throw new InvalidDataException($"任务 uv 必须是 1～{_local.MaxUvPerTask} 的整数。");
        double min = 0, max = 0;
        if (payload.TryGetValue("sleep", out var sleep) && !string.IsNullOrWhiteSpace(sleep.Value<string>()))
        {
            var parts = sleep.Value<string>()!.Split('-');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                || !double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max < min || max > _local.MaxDwellSeconds)
                throw new InvalidDataException($"sleep 必须是有效秒数范围，例如 5-5，上限 {_local.MaxDwellSeconds} 秒。");
        }
        return (count, min, max);
    }

    public void Dispose() => _slots.Dispose();
}
