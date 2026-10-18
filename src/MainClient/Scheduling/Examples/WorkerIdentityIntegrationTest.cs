using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TrafficVista.Business;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Producers;
using TrafficVista.UiLogging;

namespace TrafficVista.Scheduling.Examples;

internal static class WorkerIdentityIntegrationTest
{
    internal static async Task RunAsync(string directory)
    {
        var events = new Events();
        using var logger = new LoggerConfiguration().Enrich.With(new WorkerIdEnricher()).WriteTo.Sink(events).CreateLogger();
        var originals = Enumerable.Range(1, 9).Select(id => TaskFor(id) with { WorkerId = 999 }).ToArray();
        var active = new ConcurrentDictionary<int, int>();
        var assigned = new ConcurrentBag<int>();
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        await using (var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(_ =>
        {
            Check(WorkerExecutionContext.WorkerId == 0, "producer is zero");
            logger.Information("身份验证：来源 ExpectedWorker={ExpectedWorker}", 0);
            return ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(++fetches == 1 ? originals : []);
        }), new DelegateTaskConsumer<TaskEnvelope>(async (task, token) =>
        {
            var workerId = task.WorkerId;
            Check(workerId is >= 1 and <= 3 && WorkerExecutionContext.WorkerId == workerId, "task assigned from worker range");
            Check(active.AddOrUpdate(workerId, 1, (_, value) => value + 1) == 1, "one task per worker");
            try
            {
                assigned.Add(workerId);
                if (Interlocked.Increment(ref entered) == 3) allEntered.TrySetResult();
                await allEntered.Task.WaitAsync(token).WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Yield();
                await Task.Run(() =>
                {
                    Check(WorkerExecutionContext.WorkerId == workerId, "worker survives async/task continuation");
                    logger.Information("身份验证：消费 TaskId={TaskId} ExpectedWorker={ExpectedWorker}", task.Id, workerId);
                }, token);
                await Task.Delay(10, token);
                if (task.Id == 5) throw new IOException("worker identity fixture failure");
                return true;
            }
            finally { active.AddOrUpdate(workerId, 0, (_, value) => value - 1); }
        }), logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 3, QueueCapacity = 2, PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
            var result = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            Check(result.Completed == 9 && result.Failed == 1 && result.State == SchedulerState.Stopped, "worker retained after task failure");
        }
        Check(assigned.Distinct().Order().SequenceEqual(new[] { 1, 2, 3 }) && active.Values.All(value => value == 0), "fixed worker identities and serial consumption");
        Check(originals.All(task => task.WorkerId == 999), "assignment does not mutate queued deliveries");
        var pendingJson = JsonConvert.SerializeObject(originals[0]);
        Check(!JObject.Parse(pendingJson).ContainsKey("WorkerId") && JsonConvert.DeserializeObject<TaskEnvelope>(pendingJson)!.WorkerId == 0,
            "pending-task persistence does not retain stale worker identities");

        var business = new BusinessOptions { ExportRunData = true, SessionRoot = Path.Combine(directory, "worker-identity") };
        var runner = new Runner(logger);
        using var consumer = new BrowserTaskConsumer(new Devices(), new Proxy(), runner, business, logger);
        consumer.Configure(new() { MaxConcurrency = 3, UvExecutionInterval = 0 });
        fetches = 0;
        await using (var scheduler = new AsyncTaskScheduler<TaskEnvelope>(new DelegateTaskProducer<TaskEnvelope>(_ =>
            ValueTask.FromResult<IReadOnlyList<TaskEnvelope>>(++fetches == 1 ? originals.Take(3).ToArray() : [])), consumer, logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 3, QueueCapacity = 2, PollInterval = TimeSpan.Zero, CompleteWhenSourceEmpty = true });
            var result = await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            Check(result.Succeeded == 3 && runner.Calls == 6, "UVs inherit assigned task worker");
        }
        foreach (var taskFile in Directory.EnumerateFiles(business.SessionRoot, "task.json", SearchOption.AllDirectories))
        {
            var task = JObject.Parse(File.ReadAllText(taskFile));
            var summary = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(taskFile)!, "summary.json")));
            Check(task.Value<int>("workerId") is >= 1 and <= 3 && task.Value<int>("workerId") == summary.Value<int>("workerId"), "task and summary expose runtime worker");
        }
        Check(WorkerExecutionContext.WorkerId == 0, "worker scope does not leak into caller");
        logger.Information("身份验证：界面 ExpectedWorker={ExpectedWorker}", 0);
        foreach (var entry in events.Values.Where(entry => entry.Properties.ContainsKey("ExpectedWorker")))
            Check(entry.Properties["WorkerId"].ToString() == entry.Properties["ExpectedWorker"].ToString(), "logs retain exact worker identity");
    }

    private static TaskEnvelope TaskFor(int id) => new(id, "identity", "http://fixture.invalid/", DateTime.UtcNow)
        { Payload = new JObject { ["uv"] = 2, ["pv"] = 1, ["sleep"] = 0 } };
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Worker identity: " + message); }
    private sealed class Events : ILogEventSink
    {
        internal readonly ConcurrentQueue<LogEvent> Values = new();
        public void Emit(LogEvent logEvent) => Values.Enqueue(logEvent);
    }
    private sealed class Runner(ILogger logger) : IBusinessUvRunner
    {
        internal int Calls;
        public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
        {
            await Task.Delay(10, token);
            Check(run.WorkerId is >= 1 and <= 3 && run.WorkerId == WorkerExecutionContext.WorkerId, "UV metadata and async scope agree");
            logger.Information("身份验证：UV ExpectedWorker={ExpectedWorker}", run.WorkerId);
            Interlocked.Increment(ref Calls);
            return true;
        }
    }
    private sealed class Devices : IBusinessDeviceProvider
    {
        public ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token)
            => ValueTask.FromResult(new BusinessDeviceProfile("identity", "test", "test", "", "14", 1080, 2400, "test", 8, "", "", null));
    }
    private sealed class Proxy : IBusinessProxyProvider
    {
        public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1)
            => ValueTask.FromResult<BusinessProxy?>(null);
    }
}
