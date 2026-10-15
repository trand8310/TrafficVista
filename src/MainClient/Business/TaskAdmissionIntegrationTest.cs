using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Scheduling;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Persistence;
using TrafficVista.Scheduling.Producers;

namespace TrafficVista.Business;

internal static class TaskAdmissionIntegrationTest
{
    private static readonly SystemPressureSample Healthy = new(true, 10, 16UL << 30, 12UL << 30, 20UL << 30);
    internal static async Task<string> RunAsync(ILogger logger)
    {
        var report = new StringBuilder();
        VerifyDispatchWindow(report);
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await VerifySharedPoolAsync(report);
        await VerifyLaunchPacingAsync(report);
        await VerifySetupTimeoutAsync(report);
        await VerifyManyWorkersAsync(root, logger, report);
        await VerifyPendingRecoveryAsync(root, logger, report);
        await VerifyFetchWaitAsync(report);
        await VerifyGracefulCloseAsync(logger, report);
        await VerifyRuntimeHiddenModeAsync(root, logger, report);
        report.AppendLine("输出目录：" + root);
        return report.ToString();
    }

    private static void VerifyDispatchWindow(StringBuilder report)
    {
        long second = 0;
        var metrics = new BusinessMetrics(() => second);
        metrics.DispatchUv(); metrics.DispatchUv();
        Expect(metrics.Snapshot.DispatchesPerMinute == 2, "actual startup dispatch count");
        second = 59; metrics.DispatchUv();
        Expect(metrics.Snapshot.DispatchesPerMinute == 3, "window retains recent dispatches");
        second = 60;
        Expect(metrics.Snapshot.DispatchesPerMinute == 1 && metrics.Snapshot.DispatchedUvs == 3, "old dispatches expire independently of cumulative total");
        second = 120;
        Expect(metrics.Snapshot.DispatchesPerMinute == 0, "idle dispatch rate returns to zero");
        metrics.DispatchUv(); metrics.Reset();
        Expect(metrics.Snapshot.DispatchesPerMinute == 0 && metrics.Snapshot.DispatchedUvs == 0, "restart resets dispatch window and totals");
        report.AppendLine("PASS 每分钟UV投递：实际启动量、60秒窗口到期、空闲归零、累计隔离及新轮次重置");
    }

    private static async Task VerifySetupTimeoutAsync(StringBuilder report)
    {
        using var launches = new BrowserLaunchLimiter(1, 0);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = false;
        var nextStarted = false;
        var first = launches.ExecuteAsync(() => BusinessUvBrowserRunner.RunSetupAsync(_ =>
        {
            entered.SetResult();
            return stalled.Task; // 模拟浏览器API不响应取消。
        }, 1, () => "cdp", () => aborted = true, CancellationToken.None), CancellationToken.None);
        await entered.Task;
        var next = launches.ExecuteAsync(() => { nextStarted = true; Expect(aborted, "abort before next launch"); return Task.CompletedTask; }, CancellationToken.None);
        await Task.Delay(100);
        Expect(!nextStarted, "launch slot remains held until page setup ends");
        try { await first.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("准备没有超时。"); }
        catch (TimeoutException ex) { Expect(ex.Message.Contains("cdp"), "timeout identifies stalled stage"); }
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        stalled.SetException(new IOException("迟到的浏览器API异常"));
        using var stop = new CancellationTokenSource();
        var cancel = BusinessUvBrowserRunner.RunSetupAsync(_ => Task.Delay(Timeout.Infinite, stop.Token),
            20, () => "page", () => { }, stop.Token);
        stop.Cancel();
        try { await cancel; throw new InvalidOperationException("外部取消未生效。"); }
        catch (OperationCanceledException) { }
        report.AppendLine("PASS 页面准备期间占用启动名额；无响应API独立超时并先终止浏览器再放行下一启动，标明卡点；外部取消保留取消分类");
    }

