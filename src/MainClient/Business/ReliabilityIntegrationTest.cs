using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Net;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;
using TrafficVista.Scheduling;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Persistence;

namespace TrafficVista.Business;

/// <summary>Offline regressions and real-Chrome benchmark. No production API is contacted.</summary>
internal static class ReliabilityIntegrationTest
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static readonly string DeviceJson = """{"success":true,"data":[{"ua":"Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/145.0.0.0 Mobile Safari/537.36","sw":1080,"sh":2400,"cpu":8,"make":"Test","model":"Offline"}]}""";

    internal static async Task<string> RunBrowserCloseAsync(ILogger logger)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "browser-close", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new TestServer();
        using var playwright = await Playwright.CreateAsync();
        var business = new BusinessOptions { ExportRunData = true, StaticCacheRoot = Path.Combine(root, "cache"),
            ChromeExecutablePath = Environment.GetEnvironmentVariable("TRAFFICVISTA_TEST_CHROME")
                ?? Path.Combine(AppContext.BaseDirectory, "File/chrome-win/145.0.7632.110/chrome.exe") };
        using var launches = new BrowserLaunchLimiter(1);
        using var cache = new SharedStaticResourceCache(business, logger);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var metrics = new BusinessMetrics();
        var runner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, metrics, business);
        var report = new StringBuilder("PASS\n");
        foreach (var scenario in new[] { "pageDelay", "dwell", "browser", "cancel", "normal" })
        {
            var directory = Path.Combine(root, scenario);
            Directory.CreateDirectory(directory);
            using var requested = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var options = new TrafficVistaOptions { IsHiddenMode = true, PageLoadingTimeout = 20,
                PageLoadingDelay = scenario == "pageDelay" ? 60m : 0m };
            var run = new BusinessUvRun(1, 1, new(server.Url + "page"), directory, TestDevice(), null, options,
                scenario is "pageDelay" or "normal" ? TimeSpan.Zero : TimeSpan.FromSeconds(60), 1, null);
            var running = runner.RunAsync(run, requested.Token);
            if (scenario != "normal")
            {
                var portFile = Path.Combine(directory, "chrome-profile", "DevToolsActivePort");
                string[] endpoint = [];
                await WaitUntilAsync(() =>
                {
                    try { if (File.Exists(portFile)) endpoint = File.ReadAllLines(portFile); }
                    catch (IOException) { }
                    return endpoint.Length >= 2;
                });
                var baseUrl = "http://127.0.0.1:" + endpoint[0];
                string? pageId = null;
                using var discover = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (pageId is null)
                {
                    var list = JArray.Parse(await client.GetStringAsync(baseUrl + "/json/list", discover.Token));
                    pageId = list.OfType<JObject>().FirstOrDefault(t => t.Value<string>("type") == "page"
                        && t.Value<string>("url") == run.Target.AbsoluteUri)?.Value<string>("id");
                    if (pageId is null) await Task.Delay(50, discover.Token);
                }
                // 本地页面提交后留出DOM检查时间，确保关闭发生于普通60秒等待中。
                await Task.Delay(1500);
                var closeTime = Stopwatch.StartNew();
                if (scenario == "cancel") requested.Cancel();
                else if (scenario == "browser")
                {
                    using var socket = new System.Net.WebSockets.ClientWebSocket();
                    await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + endpoint[0] + endpoint[1]), CancellationToken.None);
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}"),
                        System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else await client.GetStringAsync(baseUrl + "/json/close/" + pageId);
                try { await running.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("关闭后的UV不应成功：" + scenario); }
                catch (OperationCanceledException) when (scenario == "cancel" && requested.IsCancellationRequested) { }
                catch (AggregateException) when (scenario != "cancel") { }
                Expect(closeTime.Elapsed < TimeSpan.FromSeconds(10), "close should interrupt long wait");
                report.AppendLine($"PASS {scenario} 关闭/取消后完成清理：{closeTime.Elapsed.TotalMilliseconds:F0}ms（原等待60秒）");
            }
            else Expect(await running, "normal cleanup must not be classified as unexpected closure");
            var result = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
            Expect(result.Value<bool>("browserInterrupted") == (scenario is not ("cancel" or "normal")), "manual closure classification");
            Expect(result.Value<bool>("canceled") == (scenario == "cancel"), "external cancellation classification");
            Expect(result.Value<bool>("success") == (scenario == "normal"), "success classification");
            if (scenario is "pageDelay" or "dwell")
                Expect(result.Value<string>("failureStage") == scenario, "closure must occur during expected long wait");
            Expect(metrics.Snapshot.ActiveBrowsers == 0 && ChromeProcessJob.OwnedProcessCount == 0, "closed browser releases metrics and process job");
            Expect(!Directory.Exists(Path.Combine(directory, "chrome-profile")) || result.Value<bool>("profileCleanupDeferred"), "managed profile cleanup");
        }
        var noExport = business with { ExportRunData = false, SessionRoot = Path.Combine(root, "no-export") };
        var memoryRunner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, metrics, noExport);
        using (var consumer = new BrowserTaskConsumer(new FakeDevices(), new FixedProxy(), memoryRunner, noExport, logger, metrics))
        {
            consumer.Configure(new() { MaxConcurrency = 1, IsHiddenMode = true, PageLoadingDelay = 0, UvExecutionInterval = 0 });
            var task = TaskFor(server.Url + "page", 1, 2);
            Expect(await consumer.ConsumeAsync(task, CancellationToken.None), "no-export real browser with repeated PV");
            Expect(!Directory.Exists(noExport.SessionRoot), "disabled export must not create business archives");
            var tempTask = Path.Combine(Path.GetTempPath(), "TrafficVista", "uv", task.Id.ToString());
            Expect(!Directory.Exists(tempTask) || !Directory.EnumerateFileSystemEntries(tempTask).Any(), "temporary run must be removed");
            Expect(metrics.Snapshot.ActiveBrowsers == 0 && ChromeProcessJob.OwnedProcessCount == 0, "no-export browser cleanup");
        }
        report.AppendLine("PASS 关闭导出时真实Chrome完成两个PV，不创建业务归档，临时运行目录及进程清理完成。");
        report.AppendLine("PASS 正常关闭不误报；手动页面关闭/浏览器退出计为当前UV失败，外部取消保持取消分类；进程/浏览器计数归零。仅使用本地页面及本测试创建的Chrome。");
        return report.ToString();
    }


    internal static async Task<string> RunAsync(ILogger logger, bool benchmark = false)
    {
        var report = new StringBuilder();
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", benchmark ? "benchmark" : "reliability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var server = new TestServer();
        var business = new BusinessOptions
        {
            ExportRunData = true,
            SessionRoot = Path.Combine(directory, "tasks"), StaticCacheRoot = Path.Combine(directory, "cache"),
            RecoveryRoot = Path.Combine(directory, "recovery"),
            ChromeExecutablePath = Environment.GetEnvironmentVariable("TRAFFICVISTA_TEST_CHROME")
                ?? Path.Combine(AppContext.BaseDirectory, "File/chrome-win/145.0.7632.110/chrome.exe")
        };
        if (!benchmark)
        {
            await VerifyNetAsync(server, logger, report);
            await VerifyAdmissionAsync(report);
            await VerifyPersistenceAsync(directory, report);
            await VerifyCacheAsync(business, logger, report);
            await VerifyConsumerAsync(business, logger, report);
            await VerifyRetentionAsync(directory, logger, report);
            var uri = Scheduling.Producers.HttpTaskSource.BuildRequestUri(new TrafficVistaOptions
                { TaskApiUrl = server.Url + "?ACTION=wrong&name=old&keep=yes&action=other", TaskName = "北京&1" });
            Expect(uri.Query.Split("action=").Length == 2 && !uri.Query.Contains("wrong") && uri.Query.Contains("keep=yes"), "action/name normalization");
            report.AppendLine("PASS action/name 唯一参数及原有其他参数保留");
        }

        using var playwright = await Playwright.CreateAsync();
        if (!File.Exists(business.ChromeExecutablePath)) throw new FileNotFoundException("离线浏览器验证需要本地 Chrome；设置 TRAFFICVISTA_TEST_CHROME。", business.ChromeExecutablePath);
        using var launches = new BrowserLaunchLimiter(4);
        using var cache = new SharedStaticResourceCache(business, logger);
        var metrics = new BusinessMetrics();
        var runner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, metrics, business);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var devices = new HttpDeviceFingerprintProvider(client, logger);
        var options = new TrafficVistaOptions { MaxConcurrency = 2, IsHiddenMode = true,
            PageLoadingTimeout = 60, UvExecutionInterval = 0, DevApiUrl = server.Url + "devices", IsCacheMode = true };

        if (benchmark)
        {
            var rows = new JArray();
            foreach (var concurrency in new[] { 1, 2, 4, 8 })
            {
                var measured = new BusinessMetrics();
                var measuredRunner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, measured, business);
                using var consumer = new BrowserTaskConsumer(devices, new NoProxy(), measuredRunner, business, logger, measured);
                consumer.Configure(options with { MaxConcurrency = concurrency, IsCacheMode = false });
                var stopwatch = Stopwatch.StartNew();
                var before = GC.GetTotalAllocatedBytes();
                long peakMainBytes = 0;
                using var sampling = new CancellationTokenSource();
                var sample = Task.Run(async () =>
                {
                    using var current = Process.GetCurrentProcess();
                    try
                    {
                        while (true)
                        {
                            current.Refresh(); peakMainBytes = Math.Max(peakMainBytes, current.WorkingSet64);
                            await Task.Delay(50, sampling.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                var success = false;
                try { success = await consumer.ConsumeAsync(TaskFor(server.Url + "page", 8, 1), CancellationToken.None); }
                finally { sampling.Cancel(); await sample; }
                Expect(success && measured.Snapshot.ActiveBrowsers == 0, "benchmark cleanup");
                var row = JObject.FromObject(measured.Snapshot);
                row["concurrency"] = concurrency; row["elapsedSeconds"] = stopwatch.Elapsed.TotalSeconds;
                row["completedUvsPerMinute"] = 8 * 60 / stopwatch.Elapsed.TotalSeconds;
                row["mainProcessPeakMiB"] = peakMainBytes / 1048576.0;
                row["managedAllocatedMiB"] = (GC.GetTotalAllocatedBytes() - before) / 1048576.0;
                rows.Add(row);
                report.AppendLine($"并发 {concurrency}: 8 UV，{stopwatch.Elapsed.TotalSeconds:F2}s，{8 * 60 / stopwatch.Elapsed.TotalSeconds:F1} UV/min，P95={measured.Snapshot.UvP95Ms:F0}ms，峰值浏览器={measured.Snapshot.PeakBrowsers}");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "benchmark.json"), rows.ToString());
            report.AppendLine("本地零停留页面；主进程内存为工作集，PeakSessionBrowserCommittedMiB 为单 UV 整个 Chrome Job 的峰值提交内存，均不代表全机总内存。不能外推真实网络吞吐。");
        }
        else
        {
            using var consumer = new BrowserTaskConsumer(devices, new NoProxy(), runner, business, logger, metrics);
            // 首次导航按DOM就绪继续；缓存用例保留短停留，确保图片请求有机会完成。
            consumer.Configure(options with { PageLoadingDelay = 0.2m });
            // The fixture document is no-store; public assets can use the optional shared cache.
            var beforePages = server.Hits.GetValueOrDefault("/page");
            Expect(await consumer.ConsumeAsync(TaskFor(server.Url + "page", 1, 3), CancellationToken.None), "pv=3 browser execution");
            Expect(server.Hits.GetValueOrDefault("/page") - beforePages == 3, "pv means total views");
            var assets = server.Hits.GetValueOrDefault("/asset.css");
            Expect(await consumer.ConsumeAsync(TaskFor(server.Url + "page", 1, 1), CancellationToken.None), "second cached task");
            Expect(assets > 0 && server.Hits.GetValueOrDefault("/asset.css") == assets && metrics.Snapshot.CacheHits > 0,
                $"public stylesheet cache hit: before={assets}, after={server.Hits.GetValueOrDefault("/asset.css")}, hits={metrics.Snapshot.CacheHits}, misses={metrics.Snapshot.CacheMisses}");
            report.AppendLine("PASS 真实 Chrome：PV=3 为首次访问及两次刷新，公共CSS共享缓存命中");

            consumer.Configure(options with { IsCacheMode = false });
            var native = TaskFor(server.Url + "native-session", 1, 3);
            native.Payload!["sleep"] = "0.3-0.3";
            var nativeAssetBefore = server.Hits.GetValueOrDefault("/native-asset.js");
            Expect(await consumer.ConsumeAsync(native, CancellationToken.None), "native cache and cookies across PVs");
            var firstState = ReadNativeState(business.SessionRoot, native.Id);
            var matchedViewport = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(
                Path.Combine(business.SessionRoot, native.Id.ToString()), "viewport.json", SearchOption.AllDirectories).Single()));
            var measuredViewport = JObject.Parse(firstState["browserViewport"]);
            Expect(measuredViewport.Value<int>("screenWidth") == matchedViewport.Value<int>("CssWidth")
                && measuredViewport.Value<int>("screenHeight") == matchedViewport.Value<int>("CssHeight")
                && Math.Abs(measuredViewport.Value<double>("innerWidth") - matchedViewport.Value<int>("CssWidth")) <= 1
                && Math.Abs(measuredViewport.Value<double>("innerHeight") - matchedViewport.Value<int>("CssHeight")) <= 1
                && Math.Abs(measuredViewport.Value<double>("dpr") - matchedViewport.Value<double>("DeviceScaleFactor")) < .001,
                "real Chrome screen, responsive viewport and DPR agree with AndroidViewportMatcher");
            Expect(firstState["pvs"] == "3" && firstState["assetLoads"] == "3", "all PVs execute in same session");
            Expect(server.Hits.GetValueOrDefault("/native-asset.js") - nativeAssetBefore == 1, "native cache reused within one UV");
            var firstRequests = server.NativeCookies.ToArray();
            Expect(firstRequests.Length == 3 && firstRequests[0] == ""
                && firstRequests.Skip(1).All(cookie => cookie.Contains("native_session=" + firstState["session"])), "cookies retained across refreshes");
            var independent = TaskFor(server.Url + "native-session", 1, 1);
            independent.Payload!["sleep"] = "0.3-0.3";
            Expect(await consumer.ConsumeAsync(independent, CancellationToken.None), "second native UV");
            var secondState = ReadNativeState(business.SessionRoot, independent.Id);
            Expect(secondState["pvs"] == "1" && secondState["assetLoads"] == "1" && secondState["session"] != firstState["session"]
                && server.NativeCookies.Last() == "" && server.Hits.GetValueOrDefault("/native-asset.js") - nativeAssetBefore == 2,
                "different UVs isolate cookies, local storage and native cache");
            report.AppendLine("PASS 原生缓存：同UV三次PV共享Cookie/存储、资源只下载一次；下一UV独立会话并重新下载");
            report.AppendLine("PASS AndroidViewportMatcher：实际Chrome screen/响应式viewport/DPR与匹配记录一致");

            foreach (var profile in new[]
            {
                TestDevice() with { ScreenWidth = 480, ScreenHeight = 800 },
                TestDevice() with { ScreenWidth = 1536, ScreenHeight = 2048 },
                TestDevice() with { ScreenWidth = 500, ScreenHeight = 500 },
                TestDevice() with { DevicePixelRatio = 3f }
            })
            {
                using var viewportConsumer = new BrowserTaskConsumer(new FakeDevices(profile), new NoProxy(), runner, business, logger);
                viewportConsumer.Configure(options with { IsCacheMode = false, PageLoadingDelay = 0.2m });
                var viewportTask = TaskFor(server.Url + "native-session", 1, 1);
                viewportTask.Payload!["sleep"] = 0.2;
                Expect(await viewportConsumer.ConsumeAsync(viewportTask, CancellationToken.None), "native viewport case");
                var actual = JObject.Parse(ReadNativeState(business.SessionRoot, viewportTask.Id)["browserViewport"]);
                var expected = Common.AndroidViewportMatcher.Match(profile.ScreenWidth, profile.ScreenHeight, profile.Make, profile.Model, profile.DevicePixelRatio);
                Expect(actual.Value<int>("screenWidth") == expected.CssWidth && actual.Value<int>("screenHeight") == expected.CssHeight
                    && Math.Abs(actual.Value<double>("innerWidth") - expected.CssWidth) <= 1
                    && Math.Abs(actual.Value<double>("innerHeight") - expected.CssHeight) <= 1
                    && Math.Abs(actual.Value<double>("dpr") - expected.DeviceScaleFactor) < .001, "real Chrome viewport uses one consistent DPR");
            }
            report.AppendLine("PASS 真实Chrome：480x800、1536x2048、方屏及设备指定DPR=3，screen/viewport/DPR一致");

            consumer.Configure(options with { IsCacheMode = false });
            var slowTask = TaskFor(server.Url + "slow-page", 1, 1);
            using var stop = new CancellationTokenSource();
            var running = consumer.ConsumeAsync(slowTask, stop.Token).AsTask();
            await WaitUntilAsync(() => server.Hits.GetValueOrDefault("/slow-page") > 0);
            var cancelTime = Stopwatch.StartNew();
            stop.Cancel();
            try { await running; throw new InvalidOperationException("Canceled browser task returned normally"); }
            catch (OperationCanceledException) { }
            Expect(cancelTime.Elapsed < TimeSpan.FromSeconds(8) && metrics.Snapshot.ActiveBrowsers == 0 && consumer.ActiveUvCount == 0, "navigation cancellation process cleanup");
            var summary = FindSummary(business.SessionRoot, slowTask.Id);
            Expect(summary.Value<string>("failure") == "canceled" && summary.Value<int>("dispatchedUvs") == 1, "canceled task summary");
            await new SessionRetention(business, logger).SweepAsync(true);
            foreach (var profile in Directory.EnumerateDirectories(business.SessionRoot, "chrome-profile", SearchOption.AllDirectories))
                Expect(File.Exists(Path.Combine(Path.GetDirectoryName(profile)!, "profile-cleanup.pending")), "locked profile has a managed cleanup marker");
            report.AppendLine($"PASS 导航中取消：{cancelTime.Elapsed.TotalMilliseconds:F0}ms，浏览器/UV 归零，临时 profile 已清理");

            consumer.Configure(options with { IsCacheMode = false });
            beforePages = server.Hits.GetValueOrDefault("/page");
            Expect(await consumer.ConsumeAsync(TaskFor(server.Url + "page", 1, 2), CancellationToken.None), "cache disabled task");
            Expect(server.Hits.GetValueOrDefault("/page") - beforePages == 2,
                $"uncached documents: expected 2, actual {server.Hits.GetValueOrDefault("/page") - beforePages}");
            report.AppendLine("PASS 共享缓存关闭后仍正常刷新，使用原生浏览器缓存策略");
            var redirects = server.Hits.GetValueOrDefault("/redirect");
            var landingPages = server.Hits.GetValueOrDefault("/page");
            Expect(await consumer.ConsumeAsync(TaskFor(server.Url + "redirect", 1, 3), CancellationToken.None), "redirect PV task");
            Expect(server.Hits.GetValueOrDefault("/redirect") - redirects == 1 && server.Hits.GetValueOrDefault("/page") - landingPages == 3,
                "later PVs refresh the current landing page, retaining the same session");
            report.AppendLine("PASS 首次访问完成重定向，后续PV刷新当前落地页面并保留会话");
            consumer.Configure(options with { IsCacheMode = false, PageLoadingTimeout = 1, PageLoadingDelay = 0.1m });
            var partial = TaskFor(server.Url + "partial-page", 1, 1);
            partial.Payload!["sleep"] = 0.2;
            Expect(await consumer.ConsumeAsync(partial, CancellationToken.None), "partial document should continue after timeout");
            var partialResult = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(
                Path.Combine(business.SessionRoot, partial.Id.ToString()), "result.json", SearchOption.AllDirectories).Single()));
            Expect(partialResult.Value<bool>("success") && partialResult["navigationWarnings"] is JArray { Count: > 0 },
                "partial document warning and successful result");
            foreach (var path in new[] { "http-status", "slow-image-page", "dynamic-page", "positioned-page", "iframe-page" })
            {
                var normal = TaskFor(server.Url + path, 1, 1);
                Expect(await consumer.ConsumeAsync(normal, CancellationToken.None), "visible page " + path);
                var normalResult = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(
                    Path.Combine(business.SessionRoot, normal.Id.ToString()), "result.json", SearchOption.AllDirectories).Single()));
                Expect(normalResult["navigationWarnings"] is JArray { Count: 0 }, "within-timeout navigation should be silent " + path);
            }
            foreach (var path in new[] { "blank-page", "proxy-error", "proxy407", "empty-page", "gateway-error", "hidden-content", "empty-iframe", "error-iframe" })
                Expect(!await consumer.ConsumeAsync(TaskFor(server.Url + path, 1, 1), CancellationToken.None), "unusable page " + path);
            foreach (var path in new[] { "final-blank", "final-error" })
            {
                var changed = TaskFor(server.Url + path, 1, 1);
                changed.Payload!["sleep"] = 0.6;
                Expect(!await consumer.ConsumeAsync(changed, CancellationToken.None), "page becomes unusable after opening " + path);
            }
            var busy = TaskFor(server.Url + "final-busy", 1, 1);
            busy.Payload!["sleep"] = 1.3;
            var busyStarted = Stopwatch.StartNew();
            Expect(!await consumer.ConsumeAsync(busy, CancellationToken.None), "unresponsive real renderer must fail and release resources");
            Expect(busyStarted.Elapsed < TimeSpan.FromSeconds(15), "busy renderer does not hold final inspection indefinitely");
            var busyResult = JObject.Parse(File.ReadAllText(Directory.EnumerateFiles(
                Path.Combine(business.SessionRoot, busy.Id.ToString()), "result.json", SearchOption.AllDirectories).Single()));
            Expect(busyResult.Value<string>("failureStage") == "contentCheck"
                && busyResult["errors"]!.ToString().Contains("检查调用超时"), "real inspection timeout classified correctly");
            var stalledInspection = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inspectionStarted = Stopwatch.StartNew();
            try
            {
                await PageContentInspector.AwaitInspectionAsync(stalledInspection.Task, TimeSpan.FromMilliseconds(100), CancellationToken.None);
                throw new InvalidOperationException("检查无响应未超时。");
            }
            catch (TimeoutException ex) { Expect(ex.Message.Contains("检查调用超时"), "inspection timeout distinct from empty content"); }
            Expect(inspectionStarted.Elapsed < TimeSpan.FromSeconds(2), "inspection bounded without responsive browser");
            stalledInspection.SetException(new IOException("late inspection failure"));
            // 先释放一个未监听端口，验证主文档连接拒绝不能被误判为慢加载成功。
            var unused = new TcpListener(IPAddress.Loopback, 0);
            unused.Start();
            var refusedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
            unused.Stop();
            Expect(!await consumer.ConsumeAsync(TaskFor($"http://127.0.0.1:{refusedPort}/", 1, 1), CancellationToken.None),
                "main document connection refused");
            Expect(metrics.Snapshot.ActiveBrowsers == 0, "initial navigation fixtures clean up");
            report.AppendLine("PASS 首次导航：超时已有内容继续并提示；限时内非2xx有内容/慢图片静默继续；空白超时、空页面、代理认证/错误页、网关错误及连接拒绝仍失败，浏览器清理归零");
            report.AppendLine("PASS 动态渲染、body零高度绝对定位内容、跨域iframe实际内容；结束变空/跳转网关错误仍失败，检查无响应独立超时且区分为空");
            using var authenticated = new BrowserTaskConsumer(devices, new CredentialProxy(server.Port), runner, business, logger, metrics);
            authenticated.Configure(options with { IsProxyMode = true, IsCacheMode = false, IpTtl = 0 });
            Expect(await authenticated.ConsumeAsync(TaskFor("http://offline-target.invalid/auth-page", 1, 1), CancellationToken.None), "authenticated browser proxy");
            Expect(server.Hits.GetValueOrDefault("/auth-success") > 0, "browser sent proxy authentication");
            report.AppendLine("PASS 真实 Chrome：HTTP 407 代理认证及凭证透传");
            using var expiring = new BrowserTaskConsumer(devices, new ExpiringProxy(server.Port), runner, business, logger, metrics);
            expiring.Configure(options with { IsProxyMode = true, IsCacheMode = false, IpTtl = 0 });
            var ttlElapsed = Stopwatch.StartNew();
            Expect(!await expiring.ConsumeAsync(TaskFor("http://offline-target.invalid/slow-page", 1, 1), CancellationToken.None), "in-flight vendor expiry");
            Expect(ttlElapsed.Elapsed < TimeSpan.FromSeconds(8) && metrics.Snapshot.ActiveBrowsers == 0, "expired proxy cleans up owned job");
            report.AppendLine($"PASS 真实 Chrome：供应商剩余 TTL 到期清理，{ttlElapsed.Elapsed.TotalMilliseconds:F0}ms");
        }
        report.AppendLine("输出目录：" + directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.txt"), report.ToString());
        return report.ToString();
    }

    private static async Task VerifyNetAsync(TestServer server, ILogger logger, StringBuilder report)
    {
        var net = new NetOptions { IpInfo = new IpInfoOptions { ApiUrls = [server.Url + "bad-ip", server.Url + "good-ip"], RequestTimeoutSeconds = 2 } };
        using var transport = new ProxyTransportFactory(net, logger);
        using var info = new ProxyIpInfoProvider(net, transport, logger);
        var result = await info.QueryAsync(new ProxyEndpoint { VendorId = "test", Host = "127.0.0.1", Port = server.Port }, CancellationToken.None);
        Expect(result.Value<string>("ip") == "203.0.113.7", "invalid JSON object must not win IpInfo race");
        var elapsed = Stopwatch.StartNew();
        try
        {
            await transport.GetLimitedAsStringAsync(server.Url + "slow-body", null, TimeSpan.FromMilliseconds(250), 1024, "test", CancellationToken.None);
            throw new InvalidOperationException("Slow body did not time out");
        }
        catch (ProxyRequestTimeoutException) { }
        Expect(elapsed.Elapsed < TimeSpan.FromSeconds(1.5), "response body deadline");
        using var canceled = new CancellationTokenSource(50);
        try
        {
            await transport.GetLimitedAsStringAsync(server.Url + "slow-body", null, TimeSpan.FromSeconds(5), 1024, "test", canceled.Token);
            throw new InvalidOperationException("Caller cancellation did not propagate");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        var credentials = new BusinessProxy("127.0.0.1:80") { Protocol = "socks5", Username = "u", Password = "test-password",
            AcquiredAt = DateTimeOffset.UtcNow.AddSeconds(-5), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(2) };
        var context = BusinessUvBrowserRunner.CreateContextOptions(new(1, 1, new Uri(server.Url), "unused", TestDevice(), credentials,
            new(), TimeSpan.Zero, 1, null));
        Expect(context.Proxy!.Server == "socks5://127.0.0.1:80" && context.Proxy.Username == "u" && context.Proxy.Password == "test-password", "proxy credentials/protocol");
        Expect(credentials.Deadline(3) < DateTimeOffset.UtcNow && !credentials.ToString().Contains("test-password"), "TTL acquisition origin and redaction");
        using var handlerClient = new HttpClient(new DeviceHandler());
        using var provider = new HttpDeviceFingerprintProvider(handlerClient, logger);
        var a = await provider.AcquireAsync(new() { DevApiUrl = "http://device.test/a" }, 1, 1, CancellationToken.None);
        var b = await provider.AcquireAsync(new() { DevApiUrl = "http://device.test/b" }, 1, 2, CancellationToken.None);
        Expect(a.Make == "a" && b.Make == "b", "changing device API invalidates pool");
        report.AppendLine("PASS IpInfo 无效快响应淘汰、慢正文超时/取消、代理认证/协议/领取时刻 TTL、设备源切换");
    }

    private static async Task VerifyAdmissionAsync(StringBuilder report)
    {
        using var gate = new FairUvAdmission(1);
        await gate.AcquireAsync(CancellationToken.None);
        var order = new List<int>();
        async Task Waiter(int number, CancellationToken token)
        { await gate.AcquireAsync(token); order.Add(number); gate.Release(); }
        using var canceled = new CancellationTokenSource();
        var first = Waiter(1, canceled.Token);
        var second = Waiter(2, CancellationToken.None);
        var third = Waiter(3, CancellationToken.None);
        canceled.Cancel(); gate.Release();
        try { await first; throw new InvalidOperationException("Canceled permit was granted"); } catch (OperationCanceledException) { }
        await Task.WhenAll(second, third);
        Expect(order.SequenceEqual(new[] { 2, 3 }), "FIFO after canceled waiter");
        await gate.StartAsync(100, CancellationToken.None);
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(gate.StartAsync(100, CancellationToken.None), gate.StartAsync(100, CancellationToken.None));
        Expect(elapsed.Elapsed >= TimeSpan.FromMilliseconds(180), "global interval");
        report.AppendLine("PASS FIFO 准入、取消不占许可、跨任务全局启动间隔");
    }

    private static async Task VerifyPersistenceAsync(string directory, StringBuilder report)
    {
        var task = TaskFor("http://test.invalid/", 1, 1);
        var batch = TaskSchedulingService.RepeatForMultiple([task], 1000);
        Expect(batch.Count == 1000 && batch[0].DeliveryId != batch[1].DeliveryId && batch[999].DeliveryId == batch[999].DeliveryId, "stable independent delivery keys");
        var options = new LocalTaskCacheOptions { FilePath = Path.Combine(directory, "pending.json"), LoadOnStart = true };
        await using (var cache = await LocalTaskCache<TaskEnvelope>.OpenAsync(options, item => item.DeliveryId))
        {
            await cache.StageBatchAsync([batch[0], batch[1]]);
            Expect(await cache.ClaimAsync(batch[0].DeliveryId), "durable claim");
            await cache.FlushAsync();
        }
        await using (var restored = await LocalTaskCache<TaskEnvelope>.OpenAsync(options, item => item.DeliveryId))
            Expect(restored.ReplayItems().Single().DeliveryId == batch[1].DeliveryId, "only unclaimed copy replays");
        var emptyRecord = Newtonsoft.Json.JsonConvert.SerializeObject(new { Version = 1, TaskType = typeof(TaskEnvelope).FullName, Keys = Array.Empty<string>() }) + "\n";
        var journalPath = options.FilePath + ".consumed.jsonl";
        await File.AppendAllTextAsync(journalPath, string.Concat(Enumerable.Repeat(emptyRecord, 1024 * 1024 / emptyRecord.Length + 1)));
        await using (var compacted = await LocalTaskCache<TaskEnvelope>.OpenAsync(options with { CompactClaimsWhenEmpty = true }, item => item.DeliveryId))
        {
            Expect(await compacted.ClaimAsync(batch[1].DeliveryId), "pending copy claimed before compaction");
            using (var externalReader = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                try { await compacted.FlushAsync(); throw new InvalidOperationException("Locked journal compaction should fail explicitly"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var following = task with { DeliveryId = "after-compaction-failure" };
                await compacted.StageBatchAsync([following]);
                Expect(await compacted.ClaimAsync(following.DeliveryId), "original journal remains writable after failed replacement");
            }
            await compacted.FlushAsync();
        }
        Expect(new FileInfo(journalPath).Length < 1024, "empty durable inbox allows bounded claim journal compaction");
        var tailPath = Path.Combine(directory, "tail.json");
        var tailOptions = new SchedulerOptions { WorkerCount = 1, QueueCapacity = 1,
            LocalCache = new() { FilePath = tailPath, LoadOnStart = true }, CompleteWhenSourceEmpty = true };
        var delivered = new[] { task with { DeliveryId = "tail-1" }, task with { DeliveryId = "tail-2" }, task with { DeliveryId = "tail-3" } };
        await using (var engine = AsyncTaskScheduler<TaskEnvelope>.FromMethods(_ => Task.FromResult<IReadOnlyList<TaskEnvelope>>(delivered),
            async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }, cacheKeySelector: item => item.DeliveryId))
        {
            await engine.StartAsync(tailOptions);
            await WaitUntilAsync(() => engine.Snapshot.Running == 1 && engine.Snapshot.Queued == 1);
            await engine.StopAsync();
            Expect(engine.Snapshot.Cached == 2 && engine.Snapshot.Canceled == 1, "queued and not-yet-enqueued tail remains durable");
        }
        var replayed = new List<string>();
        await using (var restarted = AsyncTaskScheduler<TaskEnvelope>.FromMethods(_ => Task.FromResult<IReadOnlyList<TaskEnvelope>>([]),
            (item, _) => { replayed.Add(item.DeliveryId); return Task.FromResult(true); }, cacheKeySelector: item => item.DeliveryId))
        {
            await restarted.StartAsync(tailOptions);
            var completed = await restarted.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Expect(completed.State == SchedulerState.Stopped && replayed.Order().SequenceEqual(new[] { "tail-2", "tail-3" }), "scheduler restart consumes only pending tail");
        }
        report.AppendLine("PASS 1000 倍惰性批次、同 ID 独立领取、待执行任务恢复且不重放已领取任务");
        report.AppendLine("PASS 队列满时取消保存批次尾部、重启恢复未执行任务、空收件箱压缩及压缩失败后原日志可继续领取");
    }

    private static async Task VerifyCacheAsync(BusinessOptions business, ILogger logger, StringBuilder report)
    {
        using var cache = new SharedStaticResourceCache(business, logger);
        var request = new Dictionary<string, string> { ["user-agent"] = "a" };
        var publicHeaders = new Dictionary<string, string> { ["cache-control"] = "public, max-age=60" };
        Expect(SharedStaticResourceCache.PublicLifetime(request, publicHeaders) == TimeSpan.FromSeconds(60), "public cache policy");
        var aged = new Dictionary<string, string>(publicHeaders) { ["age"] = "60" };
        Expect(SharedStaticResourceCache.PublicLifetime(request, aged) is null, "already aged response is stale");
        aged["age"] = "0"; aged["date"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("r");
        Expect(SharedStaticResourceCache.PublicLifetime(request, aged) is null, "old Date makes response stale");
        Expect(SharedStaticResourceCache.PublicLifetime(request, new Dictionary<string, string>
            { ["cache-control"] = "public, max-age=60, s-maxage=0" }) is null, "shared cache lifetime overrides max-age");
        foreach (var directive in new[] { "private, max-age=60", "public, no-store, max-age=60", "public, no-cache, max-age=60", "public, private=\"x-secret\", max-age=60" })
            Expect(SharedStaticResourceCache.PublicLifetime(request, new Dictionary<string, string> { ["cache-control"] = directive }) is null, "private/no-store/no-cache bypass");
        var varied = new Dictionary<string, string>(publicHeaders) { ["vary"] = "X-Custom" };
        Expect(SharedStaticResourceCache.PublicLifetime(request, varied) is null, "unknown Vary bypass");
        request["cookie"] = "secret";
        Expect(SharedStaticResourceCache.PublicLifetime(request, publicHeaders) is null, "cookie isolation");
        request.Remove("cookie");
        var key = SharedStaticResourceCache.RequestKey("http://asset.test/", request, "direct");
        var other = SharedStaticResourceCache.RequestKey("http://asset.test/", request, "proxy-b");
        Expect(key != other, "proxy partition");
        var bodies = Enumerable.Range(1, 32).Select(i => Enumerable.Repeat((byte)i, 1024).ToArray()).ToArray();
        await Task.WhenAll(bodies.Select(body => cache.StoreAsync(key, "image/png", new() { ["x-test"] = body[0].ToString() }, body, TimeSpan.FromMinutes(1))));
        var hit = await cache.TryGetAsync(key);
        Expect(hit is not null && hit.Body.All(b => b == byte.Parse(hit.Headers["x-test"])), "atomic metadata/body concurrent writes");
        await cache.StoreAsync("expired", "image/png", new(), Png, TimeSpan.FromMilliseconds(10));
        await Task.Delay(30);
        Expect(await cache.TryGetAsync("expired") is null && await cache.TryGetAsync(other) is null, "TTL and partition miss");
        await cache.StoreAsync("large", "image/png", new(), new byte[SharedStaticResourceCache.MaxEntryBytes + 1], TimeSpan.FromMinutes(1));
        Expect(await cache.TryGetAsync("large") is null, "large entry bypass");
        cache.Clear();
        report.AppendLine("PASS public/private/Vary/Cookie 缓存策略、出口隔离、并发原子发布、TTL 和大小保护");
    }

    private static async Task VerifyConsumerAsync(BusinessOptions business, ILogger logger, StringBuilder report)
    {
        var runner = new FakeRunner();
        var proxies = new FixedProxy();
        using var consumer = new BrowserTaskConsumer(new FakeDevices(), proxies, runner, business, logger);
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0, IsProxyMode = true, IpTtl = 0 });
        var expired = TaskFor("http://test.invalid/", 3, 1);
        Expect(!await consumer.ConsumeAsync(expired, CancellationToken.None) && runner.Calls == 0, "vendor deadline honored with IpTtl=0");
        consumer.Configure(new() { MaxConcurrency = 1, UvExecutionInterval = 0 });
        var partial = TaskFor("http://test.invalid/", 3, 1);
        Expect(!await consumer.ConsumeAsync(partial, CancellationToken.None), "partial success returns task failure");
        var summary = FindSummary(business.SessionRoot, partial.Id);
        Expect(summary.Value<int>("succeededUvs") == 2 && summary.Value<int>("unsuccessfulUvs") == 1, "partial summary counts");
        var throwing = new ThrowingProxy();
        using var failed = new BrowserTaskConsumer(new FakeDevices(), throwing, runner, business, logger);
        failed.Configure(new() { MaxConcurrency = 1 });
        var task = TaskFor("http://test.invalid/", 1, 1);
        Expect(!await failed.ConsumeAsync(task, CancellationToken.None), "proxy failure result");
        Expect(FindSummary(business.SessionRoot, task.Id).Value<string>("failure")!.StartsWith("proxy:"), "proxy failure summary");
        report.AppendLine("PASS 供应商 TTL=到期不派发、部分成功精确摘要、代理失败快照/摘要");
    }

    private static async Task VerifyRetentionAsync(string directory, ILogger logger, StringBuilder report)
    {
        var options = new BusinessOptions { SessionRoot = Path.Combine(directory, "retention"), SessionRetentionDays = 1 };
        var run = Path.Combine(options.SessionRoot, "1", "20200101-000000000-1-1234abcd");
        Directory.CreateDirectory(run);
        await File.WriteAllTextAsync(Path.Combine(run, "summary.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(run, "summary.json"), DateTime.UtcNow.AddDays(-3));
        var marker = Path.Combine(run, "run.lock");
        var retention = new SessionRetention(options, logger);
        using (var lease = new FileStream(marker, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        { await retention.SweepAsync(true); Expect(Directory.Exists(run), "active run must not be deleted"); }
        await retention.SweepAsync(true);
        Expect(!Directory.Exists(run), "expired completed run removed");
        report.AppendLine("PASS 会话保留期限：活跃租约保护、已完成过期目录清理");
    }

    private static JObject FindSummary(string root, long id) => JObject.Parse(File.ReadAllText(
        Directory.EnumerateFiles(Path.Combine(root, id.ToString()), "summary.json", SearchOption.AllDirectories).Single()));
    private static Dictionary<string, string> ReadNativeState(string root, long id)
    {
        var file = Directory.EnumerateFiles(Path.Combine(root, id.ToString()), "storage-state.json", SearchOption.AllDirectories).Single();
        var state = JObject.Parse(File.ReadAllText(file));
        return state["origins"]!.SelectMany(origin => origin["localStorage"]!)
            .ToDictionary(item => item.Value<string>("name")!, item => item.Value<string>("value")!);
    }
    private static TaskEnvelope TaskFor(string url, int uv, int pv) => new(DateTime.UtcNow.Ticks, "离线验证", url, DateTime.UtcNow)
        { Payload = new JObject { ["uv"] = uv, ["pv"] = pv, ["sleep"] = "0-0" } };
    private static BusinessDeviceProfile TestDevice() => new("offline", "Test", "Offline", "", "14", 1080, 2400,
        "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/145.0.0.0 Mobile Safari/537.36", 8, "", "", null);
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class NoProxy : IBusinessProxyProvider
    { public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(null); }
    private sealed class FixedProxy : IBusinessProxyProvider
    {
        public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(options.IsProxyMode
            ? new BusinessProxy("127.0.0.1:80") { AcquiredAt = DateTimeOffset.UtcNow.AddMinutes(-2), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } : null);
    }
    private sealed class CredentialProxy(int port) : IBusinessProxyProvider
    {
        public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(
            new BusinessProxy($"127.0.0.1:{port}") { Username = "offline", Password = "password", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
    }
    private sealed class ExpiringProxy(int port) : IBusinessProxyProvider
    {
        public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => ValueTask.FromResult<BusinessProxy?>(
            new BusinessProxy($"127.0.0.1:{port}") { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1.5) });
    }
    private sealed class ThrowingProxy : IBusinessProxyProvider
    { public ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1) => throw new InvalidOperationException("offline proxy failure"); }
    private sealed class FakeDevices(BusinessDeviceProfile? profile = null) : IBusinessDeviceProvider
    { public ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token) => ValueTask.FromResult(profile ?? TestDevice()); }
    private sealed class FakeRunner : IBusinessUvRunner
    { public int Calls; public Task<bool> RunAsync(BusinessUvRun run, CancellationToken token) { Calls++; return Task.FromResult(run.UvId != 2); } }
    private sealed class DeviceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                DeviceJson.Replace("\"Test\"", "\"" + request.RequestUri!.AbsolutePath.Trim('/') + "\"")) });
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _requests = new();
        private readonly Task _serving;
        internal ConcurrentDictionary<string, int> Hits { get; } = new();
        internal ConcurrentQueue<string> NativeCookies { get; } = new();
        internal int Port { get; }
        internal string Url => $"http://127.0.0.1:{Port}/";
        internal TestServer()
        { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _serving = ServeAsync(); }
        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _requests.RemoveAll(task => task.IsCompleted);
                    _requests.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) return;
                    var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string? requestHeader;
                    while (!string.IsNullOrEmpty(requestHeader = await reader.ReadLineAsync(_stop.Token)))
                    {
                        var separator = requestHeader.IndexOf(':');
                        if (separator > 0) requestHeaders[requestHeader[..separator]] = requestHeader[(separator + 1)..].Trim();
                    }
                    var target = line.Split(' ')[1];
                    var path = Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme != "file"
                        ? absolute.AbsolutePath : target.Split('?')[0];
                    Hits.AddOrUpdate(path, 1, (_, previous) => previous + 1);
                    if (path == "/native-session") NativeCookies.Enqueue(requestHeaders.GetValueOrDefault("Cookie") ?? "");
                    if (path == "/redirect")
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /page\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                        return;
                    }
                    if (path == "/auth-page")
                    {
                        if (requestHeaders.GetValueOrDefault("Proxy-Authorization") != "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("offline:password")))
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"offline\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                            return;
                        }
                        Hits.AddOrUpdate("/auth-success", 1, (_, previous) => previous + 1);
                    }
                    if (path == "/slow-page") await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
                    if (path == "/good-ip") await Task.Delay(100, _stop.Token);
                    if (path is "/slow-script.js" or "/slow-image.png") await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                    var body = path switch
                    {
                        "/devices" => Encoding.UTF8.GetBytes(DeviceJson),
                        "/bad-ip" => Encoding.UTF8.GetBytes("{}"),
                        "/good-ip" => Encoding.UTF8.GetBytes("{\"ip\":\"203.0.113.7\"}"),
                        "/asset.png" => Png,
                        "/asset.css" => Encoding.UTF8.GetBytes("body { color: #123456; }"),
                        "/slow-image.png" => Png,
                        "/slow-script.js" => Encoding.UTF8.GetBytes("void 0;"),
                        "/partial-page" => Encoding.UTF8.GetBytes("<html><body>Visible partial page<script src='/slow-script.js'></script></body></html>"),
                        "/blank-page" => Encoding.UTF8.GetBytes("<html><head><script src='/slow-script.js'></script></head><body>Later content</body></html>"),
                        "/slow-image-page" => Encoding.UTF8.GetBytes("<html><body>Visible page<img src='/slow-image.png'></body></html>"),
                        "/http-status" => Encoding.UTF8.GetBytes("<html><body>Visible application content with non-2xx status</body></html>"),
                        "/proxy-error" => Encoding.UTF8.GetBytes("<html><body>Proxy Authentication Required</body></html>"),
                        "/proxy407" => Encoding.UTF8.GetBytes("<html><body>Authentication required</body></html>"),
                        "/empty-page" => Encoding.UTF8.GetBytes("<html><body></body></html>"),
                        "/dynamic-page" => Encoding.UTF8.GetBytes("<html><body><script>setTimeout(() => document.body.textContent = 'Dynamic visible content', 300)</script></body></html>"),
                        "/positioned-page" => Encoding.UTF8.GetBytes("<html><body style='margin:0;height:0'><div style='position:absolute;top:20px'>Positioned visible content</div></body></html>"),
                        "/iframe-page" => Encoding.UTF8.GetBytes($"<html><body style='height:0'><iframe style='position:absolute;width:300px;height:200px' src='http://localhost:{Port}/frame-content'></iframe></body></html>"),
                        "/frame-content" => Encoding.UTF8.GetBytes("<html><body>Real frame content</body></html>"),
                        "/final-blank" => Encoding.UTF8.GetBytes("<html><body>Initially visible<script>setTimeout(() => document.body.textContent = '', 300)</script></body></html>"),
                        "/final-error" => Encoding.UTF8.GetBytes("<html><body>Initially visible<script>setTimeout(() => location.href = '/gateway-error', 300)</script></body></html>"),
                        "/final-busy" => Encoding.UTF8.GetBytes("<html><body>Initially visible<script>setTimeout(() => { while (true) {} }, 1000)</script></body></html>"),
                        "/hidden-content" => Encoding.UTF8.GetBytes("<html><body style='display:none'><div>Hidden content</div></body></html>"),
                        "/empty-iframe" => Encoding.UTF8.GetBytes("<html><body><iframe src='/empty-page'></iframe></body></html>"),
                        "/error-iframe" => Encoding.UTF8.GetBytes("<html><body><iframe src='/gateway-error'></iframe></body></html>"),
                        "/gateway-error" => Encoding.UTF8.GetBytes("<html><body>502 Bad Gateway</body></html>"),
                        "/native-asset.js" => Encoding.UTF8.GetBytes("native-cache-body"),
                        "/native-session" => Encoding.UTF8.GetBytes("""
                            <!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body>native session<script>
                            localStorage.setItem('browserViewport', JSON.stringify({innerWidth, innerHeight,
                              screenWidth: screen.width, screenHeight: screen.height, dpr: devicePixelRatio}));
                            localStorage.setItem('pvs', String(Number(localStorage.getItem('pvs') || 0) + 1));
                            let session = localStorage.getItem('session');
                            if (!session) { session = crypto.randomUUID(); localStorage.setItem('session', session); }
                            document.cookie = 'native_session=' + session + '; path=/';
                            fetch('/native-asset.js', {cache: 'force-cache'}).then(r => r.text()).then(() =>
                              localStorage.setItem('assetLoads', String(Number(localStorage.getItem('assetLoads') || 0) + 1)));
                            </script></body></html>
                            """),
                        _ => Encoding.UTF8.GetBytes("<!doctype html><html><head><link rel='stylesheet' href='/asset.css'></head><body>offline<img src='/asset.png'></body></html>")
                    };
                    var type = path is "/asset.png" or "/slow-image.png" ? "image/png"
                        : path == "/asset.css" ? "text/css"
                        : path == "/slow-script.js" ? "application/javascript"
                        : path is "/devices" or "/bad-ip" or "/good-ip" ? "application/json" : "text/html";
                    var policy = type == "text/html" && path != "/native-asset.js" ? "no-store" : "public, max-age=60";
                    var status = path == "/http-status" ? "503 Service Unavailable" : path == "/proxy407" ? "407 Proxy Authentication Required" : "200 OK";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: {type}\r\nCache-Control: {policy}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    if (path == "/slow-body") await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await _serving; await Task.WhenAll(_requests); _stop.Dispose(); }
    }
}
