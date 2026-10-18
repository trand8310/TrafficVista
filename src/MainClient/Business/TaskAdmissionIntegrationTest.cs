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
    internal static async Task<string> RunAsync(ILogger logger)
    {
        var report = new StringBuilder();
        await VerifyCacheCallbackLifetimeAsync(report);
        await PlaywrightDriversIntegrationTest.VerifyAsync(logger, report);
        await VerifyCloseOptimizationAsync(report);
        VerifyDispatchWindow(report);
        await VerifyTaskIntakeAsync(logger, report);
        await VerifyOptionalIntakeAsync(logger, report);
        await VerifyForecastIntakeAsync(logger, report);
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await VerifyDispatchCadenceAsync(root, logger, report);
        await VerifyLaunchPacingAsync(report);
        await VerifyResourceStartProtectionAsync(report);
        await VerifySetupTimeoutAsync(report);
        await VerifyManyWorkersAsync(root, logger, report);
        await VerifyWorkerFailuresAsync(report);
        await VerifyPendingRecoveryAsync(root, logger, report);
        await TrafficVista.Scheduling.Examples.ScheduledRestartIntegrationTest.RunAsync(report);
        await VerifyFetchWaitAsync(report);
        await VerifyGracefulCloseAsync(logger, report);
        await VerifyRuntimeHiddenModeAsync(root, logger, report);
        report.AppendLine("输出目录：" + root);
        return report.ToString();
    }

    private static async Task VerifyCacheCallbackLifetimeAsync(StringBuilder report)
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var callbacks = new UvCacheCallbacks(CancellationToken.None, errors.Add);
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = Enumerable.Range(0, 80).Select(_ => Task.Run(() => callbacks.RunAsync(async token =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
        }))).ToArray();
        await UntilAsync(() => Volatile.Read(ref entered) == 80);
        callbacks.Stop();
        var lateCalls = 0;
        await callbacks.RunAsync(_ => { lateCalls++; throw new ObjectDisposedException("late CTS access"); });
        var drain = callbacks.DrainAsync();
        Expect(!drain.IsCompleted && lateCalls == 0, "stopped callbacks reject late events and retain admitted tasks until completion");
        release.SetResult();
        await Task.WhenAll(active);
        await drain;
        await callbacks.DisposeAsync();
        await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => callbacks.RunAsync(_ =>
        { Interlocked.Increment(ref lateCalls); throw new ObjectDisposedException("disposed source"); })));
        Expect(lateCalls == 0 && errors.IsEmpty, "late responses/routes after CTS disposal never execute and never fault a driver dispatcher");

        await using var isolated = new UvCacheCallbacks(CancellationToken.None, errors.Add);
        await isolated.RunAsync(_ => throw new IOException("synchronous event failure"));
        await isolated.RunAsync(_ => Task.FromException(new IOException("asynchronous route failure")));
        var healthy = false;
        await isolated.RunAsync(_ => { healthy = true; return Task.CompletedTask; });
        Expect(errors.Count == 2 && healthy, "sync and async callback failures are observed without poisoning subsequent callbacks");
        await using var canceled = new UvCacheCallbacks(CancellationToken.None, _ => throw new InvalidOperationException("unexpected callback error"));
        var waiting = canceled.RunAsync(token => Task.Delay(Timeout.Infinite, token));
        canceled.Stop();
        await canceled.DrainAsync();
        Expect(waiting.IsCompletedSuccessfully, "canceled cache callback completes without surfacing into Playwright");
        report.AppendLine("PASS 缓存回调生命周期：80并发回调排空、停止后及取消源释放后的迟到事件拒绝；同步/异步异常隔离，取消不影响驱动连接。");
    }

    private static async Task VerifyCloseOptimizationAsync(StringBuilder report)
    {
        TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundDetach = Signal();
        bool? deferred = null;
        Exception? diagnosticError = null;
        var observing = OwnedChromeSession.ObserveDiagnosticDetachAsync(() => backgroundDetach.Task,
            TimeSpan.FromSeconds(1), (pending, error) => { deferred = pending; diagnosticError = error; });
        Expect(!observing.IsCompleted && deferred is null, "page setup can start while diagnostic detach remains pending");
        backgroundDetach.SetResult();
        await observing;
        Expect(deferred == false && diagnosticError is null, "completed background detach is observed");
        await OwnedChromeSession.ObserveDiagnosticDetachAsync(() => throw new IOException("sync diagnostic failure"),
            TimeSpan.FromSeconds(1), (pending, error) => { deferred = pending; diagnosticError = error; });
        Expect(deferred == false && diagnosticError is IOException, "diagnostic invocation failure does not discard an owned browser");
        await OwnedChromeSession.ObserveDiagnosticDetachAsync(() => Task.FromException(new IOException("async diagnostic failure")),
            TimeSpan.FromSeconds(1), (pending, error) => { deferred = pending; diagnosticError = error; });
        Expect(deferred == false && diagnosticError is IOException, "asynchronous diagnostic failure remains observed");
        var lateDetach = Signal();
        await OwnedChromeSession.ObserveDiagnosticDetachAsync(() => lateDetach.Task, TimeSpan.FromMilliseconds(10),
            (pending, error) => { deferred = pending; diagnosticError = error; });
        Expect(deferred == true && diagnosticError is TimeoutException, "bounded diagnostic observer retains late fault observation");
        lateDetach.SetException(new IOException("late background detach"));
        var processParents = Enumerable.Range(1, 20000).ToDictionary(id => id, _ => 0);
        processParents[100] = 300; processParents[200] = 100; processParents[300] = 200;
        processParents[400] = 200; processParents[500] = 500;
        Expect(ChromeProcessJob.FindDescendants(100, processParents).ToHashSet().SetEquals(new[] { 100, 200, 300, 400 }),
            "indexed process traversal excludes unrelated processes and tolerates PID cycles");
        report.AppendLine("PASS 启动辅助优化：CDP解绑与页面准备并行，正常/同步异常/异步异常/超时/迟到异常均被观察；2万进程树索引排除无关进程并防环。");
        Expect(await OwnedChromeSession.CompleteStartupDetachAsync(Task.CompletedTask, TimeSpan.FromSeconds(1), () => false),
            "completed CDP detach needs no degraded-start decision");
        var detach = Signal();
        Expect(!await OwnedChromeSession.CompleteStartupDetachAsync(detach.Task, TimeSpan.FromMilliseconds(10), () => true),
            "slow diagnostic detach does not discard an owned healthy browser");
        detach.SetException(new IOException("late diagnostic detach failure"));
        var unhealthy = Signal();
        try
        {
            await OwnedChromeSession.CompleteStartupDetachAsync(unhealthy.Task, TimeSpan.FromMilliseconds(10), () => false);
            throw new InvalidOperationException("expected unhealthy detach failure");
        }
        catch (TimeoutException) { }
        unhealthy.SetResult();
        try
        {
            await OwnedChromeSession.CompleteStartupDetachAsync(Task.FromException(new TimeoutException("actual detach API failure")),
                TimeSpan.FromSeconds(1), () => true);
            throw new InvalidOperationException("expected actual detach failure");
        }
        catch (TimeoutException ex) when (ex.Message == "actual detach API failure") { }
        report.AppendLine("PASS 启动CDP解绑：正常完成、健康且已归属浏览器的等待超时降级；异常浏览器及实际API失败保留失败，迟到异常被观察。");
        var pending = Signal();
        Expect(await OwnedChromeSession.WaitForCloseGraceAsync(Task.CompletedTask, null, TimeSpan.FromSeconds(1)) == "正常关闭完成", "normal close completes");
        Expect(await OwnedChromeSession.WaitForCloseGraceAsync(pending.Task, Task.CompletedTask, TimeSpan.FromSeconds(10)) == "主进程已退出，提前收尾", "native exit avoids waiting for driver");
        Expect(await OwnedChromeSession.WaitForCloseGraceAsync(pending.Task, null, TimeSpan.FromMilliseconds(10)) == "正常关闭宽限已到", "grace expires while retaining close task");
        try
        {
            await OwnedChromeSession.WaitForCloseGraceAsync(Task.FromException(new IOException("close failed")), null, TimeSpan.FromSeconds(1));
            throw new InvalidOperationException("expected close fault");
        }
        catch (IOException) { }

        var coordinator = new BrowserCloseCoordinator(1);
        var lease = await coordinator.ReserveAsync(() => throw new InvalidOperationException("unexpected pressure"), TimeSpan.FromSeconds(1));
        var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Defer(pending.Task, lease, error => observed.TrySetResult(error));
        Expect(coordinator.Snapshot.InFlight == 1 && coordinator.Snapshot.Deferred == 1, "pending driver close remains tracked");
        Expect(!await coordinator.DrainAsync(TimeSpan.FromMilliseconds(10)), "drain timeout retains pending ownership");
        var pressure = 0;
        try
        {
            await coordinator.ReserveAsync(() => pressure++, TimeSpan.FromMilliseconds(10));
            throw new InvalidOperationException("expected slot timeout");
        }
        catch (TimeoutException) { }
        var next = coordinator.ReserveAsync(() => pressure++, TimeSpan.FromSeconds(5));
        Expect(!next.IsCompleted && pressure == 2, "capacity enforces backpressure");
        pending.SetException(new IOException("late close failure"));
        Expect(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)) is IOException, "late fault is observed");
        var nextLease = await next;
        nextLease.Dispose();
        nextLease.Dispose();
        Expect(await coordinator.DrainAsync(TimeSpan.FromSeconds(1)) && coordinator.Snapshot.InFlight == 0 && coordinator.Snapshot.Deferred == 0, "all close slots drain; release is idempotent");
        report.AppendLine("PASS: 关闭宽限、主进程提前退出、异常观察、有界收尾背压和排空。");
    }

    private static async Task VerifyTaskIntakeAsync(ILogger logger, StringBuilder report)
    {
        using var launches = new BrowserLaunchLimiter(1);
        using var gate = new BrowserTaskIntakeGate(launches, 3, 1, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitial = Signal();
        var initialEntered = Signal();
        var releaseTask = Signal();
        var taskEntered = Signal();
        var options = new SchedulerOptions { WorkerCount = 1, QueueCapacity = 4, PollInterval = TimeSpan.Zero,
            CompleteWhenSourceEmpty = true, StopTimeout = TimeSpan.FromSeconds(3) };
        var fetches = 0;
        await using var scheduler = new AsyncTaskScheduler<int>(_ => ValueTask.FromResult<IReadOnlyList<int>>(
            Interlocked.Increment(ref fetches) == 1 ? new[] { 1, 2 } : Array.Empty<int>()), async (item, token) =>
            {
                if (item == 1) { taskEntered.SetResult(); await releaseTask.Task.WaitAsync(token); }
                return true;
            }, queueFactory: settings => new TrafficVista.Scheduling.Queues.BoundedTaskQueue<int>(settings.QueueCapacity,
                settings.WorkerCount, gate.WaitAsync));
        await scheduler.StartAsync(options, timeout.Token);
        await taskEntered.Task.WaitAsync(timeout.Token);
        var initial = launches.ExecuteAsync(async () => { initialEntered.SetResult(); await releaseInitial.Task.WaitAsync(timeout.Token); }, timeout.Token);
        await initialEntered.Task.WaitAsync(timeout.Token);
        var releases = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
        var queued = releases.Select(release => launches.ExecuteAsync(() => release.Task.WaitAsync(timeout.Token), timeout.Token)).ToArray();
        await UntilAsync(() => gate.Snapshot.Paused && launches.WaitingStarts == 3);
        releaseTask.SetResult();
        await UntilAsync(() => gate.Snapshot.WaitingWorkers == 1);
        Expect(scheduler.Snapshot.Claimed == 1 && scheduler.Snapshot.Completed == 1 && scheduler.Snapshot.Queued == 1,
            "claimed task finishes; paused worker leaves next item queued and unclaimed");

        using (var canceled = new CancellationTokenSource())
        {
            var wait = gate.WaitAsync(canceled.Token).AsTask();
            canceled.Cancel();
            try { await wait; throw new InvalidOperationException("expected intake wait cancellation"); }
            catch (OperationCanceledException) { }
            Expect(gate.Snapshot.WaitingWorkers == 1, "canceled waiter removes its own count");
        }
        var stoppedFetches = 0;
        await using (var stopped = new AsyncTaskScheduler<int>(_ => ValueTask.FromResult<IReadOnlyList<int>>(
            Interlocked.Increment(ref stoppedFetches) == 1 ? new[] { 3 } : Array.Empty<int>()),
            (_, _) => ValueTask.FromResult(true), queueFactory: settings => new TrafficVista.Scheduling.Queues.BoundedTaskQueue<int>(
                settings.QueueCapacity, settings.WorkerCount, gate.WaitAsync)))
        {
            await stopped.StartAsync(options, timeout.Token);
            await UntilAsync(() => gate.Snapshot.WaitingWorkers == 2);
            await stopped.StopAsync(SchedulerStopMode.Cancel, timeout.Token);
            Expect(stopped.Snapshot.Claimed == 0 && gate.Snapshot.WaitingWorkers == 1, "stop while paused exits without claiming");
        }
        releaseInitial.SetResult();
        await UntilAsync(() => launches.WaitingStarts == 2);
        Expect(gate.Snapshot.Paused && scheduler.Snapshot.Claimed == 1, "middle band retains paused state");
        releases[0].SetResult();
        await UntilAsync(() => launches.WaitingStarts == 1);
        var done = await scheduler.Completion.WaitAsync(timeout.Token);
        Expect(done.Claimed == 2 && done.Succeeded == 2 && !gate.Snapshot.Paused && gate.Snapshot.WaitingWorkers == 0,
            "lower threshold resumes and drains all tasks");
        foreach (var release in releases) release.TrySetResult();
        await Task.WhenAll(queued.Append(initial)).WaitAsync(timeout.Token);
        Expect(launches.WaitingStarts == 0, "startup queue counter drains");
        report.AppendLine("PASS 任务领取背压：达到上限暂停、区间保持暂停、低水位恢复；已领取任务继续、未领取保留队列、等待可取消及停止归零");
    }

    private static async Task VerifyOptionalIntakeAsync(ILogger logger, StringBuilder report)
    {
        using var launches = new BrowserLaunchLimiter(1);
        using var gate = new BrowserTaskIntakeGate(launches, 100, 50, logger);
        using var first = gate.TryReserve("first", 120);
        Expect(gate.Snapshot.Paused, "enabled backpressure pauses above threshold");
        var waiter = gate.WaitAsync(CancellationToken.None).AsTask();
        Expect(!waiter.IsCompleted && gate.Snapshot.WaitingWorkers == 1, "worker waits at enabled gate");
        gate.SetEnabled(false);
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        using var second = gate.TryReserve("second", 120);
        Expect(second is not null && !gate.Snapshot.Enabled && !gate.Snapshot.Paused
            && gate.Snapshot.Pressure == 240 && gate.Snapshot.WaitingWorkers == 0,
            "disabling wakes paused workers and permits reservations above threshold while retaining pressure");
        gate.SetEnabled(true);
        Expect(gate.Snapshot.Enabled && gate.Snapshot.Paused && gate.TryReserve("blocked", 1) is null,
            "re-enabling immediately enforces existing pressure");
        first!.Dispose(); second!.Dispose();
        Expect(!gate.Snapshot.Paused && gate.Snapshot.Pressure == 0, "reservations drain after live toggles");
        report.AppendLine("PASS 背压开关：默认开启、关闭唤醒等待工作者并放行高压力领取、保留压力统计、重新开启即时暂停、清理归零");
    }

    private static async Task VerifyForecastIntakeAsync(ILogger logger, StringBuilder report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var launches = new BrowserLaunchLimiter(1);
        using var gate = new BrowserTaskIntakeGate(launches, 100, 50, logger);
        var proxies = new HeldProxy();
        var devices = new HeldDevices();
        var runner = new IntakeRunner(launches);
        using var consumer = new BrowserTaskConsumer(devices, proxies, runner, new(), logger) { IntakeGate = gate };
        consumer.Configure(new() { UvExecutionInterval = 10 });
        var tasks = Enumerable.Range(0, 40).Select(i => TaskFor(5000 + i, 20)).ToArray();
        var fetched = false;
        await using var scheduler = new AsyncTaskScheduler<TaskEnvelope>(_ =>
        {
            IReadOnlyList<TaskEnvelope> batch = fetched ? Array.Empty<TaskEnvelope>() : tasks;
            fetched = true;
            return ValueTask.FromResult(batch);
        }, consumer.ConsumeAsync, queueFactory: settings => new TrafficVista.Scheduling.Queues.BoundedTaskQueue<TaskEnvelope>(
            settings.QueueCapacity, settings.WorkerCount, gate.WaitAsync,
            task => gate.TryReserve(task.DeliveryId, consumer.EstimatePendingUvs(task))));
        var options = new SchedulerOptions { WorkerCount = 80, QueueCapacity = 80, PollInterval = TimeSpan.Zero,
            CompleteWhenSourceEmpty = true, StopTimeout = TimeSpan.FromSeconds(5) };
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStart = launches.ExecuteAsync(() => releaseStart.Task.WaitAsync(timeout.Token), timeout.Token);
        await scheduler.StartAsync(options, timeout.Token);
        try
        {
            await UntilAsync(() => proxies.Calls == 5 && gate.Snapshot.WaitingWorkers == 75);
            Expect(gate.Snapshot.Paused && gate.Snapshot.PendingUvs == 100 && gate.Snapshot.Pressure == 100
                && scheduler.Snapshot.Claimed == 5 && scheduler.Snapshot.Queued == 35,
                "80 simultaneous workers reserve five 20-UV tasks before proxy acquisition; no herd overshoot");
            proxies.Release.TrySetResult();
            await UntilAsync(() => consumer.ActiveUvCount == 100 && gate.Snapshot.PreparingUvs == 100);
            Expect(gate.Snapshot.PendingUvs == 0 && gate.Snapshot.Pressure == 100 && scheduler.Snapshot.Claimed == 5,
                "device preparation retains pressure after dispatch");
            devices.Release.TrySetResult();
            await UntilAsync(() => launches.WaitingStarts == 100 && gate.Snapshot.PreparingUvs == 0);
            Expect(gate.Snapshot.Pressure == 100 && scheduler.Snapshot.Claimed == 5,
                "pressure transfers from preparation to startup queue without allowing extra tasks");
            releaseStart.TrySetResult();
            await firstStart;
            await UntilAsync(() => runner.Started == 800);
            Expect(scheduler.Snapshot.Claimed == 40 && scheduler.Snapshot.Completed == 0,
                "started browsers do not consume future-start demand; UVs continue at their cadence without a UV pool");
            runner.Release.TrySetResult();
            var result = await scheduler.Completion.WaitAsync(timeout.Token);
            Expect(result.Succeeded == 40 && gate.Snapshot.Pressure == 0 && !gate.Snapshot.Paused
                && gate.Snapshot.WaitingWorkers == 0, "success drains every demand reservation");
        }
        finally
        {
            proxies.Release.TrySetResult(); devices.Release.TrySetResult(); releaseStart.TrySetResult(); runner.Release.TrySetResult();
            await firstStart;
        }

        var held = new HeldProxy();
        using var canceledConsumer = new BrowserTaskConsumer(new Devices(), held, runner, new(), logger) { IntakeGate = gate };
        canceledConsumer.Configure(new());
        var cancelFetched = false;
        await using var canceled = new AsyncTaskScheduler<TaskEnvelope>(_ =>
        {
            IReadOnlyList<TaskEnvelope> batch = cancelFetched ? Array.Empty<TaskEnvelope>() : new[] { TaskFor(6000, 200), TaskFor(6001, 20) };
            cancelFetched = true;
            return ValueTask.FromResult(batch);
        }, canceledConsumer.ConsumeAsync, queueFactory: settings => new TrafficVista.Scheduling.Queues.BoundedTaskQueue<TaskEnvelope>(
            settings.QueueCapacity, settings.WorkerCount, gate.WaitAsync,
            task => gate.TryReserve(task.DeliveryId, canceledConsumer.EstimatePendingUvs(task))));
        await canceled.StartAsync(options, timeout.Token);
        await UntilAsync(() => held.Calls == 1 && gate.Snapshot.Paused);
        Expect(gate.Snapshot.PendingUvs == 200 && canceled.Snapshot.Claimed == 1 && canceled.Snapshot.Queued == 1,
            "single task larger than threshold is allowed, subsequent tasks remain queued");
        await canceled.StopAsync(SchedulerStopMode.Cancel, timeout.Token);
        Expect(gate.Snapshot.Pressure == 0 && gate.Snapshot.WaitingWorkers == 0 && !gate.Snapshot.Paused,
            "cancel before first UV releases forecast and opens next run");
        var invalid = TaskFor(6002, 0);
        Expect(canceledConsumer.EstimatePendingUvs(invalid) == 0, "invalid plans defer failure to task execution");
        report.AppendLine("PASS 预测领取背压：80工作者仅领取5项×20UV；代理前登记、设备准备保留、启动排队移交、后续恢复；不限制已启动UV；大任务不饿死，取消和结束归零。");
    }

    private sealed class HeldProxy : IBusinessProxyProvider
    {
        internal int Calls;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1)
        { Interlocked.Increment(ref Calls); await Release.Task.WaitAsync(token); return null; }
    }
    private sealed class HeldDevices : IBusinessDeviceProvider
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Devices _devices = new();
        public async ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token)
        { await Release.Task.WaitAsync(token); return await _devices.AcquireAsync(options, taskId, uvId, token); }
    }
    private sealed class IntakeRunner(BrowserLaunchLimiter launches) : IBusinessUvRunner
    {
        internal int Started;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
        {
            await launches.ExecuteAsync(() => { Interlocked.Increment(ref Started); return Task.CompletedTask; }, token, run.BrowserStartQueued);
            await Release.Task.WaitAsync(token);
            return true;
        }
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
        Parallel.For(0, 80, _ =>
        {
            metrics.DispatchUv();
            metrics.BrowserStartAttempted();
            metrics.UvExposed();
        });
        metrics.DispatchUv(); // Device preparation failed: no browser start.
        metrics.BrowserStartAttempted(); // A start failure does not become an exposure.
        metrics.UvFinished(false, false, TimeSpan.FromSeconds(1));
        var runtime = metrics.Snapshot;
        Expect(runtime.DispatchedUvs == 81 && runtime.BrowserStartAttempts == 81 && runtime.ExposedUvs == 80,
            "concurrent funnel counts stay independent of final failures");
        metrics.StopRuntime();
        var stopped = metrics.Snapshot.RunElapsed;
        Thread.Sleep(20);
        Expect(metrics.Snapshot.RunElapsed == stopped, "completed run duration freezes");
        metrics.Reset();
        Expect(metrics.Snapshot.DispatchedUvs == 0 && metrics.Snapshot.BrowserStartAttempts == 0
            && metrics.Snapshot.ExposedUvs == 0, "new run clears all funnel counters");
        report.AppendLine("PASS 运行指标：并发累计提交/执行/曝光、失败不回退、运行时长冻结、下一轮清零");
        report.AppendLine("PASS 每分钟UV投递：实际启动量、60秒窗口到期、空闲归零、累计隔离及新轮次重置");
        var capture = metrics.Track("缓存正文读取", new CacheResourceStage("缓存正文读取", "https://test/resource.js", 7, 8, 9, 0));
        var snapshot = metrics.Snapshot;
        Expect(snapshot.ActiveStages["缓存正文读取"].Count == 1 && snapshot.ActiveCacheResources.Single().WorkerId == 9
            && snapshot.ActiveCacheResources.Single().TaskId == 7 && snapshot.ActiveCacheResources.Single().UvId == 8,
            "cache phase snapshot retains resource identity");
        capture.Dispose(); capture.Dispose();
        Expect(metrics.Snapshot.ActiveCacheResources.Length == 0 && metrics.Snapshot.ActiveStages.Count == 0
            && metrics.Snapshot.AverageStageMs.ContainsKey("缓存正文读取"), "cache phase completion drains once and retains duration");
        metrics.Reset();
        Expect(metrics.Snapshot.AverageStageMs.Count == 0, "cache phase metrics reset with run");
        report.AppendLine("PASS 缓存采集分阶段：当前快照、资源身份、结束归零、耗时累计及重置");
    }

    private static async Task VerifyResourceStartProtectionAsync(StringBuilder report)
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var sample = new SystemPressureSample(true, 96, 32 * gib, 8 * gib, 16 * gib);
        Expect(BrowserLaunchLimiter.ResourceStartLimit(sample, 4) == 1
            && BrowserLaunchLimiter.ResourceStartLimit(sample with { CpuPercent = 85 }, 4) == 2
            && BrowserLaunchLimiter.ResourceStartLimit(sample with { Available = false }, 4) == 4,
            "CPU protection reduces only startup concurrency; unavailable sample preserves configured capacity");
        Expect(BrowserLaunchLimiter.ResourceStartLimit(sample with { CpuPercent = 30, AvailablePhysicalBytes = 1280UL * 1024 * 1024 }, 4) == 2
            && BrowserLaunchLimiter.ResourceStartLimit(sample with { Available = false, AvailableCommitBytes = 128UL * 1024 * 1024 }, 4) == 0,
            "startup memory budget limits bursts and protects memory before CPU warmup completes");
        using var launches = new BrowserLaunchLimiter(4, 0, () => Volatile.Read(ref sample));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var work = Enumerable.Range(0, 4).Select(index => launches.ExecuteAsync(async () =>
        { order.Enqueue(index); Interlocked.Increment(ref started); await release.Task; }, CancellationToken.None)).ToArray();
        try
        {
            await UntilAsync(() => Volatile.Read(ref started) == 1);
            await Task.Delay(300);
            Expect(started == 1 && launches.WaitingStarts == 3, "busy CPU permits one startup while preserving queued UVs");
            Volatile.Write(ref sample, sample with { CpuPercent = 30 });
            await UntilAsync(() => Volatile.Read(ref started) == 4);
        }
        finally { release.TrySetResult(); await Task.WhenAll(work); }
        Expect(launches.WaitingStarts == 0 && launches.PeakConcurrentStarts == 4 && order.SequenceEqual(Enumerable.Range(0, 4)),
            "resource recovery restores configured startup capacity without overtaking older UVs");
        foreach (var lowMemory in new[] { sample with { AvailablePhysicalBytes = 128 * 1024 * 1024 },
            sample with { AvailableCommitBytes = 128 * 1024 * 1024 } })
        {
            Volatile.Write(ref sample, lowMemory);
            using var cancel = new CancellationTokenSource();
            var called = false;
            var waiting = launches.ExecuteAsync(() => { called = true; return Task.CompletedTask; }, cancel.Token);
            await Task.Delay(50);
            Expect(!called && !waiting.IsCompleted && launches.EffectiveStartLimit == 0, "low physical/commit memory queues new starts");
            cancel.Cancel();
            try { await waiting; throw new InvalidOperationException("expected resource wait cancellation"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Expect(launches.WaitingStarts == 0, "resource wait cancellation releases every queue reservation");
        }
        Volatile.Write(ref sample, sample with { AvailablePhysicalBytes = 8 * gib, AvailableCommitBytes = 16 * gib });
        await launches.ExecuteAsync(() => Task.CompletedTask, CancellationToken.None);
        launches.Configure(4);
        report.AppendLine("PASS 启动资源保护：高CPU收敛到1路、恢复后4路；低物理内存/提交余量暂停新启动，取消释放名额，不取消已有UV。");
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

    private static async Task VerifyDispatchCadenceAsync(string root, ILogger logger, StringBuilder report)
    {
        var devices = new Devices();
        var runner = new ControlledRunner();
        var proxies = new CountingProxy();
        using var consumer = new BrowserTaskConsumer(devices, proxies, runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "cadence") }, logger);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 100 });
        var first = consumer.ConsumeAsync(TaskFor(10, 4), CancellationToken.None).AsTask();
        var second = consumer.ConsumeAsync(TaskFor(11, 4), CancellationToken.None).AsTask();
        try
        {
            await UntilAsync(() => runner.Started == 8);
            Expect(!first.IsCompleted && !second.IsCompleted && consumer.ActiveUvCount == 8,
                "all UVs dispatch without waiting for live sessions, even with launch concurrency one");
            foreach (var group in devices.Starts.GroupBy(value => value.TaskId))
            {
                var times = group.Select(value => value.Started).ToArray();
                Expect(times.Length == 4 && times.Zip(times.Skip(1)).All(pair =>
                    Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds >= 90),
                    "each task observes its own UV interval");
            }
            Expect(proxies.Calls == 2 && consumer.Dispatch.PendingUvs == 0, "one proxy per task and all UVs dispatched");
        }
        finally { runner.AllowFinish.TrySetResult(); await Task.WhenAll(first, second); }
        Expect(consumer.ActiveUvCount == 0 && consumer.Dispatch.ActiveTasks == 0, "all sessions clean up");

        var canceledRunner = new ControlledRunner();
        using var canceledConsumer = new BrowserTaskConsumer(new Devices(), new CountingProxy(), canceledRunner,
            new BusinessOptions(), logger);
        canceledConsumer.Configure(new() { UvExecutionInterval = 10000 });
        using var stop = new CancellationTokenSource();
        var running = canceledConsumer.ConsumeAsync(TaskFor(12, 4), stop.Token).AsTask();
        await UntilAsync(() => canceledRunner.Started == 1);
        stop.Cancel();
        try { await running.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("取消未传播。"); }
        catch (OperationCanceledException) { }
        Expect(canceledRunner.Started == 1 && canceledConsumer.ActiveUvCount == 0
            && canceledConsumer.Dispatch.PendingUvs == 0, "cancel cadence and clean every dispatched UV");
        report.AppendLine("PASS 两项任务各按100ms间隔投递4个UV，已有UV未结束仍全部投递，启动并发参数不限制UV存活数；间隔等待可取消并清理");
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
        Expect(limiter.WaitingStarts == 0, "startup queue drains without change listeners");
        Expect(limiter.PeakConcurrentStarts <= 2 && ordered.Zip(ordered.Skip(1))
            .All(pair => Stopwatch.GetElapsedTime(pair.First, pair.Second).TotalMilliseconds >= 70),
            "browser startup concurrency and pacing apply across all tasks");
        limiter.Configure(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = limiter.ExecuteAsync(() => { entered.SetResult(); return release.Task; }, CancellationToken.None);
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var queued = limiter.ExecuteAsync(() => throw new InvalidOperationException("Canceled start executed"), cancel.Token);
        try { limiter.Configure(3); throw new InvalidOperationException("Active limiter was reconfigured"); }
        catch (InvalidOperationException ex) when (ex.Message == "浏览器仍在启动或等待启动。") { }
        cancel.Cancel();
        try { await queued; throw new InvalidOperationException("Queued start was not canceled"); }
        catch (OperationCanceledException) { }
        Expect(limiter.WaitingStarts == 0, "canceled queued start removes count without listeners");
        release.SetResult();
        await active;
        limiter.Configure(3);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => limiter.ExecuteAsync(() => Task.Delay(250), CancellationToken.None)));
        Expect(limiter.MaxConcurrentStarts == 3 && limiter.PeakConcurrentStarts == 3 && limiter.WaitingStarts == 0, "idle reconfiguration and canceled wait release slots");
        report.AppendLine("PASS 浏览器独立启动限速：最多2个同时启动、连续启动间隔80ms；不限制已启动浏览器的运行数量");
    }

    private static async Task VerifyRuntimeHiddenModeAsync(string root, ILogger logger, StringBuilder report)
    {
        var runner = new ControlledRunner();
        var metrics = new BusinessMetrics();
        using var consumer = new BrowserTaskConsumer(new Devices(), new CountingProxy(), runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "runtime-hidden") }, logger, metrics);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 100, IsHiddenMode = false });
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
        Expect(consumer.Dispatch.ActiveTasks == 0 && consumer.ActiveUvCount == 0, "runtime update releases all UV reservations");
        var totals = metrics.Snapshot;
        Expect(totals.PlannedUvs == 4 && totals.DispatchedUvs == 4 && totals.FinishedUvs == 4
            && totals.UndispatchedUvs == 0 && totals.ActiveStages.Count == 0, "UV totals and tracked stages reconcile");
        using (metrics.Track("诊断测试"))
            Expect(metrics.Snapshot.ActiveStages["诊断测试"].Count == 1, "active stage snapshot");
        Expect(metrics.Snapshot.ActiveStages.Count == 0, "stage disposal clears active waits");
        metrics.Reset();
        Expect(metrics.Snapshot.DispatchedUvs == 0 && metrics.Snapshot.PlannedUvs == 0, "new run resets totals");
        report.AppendLine("PASS 运行中切换隐藏模式：当前任务的后续UV保持原模式，下一任务采用最新值，可切换回可见；不打断当前任务");
    }

    private static async Task VerifyManyWorkersAsync(string root, ILogger logger, StringBuilder report)
    {
        var proxies = new CountingProxy();
        var runner = new ControlledRunner();
        using var consumer = new BrowserTaskConsumer(new Devices(), proxies, runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "workers50") }, logger);
        consumer.Configure(new() { MaxConcurrency = 50, UvExecutionInterval = 0 });
        var fetched = 0;
        await using var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token =>
            ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(Interlocked.Increment(ref fetched) == 1
                ? Enumerable.Range(0, 54).Select(index => TaskFor(1000 + index, 1)).ToArray() : [])), consumer);
        await scheduler.StartAsync(new() { WorkerCount = 50, QueueCapacity = 4,
            PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
        try
        {
            await UntilAsync(() => runner.Started == 50 && scheduler.Snapshot.Queued == 4);
            Expect(scheduler.Snapshot.Running == 50 && proxies.Calls == 50,
                "all 50 long-lived workers execute one task each; next four remain queued until cleanup");
            runner.AllowFinish.TrySetResult();
            var result = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            Expect(result.State == SchedulerState.Stopped && result.Succeeded == 54
                && proxies.Calls == 54 && runner.Peak == 50 && consumer.ActiveUvCount == 0,
                "worker count caps concurrent tasks while all queued work finishes");
        }
        finally { runner.AllowFinish.TrySetResult(); await scheduler.StopAsync(); }
        Expect(consumer.Dispatch.ActiveTasks == 0, "all tasks finished");
        report.AppendLine("PASS Parallel.ForEachAsync启动50个长期任务工作者，首批50项未完成时后4项保持排队；全部54项完成，任务并发峰值50");
    }

    private static async Task VerifyPendingRecoveryAsync(string root, ILogger logger, StringBuilder report)
    {
        var proxies = new CountingProxy();
        var runner = new ControlledRunner();
        using var consumer = new BrowserTaskConsumer(new Devices(), proxies, runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "pending") }, logger);
        consumer.Configure(new() { UvExecutionInterval = 0 });
        var tasks = Enumerable.Range(0, 3).Select(i => TaskFor(200 + i, 1)).ToArray();
        var cache = new LocalTaskCacheOptions { FilePath = Path.Combine(root, "pending.json"), LoadOnStart = true };
        var fetched = 0;
        await using (var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(token =>
            ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(Interlocked.Increment(ref fetched) == 1 ? tasks : [])),
            consumer, cacheKeySelector: item => item.DeliveryId))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, QueueCapacity = 2, LocalCache = cache,
                PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
            await UntilAsync(() => runner.Started == 1 && scheduler.Snapshot.Queued == 2);
            await scheduler.StopAsync();
            Expect(scheduler.Snapshot.Cached == 2 && scheduler.Snapshot.Canceled == 1 && proxies.Calls == 1,
                "only executing task is claimed; queued tasks do not acquire proxies");
        }
        await using (var restored = await LocalTaskCache<TaskEnvelope>.OpenAsync(cache, item => item.DeliveryId))
            Expect(restored.ReplayItems().Select(item => item.DeliveryId).Order()
                .SequenceEqual(tasks.Skip(1).Select(item => item.DeliveryId).Order()), "queued tasks survive restart");
        Expect(consumer.ActiveUvCount == 0 && consumer.Dispatch.PendingUvs == 0, "cancel cleans sessions and pending progress");
        report.AppendLine("PASS 单工作者串行消费，停止取消当前任务并清理；排队任务不领取IP，持久化后可恢复");
    }

    private static async Task VerifyWorkerFailuresAsync(StringBuilder report)
    {
        var fetched = 0;
        var consumer = new PreparationConsumer();
        await using var scheduler = new AsyncTaskScheduler<int>(new DelegateTaskProducer<int>(_ =>
            ValueTask.FromResult<IReadOnlyList<int>>(Interlocked.Increment(ref fetched) == 1 ? [0, 1, 2, 3, 4, 5] : [])), consumer);
        var options = new SchedulerOptions { WorkerCount = 3, QueueCapacity = 1,
            PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true };
        await scheduler.StartAsync(options);
        var failed = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(failed.State == SchedulerState.Faulted && failed.LastError?.Contains("fixture-prepare") == true,
            "fatal preparation error survives Parallel cancellation and stops producer");
        consumer.FailPreparation = false;
        fetched = 0;
        await scheduler.StartAsync(options);
        var restarted = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(restarted.State == SchedulerState.Stopped && restarted.Completed == 6 && restarted.Failed == 2,
            "restart after fatal failure; false/exception outcomes do not stop subsequent tasks");
        report.AppendLine("PASS 工作者基础设施异常停止整轮并保留原始故障，可重新启动；单项失败/异常不中断后续消费");
    }

    private sealed class PreparationConsumer : IPreparedTaskConsumer<int>
    {
        internal bool FailPreparation = true;
        public ValueTask<IPreparedTask> PrepareAsync(int item, CancellationToken token)
            => FailPreparation ? ValueTask.FromException<IPreparedTask>(new IOException("fixture-prepare"))
                : ValueTask.FromResult<IPreparedTask>(new Prepared(item));
        public ValueTask<bool> ConsumeAsync(int item, CancellationToken token) => new Prepared(item).ExecuteAsync(token);
        private sealed class Prepared(int item) : IPreparedTask
        {
            public ValueTask<bool> ExecuteAsync(CancellationToken token)
                => item == 1 ? ValueTask.FromException<bool>(new IOException("fixture-item")) : ValueTask.FromResult(item != 0);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
        report.AppendLine("PASS 通用生产者等待与HTTP超时隔离，不误计接口失败");
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
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(long TaskId, long Started)> Starts = new();
        public ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token)
        {
            Starts.Enqueue((taskId, Stopwatch.GetTimestamp()));
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