    private static async Task VerifySharedPoolAsync(StringBuilder report)
    {
        SystemPressureSample pressure = Healthy;
        var metrics = new BusinessMetrics();
        metrics.BrowserMemory(8UL << 30); // 历史异常峰值不能把24并发压缩为1。
        var messages = new List<string>();
        using var pool = new TaskCapacityAdmission(24, metrics, () => pressure, (_, reason) => messages.Add(reason));
        var options = new TrafficVistaOptions { MaxConcurrency = 24, UvExecutionInterval = 1000 };
        using var first = await pool.ReserveAsync(new(12, 1, 0, 0, 0), options, CancellationToken.None);
        await first.AcquireUvAsync(CancellationToken.None);
        using var second = await pool.ReserveAsync(new(12, 1, 0, 0, 0), options, CancellationToken.None);
        await second.AcquireUvAsync(CancellationToken.None);
        for (var i = 1; i < 12; i++)
        {
            await first.AcquireUvAsync(CancellationToken.None);
            await second.AcquireUvAsync(CancellationToken.None);
        }
        Expect(pool.Snapshot.ReservedSlots == 24 && first.Slots == 12 && second.Slots == 12,
            "two tasks fill configured concurrency despite historical memory peak and pending UVs");
        using (var stop = new CancellationTokenSource(80))
        {
            try { await first.AcquireUvAsync(stop.Token); throw new InvalidOperationException("UV超过全局并发。"); }
            catch (OperationCanceledException) { }
        }
        var time = Stopwatch.StartNew();
        await first.StartUvAsync(200, CancellationToken.None);
        await first.RecordDispatchedAsync();
        await second.StartUvAsync(200, CancellationToken.None);
        await second.RecordDispatchedAsync();
        Expect(time.Elapsed < TimeSpan.FromMilliseconds(150), "task intervals must not throttle each other");
        await Task.WhenAll(first.StartUvAsync(200, CancellationToken.None), second.StartUvAsync(200, CancellationToken.None));
        Expect(time.Elapsed >= TimeSpan.FromMilliseconds(180), "each task retains its own interval");
        pressure = Healthy with { CpuPercent = 99 };
        using var cancel = new CancellationTokenSource();
        var fetch = pool.WaitForFetchAsync(cancel.Token);
        await Task.Delay(100);
        Expect(!fetch.IsCompleted && messages.Count == 1, "resource pressure pauses new fetching and reports once");
        first.ReleaseUv();
        await first.AcquireUvAsync(CancellationToken.None);
        Expect(pool.Snapshot.ReservedSlots == 24, "existing work continues under pressure");
        pressure = Healthy;
        await fetch.WaitAsync(TimeSpan.FromSeconds(2));
        Expect(messages.Count == 2, "resource recovery is reported");
        for (var i = 0; i < 12; i++)
        {
            first.Dispatched(); second.Dispatched();
            first.ReleaseUv(); second.ReleaseUv();
        }
        Expect(pool.Snapshot.ReservedSlots == 0 && pool.Snapshot.PendingUvs == 0, "all global permits released");
        Expect(TaskCapacityAdmission.EstimateCompletionMs(12, 12, 30000, 1000) == 41000,
            "12 overlapping UVs at 1 second interval and 30 seconds duration take 41 seconds ideally");
        report.AppendLine("PASS 两项12UV任务填满24并发，历史内存峰值不缩减名额；任务间隔独立、并发硬上限、取消和释放；承压仅暂停新取任务并输出恢复信息；12UV理想41秒");
    }

