using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Consumers;

namespace TrafficVista.Business;

internal static class Http644IntegrationTest
{
    internal static async Task<string> RunAsync(ILogger logger)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "http644-tests", Guid.NewGuid().ToString("N"));
        var options = new BusinessOptions { ExportRunData = true, SessionRoot = root };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await VerifySequentialAsync(options, logger, token);
        await VerifyConcurrentAsync(options with { SessionRoot = Path.Combine(root, "concurrent") }, logger, token);
        await VerifyProxyFailuresAsync(options with { SessionRoot = Path.Combine(root, "proxy-failures") }, logger, token);
        await VerifyNavigationAsync(options, logger, token);
        return "PASS\n连续3次644停止该任务剩余UV；1/2次不中断，成功/其他错误清零，普通错误文本不误判。\n"
            + "等待名额和投递间隔可中断；已投UV继续完成，其他任务及相同ID的下一轮正常执行；未投数量和失败摘要准确，名额归零。\n"
            + "明确代理错误/407在代理模式下一次即停止后续，普通超时/重置/DNS/502/504及直连不误停。真实Chrome验证644/407/200和本地失效代理，状态穿过清理异常后仍可识别；无公网/供应商请求。\n输出目录：" + root;
    }

    private static async Task VerifySequentialAsync(BusinessOptions options, ILogger logger, CancellationToken token)
    {
        var runner = new ScriptRunner();
        using var consumer = new BrowserTaskConsumer(new Devices(), new NoProxy(), runner, options, logger);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0 });
        foreach (var (id, sequence, expected) in new[]
        {
            (1L, new[] { 644, 644, 644, 200, 200, 200 }, 3),
            (2L, new[] { 644, 644, 200, 644, 644, 644, 200 }, 6),
            (3L, new[] { 644, 644, 502, 644, 644, 200 }, 6),
            (4L, new[] { 644, 644, 0, 644, 644, 200 }, 6),
            (5L, new[] { 644, 200 }, 2),
            (6L, new[] { 644, 644 }, 2)
        })
        {
            runner.Script = (run, _) => Outcome(sequence[(int)run.UvId - 1]);
            var before = runner.Calls;
            Check(!await consumer.ConsumeAsync(Task(id, sequence.Length), token), "failed UV makes task fail");
            Check(runner.Calls - before == expected, "exact dispatch count for sequence " + id);
            var summary = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(options.SessionRoot, id.ToString()), "summary.json", SearchOption.AllDirectories).Single()));
            Check(summary.Value<int>("dispatchedUvs") == expected && summary.Value<int>("undispatchedUvs") == sequence.Length - expected, "summary counts " + id);
            Check((summary.Value<string>("failure") == "consecutive-http-644") == (expected < sequence.Length), "summary reason " + id);
            Check(consumer.ActiveUvCount == 0 && consumer.Admission.ReservedSlots == 0, "permits released " + id);
        }
        runner.Script = (_, _) => System.Threading.Tasks.Task.FromResult(true);
        Check(await consumer.ConsumeAsync(Task(1, 2), token), "same task ID in a new run does not inherit the streak");
    }

    private static async Task VerifyConcurrentAsync(BusinessOptions options, ILogger logger, CancellationToken token)
    {
        // Four sessions are already running; the fifth waits for its cadence. Three fail while the fourth stays alive.
        var starts = new ConcurrentDictionary<long, TaskCompletionSource>();
        var finishes = new ConcurrentDictionary<long, TaskCompletionSource>();
        for (long i = 1; i <= 4; i++) { starts[i] = Signal(); finishes[i] = Signal(); }
        var runner = new ScriptRunner
        {
            Script = async (run, runToken) =>
            {
                if (run.TaskId != 10) return true;
                Check(run.UvId <= 4, "no UV dispatched after the third 644");
                starts[run.UvId].TrySetResult();
                await finishes[run.UvId].Task.WaitAsync(runToken);
                return await Outcome(run.UvId <= 3 ? 644 : 200);
            }
        };
        using var consumer = new BrowserTaskConsumer(new Devices(), new NoProxy(), runner, options, logger);
        consumer.Configure(new() { MaxConcurrency = 4, UvExecutionInterval = 250 });
        var running = consumer.ConsumeAsync(Task(10, 10), token).AsTask();
        await starts[4].Task.WaitAsync(token);
        for (long i = 1; i <= 3; i++)
        {
            finishes[i].TrySetResult();
            await WaitAsync(() => consumer.ActiveUvCount == 4 - i, token);
        }
        Check(!running.IsCompleted, "already-dispatched fourth UV is allowed to finish");
        Check(await consumer.ConsumeAsync(Task(11, 1), token), "another task can continue while stopped task drains");
        finishes[4].TrySetResult();
        Check(!await running, "stopped task fails after in-flight UV completes");
        Check(consumer.ActiveUvCount == 0 && consumer.Admission.ReservedSlots == 0 && consumer.Admission.PendingUvs == 0, "no stranded permits or pending UVs");

        // Cancellation while the dispatcher is blocked on a UV permit must not cancel an active UV.
        using var guard = new UvDispatchGuard(token);
        using var permit = new SemaphoreSlim(0, 1);
        var waiting = permit.WaitAsync(guard.Token);
        for (var i = 0; i < 3; i++) guard.Record(false, new AggregateException(new PageHttpStatusException(644, "http://test/")), false);
        try { await waiting; throw new InvalidOperationException("dispatch wait did not cancel"); }
        catch (OperationCanceledException) when (guard.Stopped && !token.IsCancellationRequested) { }
    }

    private static async Task VerifyProxyFailuresAsync(BusinessOptions options, ILogger logger, CancellationToken token)
    {
        var runner = new ScriptRunner();
        using var consumer = new BrowserTaskConsumer(new Devices(), new LocalProxy("127.0.0.1:1"), runner, options, logger);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0, IsProxyMode = true });
        long id = 100;
        foreach (var code in ProxyUnavailableException.BrowserCodes.Append("HTTP407"))
        {
            var current = id++;
            runner.Script = (_, _) => System.Threading.Tasks.Task.FromException<bool>(new AggregateException(code == "HTTP407"
                ? new PageHttpStatusException(407, "http://fixture/") : new ProxyUnavailableException(code, new PlaywrightException("net::" + code))));
            var before = runner.Calls;
            Check(!await consumer.ConsumeAsync(Task(current, 5), token) && runner.Calls - before == 1, "single proxy failure stops: " + code);
            var summary = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(options.SessionRoot, current.ToString()), "summary.json", SearchOption.AllDirectories).Single()));
            Check(summary.Value<string>("failure") == "proxy-unavailable:" + code && summary.Value<int>("undispatchedUvs") == 4, "proxy reason and remaining count");
        }
        foreach (var code in new[] { "ERR_CONNECTION_RESET", "ERR_CONNECTION_REFUSED", "ERR_TIMED_OUT", "ERR_NAME_NOT_RESOLVED", "ERR_CONNECTION_TIMED_OUT" })
        {
            Check(ProxyUnavailableException.BrowserCode("net::" + code) is null, "ambiguous browser error is not proxy-specific");
            runner.Script = (_, _) => System.Threading.Tasks.Task.FromException<bool>(new PlaywrightException("net::" + code));
            var before = runner.Calls;
            Check(!await consumer.ConsumeAsync(Task(id++, 2), token) && runner.Calls - before == 2, "ordinary error continues " + code);
        }
        foreach (var status in new[] { 502, 504 })
        {
            runner.Script = (_, _) => Outcome(status);
            var before = runner.Calls;
            Check(!await consumer.ConsumeAsync(Task(id++, 2), token) && runner.Calls - before == 2, "gateway status alone continues");
        }
        using var direct = new BrowserTaskConsumer(new Devices(), new NoProxy(), runner, options with { SessionRoot = options.SessionRoot + "-direct" }, logger);
        direct.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0 });
        runner.Script = (_, _) => System.Threading.Tasks.Task.FromException<bool>(new ProxyUnavailableException("ERR_PROXY_CONNECTION_FAILED", new Exception("fixture")));
        var directBefore = runner.Calls;
        Check(!await direct.ConsumeAsync(Task(id++, 2), token) && runner.Calls - directBefore == 2, "direct mode never trips proxy stop rule");
        runner.Script = (_, _) => System.Threading.Tasks.Task.FromResult(true);
        Check(await consumer.ConsumeAsync(Task(id++, 2), token), "next task gets a fresh guard");
        Check(consumer.Admission.ReservedSlots == 0 && direct.Admission.ReservedSlots == 0, "proxy stop releases permits");
    }

    private static async Task VerifyNavigationAsync(BusinessOptions options, ILogger logger, CancellationToken token)
    {
        var executable = Environment.GetEnvironmentVariable("TRAFFICVISTA_TEST_CHROME")
            ?? Path.GetFullPath(new BusinessOptions().ChromeExecutablePath, AppContext.BaseDirectory);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.FulfillAsync(new()
        {
            Status = route.Request.Url.EndsWith("/644") ? 644 : route.Request.Url.EndsWith("/407") ? 407 : 200,
            ContentType = "text/html", Body = "<html><body>fixture</body></html>"
        }));
        var started = Stopwatch.GetTimestamp();
        try { await InitialPageNavigation.OpenAsync(page, "http://fixture.test/644", logger, 1, 1, token); throw new InvalidOperationException("644 accepted"); }
        catch (PageHttpStatusException ex) when (ex.Status == 644) { }
        Check(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10), "644 does not wait for the 30-second empty-page timeout");
        try { await InitialPageNavigation.OpenAsync(page, "http://fixture.test/407", logger, 1, 1, token); throw new InvalidOperationException("407 accepted"); }
        catch (PageHttpStatusException ex) when (ex.Status == 407) { }
        Check((await InitialPageNavigation.OpenAsync(page, "http://fixture.test/ok", logger, 1, 2, token)).Status == 200, "200 still succeeds");
        await browser.CloseAsync();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var actual = options with { ChromeExecutablePath = executable, SessionRoot = Path.Combine(options.SessionRoot, "actual-proxy"), StaticCacheRoot = Path.Combine(options.SessionRoot, "actual-cache") };
        using var launches = new BrowserLaunchLimiter(1);
        using var cache = new SharedStaticResourceCache(actual, logger);
        var runner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, business: actual);
        using var consumer = new BrowserTaskConsumer(new Devices(), new LocalProxy("127.0.0.1:" + port), runner, actual, logger);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0, IsProxyMode = true, IsHiddenMode = true, PageLoadingTimeout = 10 });
        Check(!await consumer.ConsumeAsync(Task(999, 5), token), "real refused proxy fails");
        var summary = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(actual.SessionRoot, "999"), "summary.json", SearchOption.AllDirectories).Single()));
        Check(summary.Value<int>("dispatchedUvs") == 1 && summary.Value<int>("undispatchedUvs") == 4
            && summary.Value<string>("failure")!.StartsWith("proxy-unavailable:ERR_PROXY_CONNECTION_FAILED"), "real browser error survives aggregate cleanup and stops next UV");
        Check(consumer.ActiveUvCount == 0 && consumer.Admission.ReservedSlots == 0 && ChromeProcessJob.OwnedProcessCount == 0, "real proxy failure cleans browsers and permits");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async System.Threading.Tasks.Task WaitAsync(Func<bool> ready, CancellationToken token)
    { while (!ready()) await System.Threading.Tasks.Task.Delay(5, token); }
    private static System.Threading.Tasks.Task<bool> Outcome(int status) => status switch
    {
        200 => System.Threading.Tasks.Task.FromResult(true),
        0 => System.Threading.Tasks.Task.FromException<bool>(new InvalidOperationException("non-status error text HTTP=644")),
        _ => System.Threading.Tasks.Task.FromException<bool>(new AggregateException("cleanup wrapper", new PageHttpStatusException(status, "http://test/")))
    };
    private static TaskEnvelope Task(long id, int uvs) => new(id, "644 fixture", "http://fixture.test/", DateTime.UtcNow)
    { Payload = new JObject { ["uv"] = uvs, ["pv"] = 1, ["sleep"] = "0-0" } };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("644 regression: " + message); }
    private sealed class ScriptRunner : IBusinessUvRunner
    {
        internal int Calls;
        internal Func<BusinessUvRun, CancellationToken, System.Threading.Tasks.Task<bool>> Script = (_, _) => System.Threading.Tasks.Task.FromResult(true);
        public System.Threading.Tasks.Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Script(run, token); }
    }
    private sealed class LocalProxy(string server) : IBusinessProxyProvider
    { public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(new(server)); }
    private sealed class NoProxy : IBusinessProxyProvider
    { public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(null); }
    private sealed class Devices : IBusinessDeviceProvider
    {
        public ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token)
            => ValueTask.FromResult(new BusinessDeviceProfile("644-fixture", "Test", "Offline", "", "14", 1080, 2400, "test", 8, "", "", null));
    }
}
