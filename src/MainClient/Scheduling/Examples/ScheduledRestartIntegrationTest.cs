using System.Collections.Concurrent;
using System.Text;
using TrafficVista.Configuration;
using TrafficVista.Scheduling.Persistence;

namespace TrafficVista.Scheduling.Examples;

internal static class ScheduledRestartIntegrationTest
{
    internal static async Task RunAsync(StringBuilder report)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "restart-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(directory, "appsettings.json"));
        var settings = new TrafficVistaSettings(directory);
        Check(settings.CreateSchedulerOptions(settings.Value with { AutoRestartMinutes = 1 }).LocalCache is not null,
            "scheduled restart forces durable queue even in discard mode");
        var cache = new LocalTaskCacheOptions { FilePath = Path.Combine(directory, "pending.json"), LoadOnStart = true };
        var options = new SchedulerOptions { WorkerCount = 1, QueueCapacity = 2, LocalCache = cache,
            PollInterval = TimeSpan.FromMilliseconds(1), CompleteWhenSourceEmpty = true, StopTimeout = TimeSpan.FromMilliseconds(150) };
        var entered = Signal(); var release = Signal();
        var calls = 0; var executed = new ConcurrentQueue<int>();
        await using (var scheduler = new AsyncTaskScheduler<int>(_ =>
            ValueTask.FromResult<IReadOnlyList<int>>(Interlocked.Increment(ref calls) == 1 ? [1, 2, 3, 4, 5, 6] : []),
            async (item, token) => { executed.Enqueue(item); entered.TrySetResult(); await release.Task.WaitAsync(token); return true; },
            cacheKeySelector: item => item.ToString()))
        {
            await scheduler.StartAsync(options);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => scheduler.Snapshot.Queued == 2 && scheduler.Snapshot.Cached == 5);
            try { await scheduler.StopAsync(SchedulerStopMode.Suspend); throw new InvalidOperationException("active task should still be running"); }
            catch (TimeoutException) { }
            Check(!scheduler.Completion.IsCompleted && executed.Count == 1, "suspend waits for active work without consuming queue or canceling it");
            release.TrySetResult();
            var completed = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(completed.Succeeded == 1 && completed.Canceled == 0 && completed.Abandoned == 0 && completed.Cached == 5 && calls == 1,
                "queued and not-yet-enqueued batch items stay durable; active task completes once");
        }
        await using (var restored = new AsyncTaskScheduler<int>(_ => ValueTask.FromResult<IReadOnlyList<int>>([]),
            (item, _) => { executed.Enqueue(item); return ValueTask.FromResult(true); }, cacheKeySelector: item => item.ToString()))
        {
            await restored.StartAsync(options);
            var result = await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Restored == 5 && result.Succeeded == 5 && executed.Order().SequenceEqual(Enumerable.Range(1, 6)),
                "new run restores all five pending items and never repeats claimed task");
        }

        // A remote request already sent at the deadline must be allowed to return and become durable.
        var fetching = Signal(); var response = Signal(); var received = 0;
        await using (var scheduler = new AsyncTaskScheduler<int>(async token =>
        {
            fetching.TrySetResult(); await response.Task.WaitAsync(token); return new[] { 7, 8 };
        }, (_, _) => { received++; return ValueTask.FromResult(true); }, cacheKeySelector: item => item.ToString()))
        {
            await scheduler.StartAsync(options);
            await fetching.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = scheduler.StopAsync(SchedulerStopMode.Suspend);
            response.TrySetResult();
            await stopping;
            Check(received == 0 && scheduler.Snapshot.Cached == 2, "in-flight response saved before restart without being consumed");
        }
        await using (var restored = new AsyncTaskScheduler<int>(_ => ValueTask.FromResult<IReadOnlyList<int>>([]),
            (_, _) => { received++; return ValueTask.FromResult(true); }, cacheKeySelector: item => item.ToString()))
        {
            await restored.StartAsync(options);
            var result = await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(received == 2 && result.Restored == 2, "in-flight response recovered next run");
        }
        report.AppendLine("PASS 定时重启暂存：停止拉取和领取、满队列及批次余项落盘、在途任务不取消、跨轮次恢复不重复、在途接口响应落盘后恢复");
        await VerifyOneUseHandoffAsync(directory, report);
    }

    private static async Task VerifyOneUseHandoffAsync(string directory, StringBuilder report)
    {
        var root = Path.Combine(directory, "handoff");
        var original = RestartQueueFiles.Create(root);
        Check(!original.LoadOnStart, "ordinary start cannot load old restart queues");
        await using (var cache = await LocalTaskCache<int>.OpenAsync(original, item => item.ToString()))
            await cache.StageBatchAsync(new[] { 101, 102 });
        var token = RestartQueueFiles.Publish(original);
        var manual = RestartQueueFiles.Create(root);
        await using (var cache = await LocalTaskCache<int>.OpenAsync(manual, item => item.ToString()))
            Check(!cache.ReplayItems().Any(), "manual launch ignores published scheduled restart queue");
        var resumed = RestartQueueFiles.Create(root, token);
        Check(resumed.LoadOnStart && !Directory.Exists(Path.Combine(root, token + ".ready")), "load consumes original handoff atomically");
        try { RestartQueueFiles.Create(root, token); throw new InvalidOperationException("same handoff restored twice"); }
        catch (InvalidDataException) { }
        await using (var cache = await LocalTaskCache<int>.OpenAsync(resumed, item => item.ToString()))
        {
            Check(cache.ReplayItems().Order().SequenceEqual(new[] { 101, 102 }), "one-use handoff retains all pending items");
            await cache.ClaimAsync("101");
            await cache.FlushAsync();
        }
        Check(File.Exists(resumed.FilePath), "pending items retain their working snapshot after load");
        await using (var cache = await LocalTaskCache<int>.OpenAsync(resumed, item => item.ToString()))
        {
            Check(cache.ReplayItems().SequenceEqual(new[] { 102 }), "claimed items never replay");
            await cache.ClaimAsync("102");
            await cache.FlushAsync();
        }
        Check(!File.Exists(resumed.FilePath) && !File.Exists(resumed.FilePath + ".consumed.jsonl"), "empty working snapshot and journal are removed after safe close");
        report.AppendLine("PASS 定时重启专属队列：普通启动不加载、原暂存入口原子失效、同标识拒绝二次加载、未执行保留、全部领取收尾后删除正文及消费日志");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
}
