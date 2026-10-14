using System.Text;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Scheduling.Producers;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Contracts;

namespace TrafficVista.Scheduling.Examples;

internal static class SchedulingIntegrationTest
{
    public static async Task<string> RunAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "scheduler-tests", DateTime.UtcNow.Ticks.ToString());
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(directory, "appsettings.json"));
        var settings = new TrafficVistaSettings(directory);
        var handler = new FakeTaskHandler();
        using var client = new HttpClient(handler);
        var source = new HttpTaskSource(client, Log.Logger);
        await using var scheduler = new TaskSchedulingService(Log.Logger, settings, source,
            new DelegateTaskConsumer<TaskEnvelope>((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }));
        var ui = settings.Value with
        {
            TaskApiUrl = "http://127.0.0.1:6617/client-v5.php?action=getTask&name=old&other=keep",
            TaskName = "北京 test&1", TaskFetchInterval = 20, MaxConcurrency = 2
        };
        var options = settings.CreateSchedulerOptions(ui) with { StopTimeout = TimeSpan.FromSeconds(5) };
        await scheduler.StartAsync(ui, options);
        var rejected = false;
        try { await scheduler.StartAsync(ui, options); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("调度重复启动未被拒绝。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (handler.Calls < 6 || scheduler.Snapshot.Submitted < 1) await Task.Delay(10, deadline.Token);
        if (!scheduler.IsRunning || scheduler.Completion.IsCompleted || scheduler.Snapshot.SourceErrors != 0)
            throw new InvalidOperationException("空响应后没有继续取任务。");
        var uri = handler.LastUri!.AbsoluteUri;
        if (!uri.Contains("action=getTask") || !uri.Contains("other=keep") || uri.Contains("name=old")
            || !uri.Contains("name=" + Uri.EscapeDataString(ui.TaskName)) || uri.Split("name=").Length != 2
            || uri.Split("action=").Length != 2)
            throw new InvalidOperationException("任务名称参数构造错误。");
        await scheduler.StopAsync(SchedulerStopMode.Cancel);
        var final = await scheduler.Completion;
        if (scheduler.IsRunning || final.Running != 0 || final.Queued != 0
            || final.Submitted != final.Completed + final.Canceled + final.Abandoned)
            throw new InvalidOperationException("Cancel 后统计错误。");
        var stoppedCalls = handler.Calls;
        await Task.Delay(70);
        if (handler.Calls != stoppedCalls) throw new InvalidOperationException("停止后仍在取任务。");
        await scheduler.StartAsync(ui, options);
        while (handler.Calls <= stoppedCalls) await Task.Delay(10, deadline.Token);
        await scheduler.StopAsync(SchedulerStopMode.Drain);
        if (scheduler.IsRunning) throw new InvalidOperationException("重启后 Drain 失败。");
        var tasks = HttpTaskSource.ParseResponse(Encoding.UTF8.GetBytes(FakeTaskHandler.TaskJson));
        if (tasks.Count != 1 || tasks[0].Id != 43135 || tasks[0].Payload!.Value<int>("uv") != 12)
            throw new InvalidOperationException("任务解析或原始字段保留失败。");
        return "PASS\nGET/name 参数编码、有任务解析、原始字段保留、无任务持续轮询、重复启动拒绝、Cancel/Drain、停止后不再拉取和重启通过。\n使用本地模拟接口及空业务消费者；不启动浏览器。";
    }

    private sealed class FakeTaskHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Uri? LastUri { get; private set; }
        public const string TaskJson = """
            {"success":true,"data":[{"id":43135,"title":"本地 H5 测试","url":"http://127.0.0.1:6617/beijing.html","uv":12,"pv":1,"sleep":"5-5"}]}
            """;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            LastUri = request.RequestUri;
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("请求不是 GET。");
            await Task.Delay(5, token);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(call == 3 ? TaskJson :
                    "{\"success\":false,\"data\":[],\"message\":\"没有可用任务\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
