using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TrafficVista.Business;
using TrafficVista.Configuration;
using TrafficVista.DesktopIntegration;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Producers;

namespace TrafficVista.Scheduling.Examples;

internal static class TaskFetchDiagnosticsTest
{
    internal static async Task RunAsync(string directory)
    {
        var sink = new Events();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        await VerifyHttpAsync(directory, logger, sink);
        await VerifyProducerAsync(logger, sink);
    }

    private static async Task VerifyHttpAsync(string directory, ILogger logger, Events sink)
    {
        var headerEntered = Signal();
        var bodyEntered = Signal();
        var bodyRelease = Signal();
        var calls = 0;
        using var client = new HttpClient(new Handler(async token =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 2: return Json("{\"success\":true,\"data\":[]}");
                case 3: return Json("{\"success\":false,\"data\":[],\"message\":\"没有可用任务\"}");
                case 4: return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                case 5:
                    headerEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("unreachable");
                case 6:
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new DelayedBody(bodyEntered, bodyRelease))
                    };
                case 7: return Json("{invalid");
                default: return Json(TaskJson);
            }
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpTaskSource(client, logger, new BusinessOptions { RecoveryRoot = Path.Combine(directory, "fetch-recovery") });
        var options = new TrafficVistaOptions { TaskApiUrl = "http://fixture.invalid/tasks?token=diagnostic-secret", TaskName = "诊断测试" };
        source.Configure(options);
        await source.FetchAsync(CancellationToken.None);
        await source.FetchAsync(CancellationToken.None);
        await source.FetchAsync(CancellationToken.None);
        try { await source.FetchAsync(CancellationToken.None); throw new InvalidOperationException("HTTP failure hidden"); }
        catch (HttpRequestException) { }
        using (var stop = new CancellationTokenSource())
        {
            var pending = source.FetchAsync(stop.Token);
            await headerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Expect(source.Snapshot.InFlight && source.Snapshot.Stage == "等待响应头", "header wait visible");
            stop.Cancel();
            try { await pending; throw new InvalidOperationException("Cancellation hidden"); }
            catch (OperationCanceledException) { }
        }
        var body = source.FetchAsync(CancellationToken.None);
        await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { Expect(source.Snapshot.InFlight && source.Snapshot.Stage == "读取响应正文", "body wait visible"); }
        finally { bodyRelease.SetResult(); }
        await body;
        try { await source.FetchAsync(CancellationToken.None); throw new InvalidOperationException("Invalid JSON hidden"); }
        catch (Newtonsoft.Json.JsonException) { }
        await source.FetchAsync(CancellationToken.None);
        var stats = source.Snapshot;
        Expect(stats.Requests == 8 && stats.Responses == 5 && stats.EmptyResponses == 2
            && stats.NonEmptyResponses == 3 && stats.ReceivedTasks == 3
            && stats.FailedRequests == 2 && stats.InterruptedRequests == 1, "HTTP counters reconcile before Multiple expansion");
        Expect(!stats.InFlight && stats.LastTaskCount == 1 && stats.LastHttpStatus == 200
            && stats.LastDurationMs.HasValue && stats.MaxDurationMs >= stats.LastDurationMs
            && stats.SecondsSinceLastRequest.HasValue && stats.SecondsSinceLastResponse.HasValue && stats.LastError is null,
            "last result, ages and duration tracked");
        var text = SchedulingDiagnosticsForm.BuildFetchDiagnostics(new(), stats);
        Expect(text.Contains("请求8") && text.Contains("空响应2") && text.Contains("尚无记录") == false, "diagnostics render HTTP totals");
        Expect(sink.Values.Count(e => e.MessageTemplate.Text.StartsWith("任务接口请求开始")) == 8
            && !sink.Values.Any(e => e.RenderMessage().Contains("diagnostic-secret")), "request logs visible at Information without URL secrets");
        source.Configure(options);
        Expect(source.Snapshot.Requests == 0 && source.Snapshot.LastTaskCount is null
            && source.Snapshot.SecondsSinceLastRequest is null, "new run clears all fetch diagnostics");
    }

    private static async Task VerifyProducerAsync(ILogger logger, Events sink)
    {
        var fetched = 0;
        await using (var scheduler = new AsyncTaskScheduler<int>(new DelegateTaskProducer<int>(_ =>
            ValueTask.FromResult<IReadOnlyList<int>>(Interlocked.Increment(ref fetched) == 1 ? [1, 2, 3, 4] : [])),
            new DelegateTaskConsumer<int>(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }), logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, QueueCapacity = 1 });
            await UntilAsync(() => scheduler.Snapshot.Producer.Stage == "等待批次入队空位");
            Expect(fetched == 1 && scheduler.Snapshot.Queued == 1 && scheduler.Snapshot.Producer.NextFetchInSeconds is null,
                "backpressure visible without changing fetch behavior");
            await scheduler.StopAsync();
            Expect(scheduler.Snapshot.Producer.Stage == "已停止获取任务", "stop clears producer wait state");
        }
        var release = Signal();
        fetched = 0;
        await using (var scheduler = new AsyncTaskScheduler<int>(new DelegateTaskProducer<int>(_ =>
            ValueTask.FromResult<IReadOnlyList<int>>(Interlocked.Increment(ref fetched) == 1 ? [1, 2] : [])),
            new DelegateTaskConsumer<int>(async (item, token) =>
            {
                if (item == 1) await release.Task.WaitAsync(token);
                else await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }), logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, QueueCapacity = 1, PollInterval = TimeSpan.Zero });
            await UntilAsync(() => scheduler.Snapshot.Producer.Stage == "等待队列空位");
            Expect(fetched == 1, "full queue pauses next HTTP request");
            release.SetResult();
            await UntilAsync(() => fetched == 2);
            await scheduler.StopAsync();
        }
        await using (var scheduler = new AsyncTaskScheduler<int>(new DelegateTaskProducer<int>(_ =>
            ValueTask.FromResult<IReadOnlyList<int>>([])), new DelegateTaskConsumer<int>((_, _) => ValueTask.FromResult(true)), logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, EmptyPollInterval = TimeSpan.FromSeconds(5) });
            await UntilAsync(() => scheduler.Snapshot.Producer.Stage == "轮询间隔等待");
            var before = scheduler.Snapshot.Producer.NextFetchInSeconds;
            await Task.Delay(40);
            Expect(before is > 0 && scheduler.Snapshot.Producer.NextFetchInSeconds < before, "poll countdown decreases");
            await scheduler.StopAsync();
            Expect(scheduler.Snapshot.Producer.NextFetchInSeconds is null, "cancel clears countdown");
        }
        await using (var scheduler = new AsyncTaskScheduler<int>(new DelegateTaskProducer<int>(async token =>
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Array.Empty<int>(); }),
            new DelegateTaskConsumer<int>((_, _) => ValueTask.FromResult(true)), logger: logger))
        {
            await scheduler.StartAsync(new() { WorkerCount = 1, FetchTimeout = TimeSpan.FromMilliseconds(60),
                SourceRetryDelay = TimeSpan.FromSeconds(5) });
            await UntilAsync(() => scheduler.Snapshot.Producer.Stage == "失败重试等待");
            var snapshot = scheduler.Snapshot;
            Expect(snapshot.SourceTimeouts == 1 && snapshot.SourceErrors == 1 && snapshot.TimedOut == 0
                && snapshot.Producer.NextFetchInSeconds is > 0, "fetch timeout separated from execution timeout");
            var text = SchedulingDiagnosticsForm.BuildFetchDiagnostics(snapshot, new());
            Expect(text.Contains("拉取超时1") && text.Contains("失败重试等待"), "retry reason and timeout visible");
            await scheduler.StopAsync();
        }
        Expect(sink.Values.Any(e => e.RenderMessage().Contains("当前批次入队等待"))
            && sink.Values.Any(e => e.RenderMessage().Contains("任务拉取暂停：队列已满"))
            && sink.Values.Any(e => e.RenderMessage().Contains("恢复拉取"))
            && sink.Values.Any(e => e.RenderMessage().Contains("接口无任务"))
            && sink.Values.Any(e => e.RenderMessage().Contains("等待重试")), "backpressure, empty polls and retry delays logged");
    }

    private const string TaskJson = "{\"success\":true,\"data\":[{\"id\":1,\"title\":\"fixture\",\"url\":\"http://fixture.invalid/page\",\"uv\":1}]}";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Expect(bool value, string message) { if (!value) throw new InvalidOperationException("Fetch diagnostics: " + message); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fetch diagnostics fixture"); await Task.Delay(5); }
    }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }
    private sealed class DelayedBody(TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream(Encoding.UTF8.GetBytes(TaskJson))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await base.ReadAsync(buffer, token);
        }
    }
    private sealed class Events : ILogEventSink
    {
        internal readonly ConcurrentQueue<LogEvent> Values = new();
        public void Emit(LogEvent logEvent) => Values.Enqueue(logEvent);
    }
}
