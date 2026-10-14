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
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await VerifyReservationsAsync(report);
        await VerifyManyWorkersAsync(root, logger, report);
        await VerifyIpPriorityAsync(root, logger, report);
        await VerifyFirstLaunchWindowAsync(root, logger, report);
        await VerifyPendingRecoveryAsync(root, logger, report);
        await VerifyFetchWaitAsync(report);
        await VerifyGracefulCloseAsync(logger, report);
        report.AppendLine("输出目录：" + root);
        return report.ToString();
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
            await UntilAsync(() => consumer.ActiveUvCount == 0 && scheduler.Snapshot.Preparing == 50);
            Expect(proxies.Calls == 2 && consumer.Admission.ReservedSlots == 0, "CPU pressure blocks all waiting Workers after current UVs finish");
        }
        finally { runner.AllowFinish.TrySetResult(); await scheduler.StopAsync(); }
        Expect(consumer.Admission.WaitingTasks == 0 && consumer.Admission.ReservedSlots == 0, "many canceled Workers release all preparation tickets");
        report.AppendLine("PASS 50任务Worker实际启动：UV峰值仍为2，其他48个等待且不提前领IP；CPU承压后50个等待，停止释放全部名额");
    }

    private static async Task VerifyReservationsAsync(StringBuilder report)
    {
        SystemPressureSample pressure = Healthy with { CpuPercent = 95 };
        var metrics = new BusinessMetrics();
        using var gate = new TaskCapacityAdmission(4, metrics, () => Volatile.Read(ref pressure));
        var options = new TrafficVistaOptions { MaxConcurrency = 4, UvExecutionInterval = 2000, IsProxyMode = true, IpTtl = 120 };
        var plan = new UvCapacityPlan(20, 1, 0, 0, 0);
        Expect(TaskCapacityAdmission.EstimateCompletionMs(1, 1, 6000, 60000) == 6000
            && TaskCapacityAdmission.EstimateCompletionMs(20, 4, 6000, 2000) == 44000,
            "completion estimate excludes the first cadence window already waited before IP acquisition");
        var pending = gate.ReserveAsync(plan, options, CancellationToken.None);
        await UntilAsync(() => gate.Snapshot.WaitingTasks == 1);
        Expect(!pending.IsCompleted && gate.Snapshot.ReservedSlots == 0, "CPU pressure prevents admission");
        Volatile.Write(ref pressure, Healthy with { CpuPercent = 78 });
        await Task.Delay(300);
        Expect(!pending.IsCompleted, "CPU recovery hysteresis");
        Volatile.Write(ref pressure, Healthy);
        using (var reservation = await pending.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Expect(reservation.Slots == 3, "cadence determines required lanes");
            using var cancel = new CancellationTokenSource();
            var next = gate.ReserveAsync(new(1, 1, 0, 0, 0), options, cancel.Token);
            await UntilAsync(() => gate.Snapshot.WaitingTasks == 1);
            Expect(!next.IsCompleted, "new IP waits until existing task dispatches all UVs even with spare capacity");
            cancel.Cancel();
            try { await next; throw new InvalidOperationException("Canceled waiter acquired a reservation"); }
            catch (OperationCanceledException) { }
            Expect(gate.Snapshot.WaitingTasks == 0 && gate.Snapshot.ReservedSlots == 3, "cancel returns unclaimed ticket");
            Volatile.Write(ref pressure, Healthy with { AvailablePhysicalBytes = 100UL << 20, CpuPercent = 99 });
            for (var i = 0; i < 20; i++)
            {
                await reservation.AcquireUvAsync(CancellationToken.None);
                reservation.Dispatched(); reservation.ReleaseUv();
            }
            Expect(gate.Snapshot.PendingUvs == 0, "existing reservation keeps working under later pressure");
        }
        Volatile.Write(ref pressure, Healthy);
        gate.ObserveUv(plan, TimeSpan.FromSeconds(1), true);
        using (var learned = await gate.ReserveAsync(plan, options, CancellationToken.None))
        {
            Expect(learned.Slots == 1 && learned.EstimatedUvMs == 1200, "successful UV timings automatically calibrate overhead");
            for (var i = 0; i < 4; i++) await learned.AcquireUvAsync(CancellationToken.None);
            Expect(learned.Slots == 4 && gate.Snapshot.ReservedSlots == 4, "underestimated UVs borrow idle capacity without exceeding hard limit");
            using var blocked = new CancellationTokenSource(80);
            try { await learned.AcquireUvAsync(blocked.Token); throw new InvalidOperationException("Expanded reservation exceeded hard limit"); }
            catch (OperationCanceledException) { }
            for (var i = 0; i < 4; i++) learned.ReleaseUv();
        }
        using (var longPlan = await gate.ReserveAsync(new(100, 1000, 86400, 86400, 86400), options, CancellationToken.None))
            Expect(longPlan.Slots == 4 && longPlan.DeadlineAtRisk, "large plans do not overflow or wait forever for impossible TTL");
        metrics.BrowserMemory(2UL << 30);
        Volatile.Write(ref pressure, Healthy with { AvailablePhysicalBytes = 6UL << 30 });
        using (var memoryLimited = await gate.ReserveAsync(new(20, 1, 0, 0, 0), options with { UvExecutionInterval = 0 }, CancellationToken.None))
            Expect(memoryLimited.Slots == 1, "observed browser memory automatically reduces admission slots");
        Volatile.Write(ref pressure, Healthy with { Available = false });
        using (var stop = new CancellationTokenSource(350))
        {
            try { await gate.ReserveAsync(plan, options, stop.Token); throw new InvalidOperationException("Missing resource sample admitted work"); }
            catch (OperationCanceledException) { }
            Expect(gate.Snapshot.Reason.Contains("采样失败") && !gate.HasReservations, "probe failure is visible and does not leak reservations");
        }
        report.AppendLine("PASS 自动耗时/内存估算、CPU恢复滞回、大UV计划、预留名额不被新IP抢占、压力升高后继续投递、取消及采样故障");
    }

    private static async Task VerifyIpPriorityAsync(string root, ILogger logger, StringBuilder report)
    {
        SystemPressureSample pressure = Healthy;
        var proxies = new CountingProxy(); var runner = new ControlledRunner(); var devices = new Devices();
        using var consumer = new BrowserTaskConsumer(devices, proxies, runner,
            new BusinessOptions { SessionRoot = Path.Combine(root, "priority") }, logger, new(), () => Volatile.Read(ref pressure));
        consumer.Configure(new() { MaxConcurrency = 2, UvExecutionInterval = 30, IsProxyMode = true, IpTtl = 60 });
        var first = consumer.ConsumeAsync(TaskFor(101, 20), CancellationToken.None).AsTask();
        await UntilAsync(() => runner.Started >= 2);
        Volatile.Write(ref pressure, Healthy with { CpuPercent = 95, AvailablePhysicalBytes = 100UL << 20 });
        using var stop = new CancellationTokenSource();
        var second = consumer.ConsumeAsync(TaskFor(102, 1), stop.Token).AsTask();
        try
        {
        await UntilAsync(() => consumer.Admission.WaitingTasks == 1);
        Expect(proxies.Calls == 1, "no second IP while pressure or existing UV dispatch backlog");
        runner.AllowFinish.TrySetResult();
        Expect(await first.WaitAsync(TimeSpan.FromSeconds(8)), "IP-owning task finishes all 20 UVs under pressure");
        Expect(runner.Started == 20 && runner.Peak <= 2 && consumer.ActiveUvCount == 0, "hard limit includes complete UV lifecycle");
        var times = devices.Starts.Order().ToArray();
        Expect(times.Zip(times.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).All(ms => ms >= 25), "global UV interval preserved");
        Expect(!second.IsCompleted && proxies.Calls == 1, "new task still waits before IP acquisition");
        Volatile.Write(ref pressure, Healthy);
        Expect(await second.WaitAsync(TimeSpan.FromSeconds(5)) && proxies.Calls == 2, "recovered pressure allows next IP");
        Expect(consumer.Admission.ReservedSlots == 0 && consumer.Admission.WaitingTasks == 0, "task completion releases all reservations");
        }
        finally
        {
            stop.Cancel(); runner.AllowFinish.TrySetResult();
            try { await second; } catch (OperationCanceledException) { }
        }
        report.AppendLine("PASS 真实消费者：20 UV任务领IP后承压仍按间隔投递，新任务不提前领IP，峰值并发不超过2，恢复后继续");
    }

    private static async Task VerifyPendingRecoveryAsync(string root, ILogger logger, StringBuilder report)
    {
        var pressure = Healthy with { CpuPercent = 99 };
        var proxies = new CountingProxy();
        using var consumer = new BrowserTaskConsumer(new Devices(), proxies, new InstantRunner(),
            new BusinessOptions { SessionRoot = Path.Combine(root, "pending") }, logger, new(), () => pressure);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0 });
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
        report.AppendLine("PASS 准备阶段先等待资源，再落盘消费确认；等待中停止不领取IP，任务可恢复");
    }

    private static async Task VerifyFirstLaunchWindowAsync(string root, ILogger logger, StringBuilder report)
    {
        var proxies = new CountingProxy(); var devices = new Devices();
        using var consumer = new BrowserTaskConsumer(devices, proxies, new InstantRunner(),
            new BusinessOptions { SessionRoot = Path.Combine(root, "first-window") }, logger, new(), () => Healthy);
        consumer.Configure(new() { MaxConcurrency = 2, UvExecutionInterval = 500, IsProxyMode = true });
        Expect(await consumer.ConsumeAsync(TaskFor(301, 1), CancellationToken.None), "first task completed");
        using var stop = new CancellationTokenSource();
        var waiting = consumer.ConsumeAsync(TaskFor(302, 1), stop.Token).AsTask();
        await Task.Delay(100);
        Expect(proxies.Calls == 1 && !waiting.IsCompleted, "first launch interval waits before new IP acquisition");
        stop.Cancel();
        try { await waiting; throw new InvalidOperationException("Canceled first launch window executed"); }
        catch (OperationCanceledException) { }
        Expect(consumer.Admission.ReservedSlots == 0 && proxies.Calls == 1, "first-window cancellation releases reservation without consuming IP");
        Expect(await consumer.ConsumeAsync(TaskFor(303, 1), CancellationToken.None) && proxies.Calls == 2, "next window resumes");
        var starts = devices.Starts.Order().ToArray();
        Expect(Stopwatch.GetElapsedTime(starts[0], starts[1]) >= TimeSpan.FromMilliseconds(450), "cross-task first-UV interval");
        using var probe = new SystemPressureProbe();
        Expect(!probe.Read().Available, "native CPU sample needs a baseline");
        await Task.Delay(1050);
        var native = probe.Read();
        Expect(native.Available && native.CpuPercent is >= 0 and <= 100 && native.TotalPhysicalBytes > 0,
            "native memory and differential CPU sampling");
        report.AppendLine("PASS 首个UV全局间隔也在领IP前等待，等待取消不消耗IP；Windows CPU差分及内存采样");
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
        internal readonly TaskCompletionSource AllowFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Collections.Concurrent.ConcurrentQueue<long> Starts = new();
        private int _active; internal int Started, Peak;
        public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
        {
            Starts.Enqueue(Stopwatch.GetTimestamp()); Interlocked.Increment(ref Started);
            var active = Interlocked.Increment(ref _active);
            int peak; do { peak = Peak; if (peak >= active) break; } while (Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try { await AllowFinish.Task.WaitAsync(token); await Task.Delay(10, token); return true; }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