    private static async Task VerifyLaunchPacingAsync(StringBuilder report)
    {
        using var limiter = new BrowserLaunchLimiter(2, 80);
        var starts = new System.Collections.Concurrent.ConcurrentQueue<long>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => limiter.ExecuteAsync(async () =>
        {
            starts.Enqueue(Stopwatch.GetTimestamp());
            await Task.Delay(150);
        }, CancellationToken.None)));
        var ordered = starts.Order().ToArray();
        Expect(limiter.PeakConcurrentStarts <= 2 && ordered.Zip(ordered.Skip(1))
            .All(pair => Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds >= 70),
            "browser startup concurrency and pacing apply across all tasks");
        report.AppendLine("PASS 浏览器独立启动限速：最多2个同时启动、连续启动间隔80ms；不限制已启动浏览器的运行数量");
    }

    private static async Task VerifyRuntimeHiddenModeAsync(string root, ILogger logger, StringBuilder report)
    {
        var runner = new ControlledRunner();
        var metrics = new BusinessMetrics();
        using var consumer = new BrowserTaskConsumer(new Devices(), new CountingProxy(), runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "runtime-hidden") }, logger, metrics, () => Healthy);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0, IsHiddenMode = false });
        var first = consumer.ConsumeAsync(TaskFor(4000, 2), CancellationToken.None).AsTask();
        await UntilAsync(() => runner.Started == 1);
        consumer.UpdateHiddenMode(true);
        var next = consumer.ConsumeAsync(TaskFor(4001, 1), CancellationToken.None).AsTask();
        runner.AllowFinish.TrySetResult();
        Expect(await first && await next, "hidden mode update must not reset reservations or interrupt tasks");
        Expect(runner.Modes.Where(value => value.TaskId == 4000).Count() == 2
            && runner.Modes.Where(value => value.TaskId == 4000).All(value => !value.Hidden)
            && runner.Modes.Single(value => value.TaskId == 4001).Hidden,
            "active task keeps its mode while waiting task gets latest hidden mode");
        consumer.UpdateHiddenMode(false);
        Expect(await consumer.ConsumeAsync(TaskFor(4002, 1), CancellationToken.None)
            && !runner.Modes.Single(value => value.TaskId == 4002).Hidden, "runtime hidden mode switches back");
        Expect(consumer.Admission.ReservedSlots == 0 && consumer.ActiveUvCount == 0, "runtime update releases all UV reservations");
        var totals = metrics.Snapshot;
        Expect(totals.PlannedUvs == 4 && totals.DispatchedUvs == 4 && totals.FinishedUvs == 4
            && totals.UndispatchedUvs == 0 && totals.ActiveStages.Count == 0, "UV totals and tracked stages reconcile");
        using (metrics.Track("诊断测试"))
            Expect(metrics.Snapshot.ActiveStages["诊断测试"].Count == 1, "active stage snapshot");
        Expect(metrics.Snapshot.ActiveStages.Count == 0, "stage disposal clears active waits");
        metrics.Reset();
        Expect(metrics.Snapshot.DispatchedUvs == 0 && metrics.Snapshot.PlannedUvs == 0, "new run resets totals");
        report.AppendLine("PASS 运行中切换隐藏模式：当前任务的后续UV保持原模式，下一任务采用最新值，可切换回可见；不重建准入或打断任务");
    }

    private static async Task VerifyManyWorkersAsync(string root, ILogger logger, StringBuilder report)
    {
        SystemPressureSample pressure = Healthy;
        var proxies = new CountingProxy();
        var runner = new ControlledRunner();
        using var consumer = new BrowserTaskConsumer(new Devices(), proxies, runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "workers50") }, logger, new(), () => Volatile.Read(ref pressure));
        var ui = new TrafficVistaOptions { TaskWorkerCount = 50, MaxConcurrency = 2, UvExecutionInterval = 0 };
        consumer.Configure(ui);
        var fetched = 0;
        await using var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token =>
            ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(Interlocked.Increment(ref fetched) == 1
                ? Enumerable.Range(0, 54).Select(index => TaskFor(1000 + index, 1)).ToArray() : [])), consumer);
        await scheduler.StartAsync(new() { WorkerCount = ui.TaskWorkerCount, QueueCapacity = 4,
            PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
        try
        {
            await UntilAsync(() => runner.Started == 2 && scheduler.Snapshot.Preparing == 48);
            Expect(scheduler.Snapshot.Workers == 50 && consumer.Admission.WaitingTasks == 48
                && runner.Peak == 2 && proxies.Calls == 2, "50 Workers do not bypass two UV lanes or acquire extra IPs");
            Volatile.Write(ref pressure, Healthy with { CpuPercent = 99 });
            runner.AllowFinish.TrySetResult();
            await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            Expect(proxies.Calls == 54 && runner.Peak <= 2 && consumer.ActiveUvCount == 0,
                "already fetched tasks continue under pressure while global UV concurrency stays at two");
        }
        finally { runner.AllowFinish.TrySetResult(); await scheduler.StopAsync(); }
        Expect(consumer.Admission.WaitingTasks == 0 && consumer.Admission.ReservedSlots == 0, "many canceled Workers release all preparation tickets");
        report.AppendLine("PASS 50任务Worker共享2个UV名额，不提前领超出名额的IP；已经取回的54项任务在后续承压时继续完成，停止不漏名额");
    }

    private static async Task VerifyPendingRecoveryAsync(string root, ILogger logger, StringBuilder report)
    {
        var pressure = Healthy with { CpuPercent = 99 };
        var proxies = new CountingProxy();
        using var consumer = new BrowserTaskConsumer(new Devices(), proxies, new InstantRunner(),
            new BusinessOptions { SessionRoot = Path.Combine(root, "pending") }, logger, new(), () => pressure);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0 });
        await using var held = await consumer.PrepareAsync(TaskFor(200, 1), CancellationToken.None);
        var task = TaskFor(201, 20);
        var cache = new LocalTaskCacheOptions { FilePath = Path.Combine(root, "pending.json"), LoadOnStart = true };
        var fetched = 0;
        await using (var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token =>
            ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(Interlocked.Increment(ref fetched) == 1 ? [task] : [])),
            consumer, cacheKeySelector: item => item.DeliveryId))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, QueueCapacity = 2, LocalCache = cache,
                PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
            await UntilAsync(() => scheduler.Snapshot.Preparing == 1 && consumer.Admission.WaitingTasks == 1);
            await scheduler.StopAsync();
            Expect(scheduler.Snapshot.Cached == 1 && proxies.Calls == 0, "resource wait is before durable claim and IP request");
        }
        await using (var restored = await LocalTaskCache<TaskEnvelope>.OpenAsync(cache, item => item.DeliveryId))
            Expect(restored.ReplayItems().Single().DeliveryId == task.DeliveryId, "admission-waiting task survives cancellation and restart");
        report.AppendLine("PASS 首UV并发名额等待发生在落盘消费确认前；等待中停止不领取IP，任务可恢复");
    }

    private static async Task VerifyFetchWaitAsync(StringBuilder report)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = 0;
        var producer = new DelegateTaskProducer<int>(_ => { Interlocked.Increment(ref fetched); return ValueTask.FromResult<IReadOnlyList<int>>([]); },
            async token => { waiting.TrySetResult(); await release.Task.WaitAsync(token); });
        await using var scheduler = new AsyncTaskScheduler<int>(producer,
            new DelegateTaskConsumer<int>((_, _) => ValueTask.FromResult(true)));
        await scheduler.StartAsync(new() { WorkerCount = 1, QueueCapacity = 2, FetchTimeout = TimeSpan.FromMilliseconds(50), CompleteWhenSourceEmpty = true });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Expect(fetched == 0 && scheduler.Snapshot.SourceErrors == 0, "pressure waiting is not remote fetch timeout");
        release.TrySetResult();
        var done = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(fetched == 1 && done.State == SchedulerState.Stopped && done.SourceErrors == 0, "fetch resumes cleanly");
        report.AppendLine("PASS 承压暂停领取发生在HTTP超时计时之前，不误计接口失败");
    }

    private static TaskEnvelope TaskFor(long id, int uvs) => new(id, "准入验证", "http://offline.invalid/", DateTime.UtcNow)
        { Payload = new JObject { ["uv"] = uvs, ["pv"] = 1, ["sleep"] = 0 } };
    private static async Task VerifyGracefulCloseAsync(ILogger logger, StringBuilder report)
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = Stopwatch.StartNew();
        Expect(!await BusinessUvBrowserRunner.CloseContextGracefullyAsync(() => slow.Task, TimeSpan.FromMilliseconds(30), logger, 1, 1)
            && timer.Elapsed < TimeSpan.FromSeconds(2), "slow graceful close switches to owned-job cleanup instead of failing completed page work");
        slow.TrySetException(new InvalidOperationException("late close fault after process exit"));
        Expect(await BusinessUvBrowserRunner.CloseContextGracefullyAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(1), logger, 1, 2),
            "normal graceful close succeeds");
        try
        {
            await BusinessUvBrowserRunner.CloseContextGracefullyAsync(() => Task.FromException(new IOException("close failure")),
                TimeSpan.FromSeconds(1), logger, 1, 3);
            throw new InvalidOperationException("Unexpected close error was hidden");
        }
        catch (IOException) { }
        report.AppendLine("PASS 优雅关闭超时切换自有进程树清理、迟到关闭异常观察，其他关闭错误仍保留");
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task UntilAsync(Func<bool> predicate)
    { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Admission fixture condition"); await Task.Delay(10); } }
    private sealed class CountingProxy : IBusinessProxyProvider
    {
        internal int Calls;
        public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1)
        { Interlocked.Increment(ref Calls); return ValueTask.FromResult<BusinessProxy?>(new("127.0.0.1:80") { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60) }); }
    }
    private sealed class Devices : IBusinessDeviceProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<long> Starts = new();
        public ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token)
        {
            Starts.Enqueue(Stopwatch.GetTimestamp());
            return ValueTask.FromResult(new BusinessDeviceProfile(Guid.NewGuid().ToString("N"), "test", "test", "test", "14", 1080, 2400, "test", 8, "test", "test", null));
        }
    }
    private sealed class InstantRunner : IBusinessUvRunner
    { public Task<bool> RunAsync(BusinessUvRun run, CancellationToken token) => Task.FromResult(true); }
    private sealed class ControlledRunner : IBusinessUvRunner
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(long TaskId, bool Hidden)> Modes = new();
        internal readonly TaskCompletionSource AllowFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Collections.Concurrent.ConcurrentQueue<long> Starts = new();
        private int _active; internal int Started, Peak;
        public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
        {
            Modes.Enqueue((run.TaskId, run.Options.IsHiddenMode));
            Starts.Enqueue(Stopwatch.GetTimestamp()); Interlocked.Increment(ref Started);
            var active = Interlocked.Increment(ref _active);
            int peak; do { peak = Peak; if (peak >= active) break; } while (Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try { await AllowFinish.Task.WaitAsync(token); await Task.Delay(10, token); return true; }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
