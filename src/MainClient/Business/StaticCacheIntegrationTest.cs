using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Serilog;
using TrafficVista.Configuration;

namespace TrafficVista.Business;

internal static class StaticCacheIntegrationTest
{
    internal static async Task<string> RunAsync(ILogger logger)
    {
        var prefixPolicy = new ResourceCachePolicy(new() { CacheExcludedUrlPrefixes = "https://HM.baidu.com/\r\nhttps://test/Track?x=" });
        Check(prefixPolicy.Bypass("script", "https://hm.baidu.com/hm.js?v=1")
            && prefixPolicy.Bypass("script", "https://hm.baidu.com/api/script")
            && prefixPolicy.Bypass("xhr", "https://hm.baidu.com/track.JS?v=1")
            && !prefixPolicy.Bypass("image", "https://hm.baidu.com/track.png")
            && !prefixPolicy.Bypass("stylesheet", "https://hm.baidu.com/track.css"), "prefix exclusion only bypasses JavaScript; CSS and images keep caching");
        Check(!prefixPolicy.Bypass("script", "https://hm.baidu.com.evil/hm.js")
            && !prefixPolicy.Bypass("script", "http://hm.baidu.com/hm.js")
            && !prefixPolicy.Bypass("script", "https://test/track?x=1")
            && prefixPolicy.Bypass("script", "https://test/Track?x=1"), "prefix host boundaries, scheme and case-sensitive path/query");
        var disabledPolicy = new ResourceCachePolicy(new() { JavaScriptCacheMode = JavaScriptCacheMode.Disabled });
        Check(disabledPolicy.Bypass("script", "https://test/api/script") && disabledPolicy.Bypass("xhr", "https://test/x.JS?random=1")
            && !disabledPolicy.Bypass("stylesheet", "https://test/a.css"), "disabled JS includes scripts without file extensions");
        Check(SharedStaticResourceCache.ShouldDeduplicateJavaScript("GET", "https://test/script.JS?rnd=1"), "GET JS path enables deduplication");
        foreach (var url in new[] { "https://test/api/script", "https://test/api?file=x.js", "https://test/x.js/", "https://test/x.css?rnd=1" })
            Check(!SharedStaticResourceCache.ShouldDeduplicateJavaScript("GET", url), "non-JS filename excluded: " + url);
        Check(!SharedStaticResourceCache.ShouldDeduplicateJavaScript("POST", "https://test/script.js"), "POST JS excluded");
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "static-cache-tests", Guid.NewGuid().ToString("N"));
        var options = new BusinessOptions { StaticCacheRoot = Path.Combine(root, "cache"), ExportRunData = false,
            ChromeExecutablePath = Environment.GetEnvironmentVariable("TRAFFICVISTA_TEST_CHROME")
                ?? Path.GetFullPath(new BusinessOptions().ChromeExecutablePath, AppContext.BaseDirectory) };
        Directory.CreateDirectory(root);
        await VerifyStorageAsync(options, logger);
        using var cache = new SharedStaticResourceCache(options, logger);
        using var launches = new BrowserLaunchLimiter(1);
        using var playwright = await Playwright.CreateAsync();
        await using var server = new AssetServer(options.ChromeExecutablePath);
        var metrics = new BusinessMetrics();
        var runner = new BusinessUvBrowserRunner(playwright, launches, cache, logger, metrics, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        BusinessUvRun? lastRun = null;
        for (var uv = 1; uv <= 2; uv++)
        {
            Task state = Task.CompletedTask;
            runner.BrowserReadyForTest = (_, page) => state = uv == 2
                ? VerifyWarmHitBypassAsync(page, runner, cache, timeout.Token) : VerifyPageAsync(page);
            var directory = Path.Combine(root, "uv-" + uv);
            Directory.CreateDirectory(directory);
            var device = new BusinessDeviceProfile("cache-fixture", "Test", "Offline", "", "14", 1080, 2400,
                "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/145.0.0.0 Mobile Safari/537.36", 8, "", "", null);
            var run = new BusinessUvRun(1, uv, new(server.Url), directory, device, null,
                new() { IsHiddenMode = true, IsCacheMode = true, PageLoadingTimeout = 20 }, TimeSpan.FromSeconds(5), 1, null);
            lastRun = run;
            Check(await runner.RunAsync(run, timeout.Token), "browser run succeeds");
            await state;
        }
        var captureMetrics = metrics.Snapshot;
        Check(captureMetrics.ActiveCacheResources.Length == 0, "completed UV leaves no cache capture stages");
        foreach (var phase in new[] { "缓存采集排队", "缓存响应头读取", "缓存采集查重", "缓存正文读取", "缓存写入" })
            Check(captureMetrics.AverageStageMs.ContainsKey(phase), "real browser records cache phase: " + phase);
        foreach (var path in new[] { "/asset.css", "/main.js", "/large.js", "/photo.svg", "/audit.ttf" })
            Check(server.Hits.GetValueOrDefault(path) == 1, "second UV must hit shared cache: " + path);
        foreach (var path in new[] { "/asset.GIF", "/masked.png", "/magic.png" })
            Check(server.Hits.GetValueOrDefault(path) == 2, "GIF must always reach origin: " + path);
        Check(server.Hits.GetValueOrDefault("/cookie.js") == 1, "same URL is reused across cookie identities and UVs");
        Check(server.Hits.GetValueOrDefault("/clip.mp4") == 3, "full video and two distinct ranges each reach origin only once");
        Check(metrics.Snapshot.ActiveBrowsers == 0 && ChromeProcessJob.OwnedProcessCount == 0, "browser cleanup");
        Check(cache.Statistics.MemoryHits > 0, "browser resource reads use memory tier");
        using (var first = await cache.TryGetFileAsync(server.Url + "duplicate.JS?r=1"))
        using (var second = await cache.TryGetFileAsync(server.Url + "duplicate.JS?r=2"))
        using (var nonJs = await cache.TryGetFileAsync(server.Url + "duplicate"))
            Check(first is not null && second is not null && nonJs is not null && first.Path == second.Path && first.Path != nonJs.Path,
                "GET JS random URLs share decoded content file; extensionless JavaScript stays separate");
        Check(server.Hits.GetValueOrDefault("/duplicate.JS") == 2, "different JS URLs are each fetched initially, then independently hit cache");
        cache.Clear();
        var strictDirectory = Path.Combine(root, "strict-js");
        Directory.CreateDirectory(strictDirectory);
        Task strictState = Task.CompletedTask;
        runner.BrowserReadyForTest = (_, page) => strictState = VerifyPageAsync(page);
        Check(await runner.RunAsync(lastRun! with { UvId = 3, Directory = strictDirectory,
            Options = lastRun.Options with { JavaScriptCacheMode = JavaScriptCacheMode.Url } }, timeout.Token), "strict JS browser run succeeds");
        await strictState;
        using (var first = await cache.TryGetFileAsync(server.Url + "duplicate.JS?r=1"))
        using (var second = await cache.TryGetFileAsync(server.Url + "duplicate.JS?r=2"))
            Check(first is not null && second is not null && first.Path != second.Path,
                "strict URL mode keeps equal JS bodies in separate files");
        async Task RunPolicyAsync(TrafficVistaOptions options, string name)
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            Task pageDone = Task.CompletedTask;
            runner.BrowserReadyForTest = (_, page) => pageDone = page.WaitForFunctionAsync("window.done === true", null, new() { Timeout = 15000 });
            Check(await runner.RunAsync(lastRun! with { UvId = 5, Directory = directory, Options = options }, timeout.Token), name);
            await pageDone;
        }
        var noJs = lastRun!.Options with { JavaScriptCacheMode = JavaScriptCacheMode.Disabled };
        var mainBefore = server.Hits.GetValueOrDefault("/main.js");
        await RunPolicyAsync(noJs, "no-js-warm");
        Check(server.Hits.GetValueOrDefault("/main.js") == mainBefore + 1, "disabled JS bypasses previously cached script");
        cache.Clear();
        await RunPolicyAsync(noJs, "no-js-cold");
        using (var main = await cache.TryGetFileAsync(server.Url + "main.js"))
        using (var css = await cache.TryGetFileAsync(server.Url + "asset.css"))
            Check(main is null && css is not null, "disabled JS does not write scripts; CSS still cached");
        cache.Clear();
        await RunPolicyAsync(lastRun.Options with { CacheExcludedUrlPrefixes = server.Url + "main.js\n" + server.Url + "asset.css" }, "excluded-prefixes");
        using (var main = await cache.TryGetFileAsync(server.Url + "main.js"))
        using (var css = await cache.TryGetFileAsync(server.Url + "asset.css"))
        using (var other = await cache.TryGetFileAsync(server.Url + "large.js"))
            Check(main is null && css is not null && other is not null, "excluded JS uses network without storing; matching CSS and other scripts still cached");
        await using var drivers = new BusinessPlaywrightDrivers(logger);
        drivers.Configure(2, 4);
        var multiDriverRunner = new BusinessUvBrowserRunner(null, launches, cache, logger, metrics, options, drivers);
        await VerifyConcurrentBrowsersAsync(root, server, cache, multiDriverRunner, launches, timeout.Token);
        Check(drivers.Snapshot.Created == 2 && drivers.Snapshot.ActiveUvs == 0, "concurrent worker browsers use two independent lazy drivers");
        Check(launches.WaitingStarts == 0, "startup queue counter drains even without intake-gate subscribers");
        Check(await BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(15), timeout.Token), "driver close requests drain before disposing Playwright");
        Check(BrowserCloseCoordinator.Shared.Snapshot.InFlight == 0 && BrowserCloseCoordinator.Shared.Snapshot.Deferred == 0, "no outstanding driver close requests");
        Check(await drivers.ReleaseIdleAsync() && drivers.Snapshot.Created == 0, "drivers dispose after UV cleanup and pending close requests drain");
        var startupPhases = new[] { "Playwright驱动准备", "Playwright启动", "启动CDP连接", "启动进程查询", "启动进程绑定", "启动主进程绑定", "启动子进程扫描", "启动子进程绑定", "启动CDP断开" };
        var startupMetrics = metrics.Snapshot;
        foreach (var phase in startupPhases)
            Check(startupMetrics.AverageStageMs.ContainsKey(phase) && !startupMetrics.ActiveStages.ContainsKey(phase),
                "startup phase records duration and drains active count: " + phase);
        var cleanupPhases = new[] { "清理Context关闭", "清理取消回调", "清理驱动收尾排队", "清理Browser关闭", "清理终止进程树", "清理主进程退出", "清理子进程退出", "清理Job释放", "清理缓存回调" };
        foreach (var phase in cleanupPhases)
            Check(startupMetrics.AverageStageMs.ContainsKey(phase) && !startupMetrics.ActiveStages.ContainsKey(phase),
                "cleanup phase records duration and drains active count: " + phase);
        var startupReport = "\n浏览器启动细分（本地测试各阶段平均）：\n" + string.Join("\n", startupPhases.Select(phase =>
            $"  {phase}: {startupMetrics.AverageStageMs[phase]:F2} ms"))
            + "\n浏览器清理细分（本地测试各阶段平均）：\n" + string.Join("\n", cleanupPhases.Select(phase =>
                $"  {phase}: {startupMetrics.AverageStageMs[phase]:F2} ms"));
        return "PASS\n非GIF图片/CSS/JS/字体/视频跨UV命中；3MiB图片、3MiB解压后JS不受1MiB限制；gzip解码回填与chunked无Content-Length的CSS加载正常。\n"
            + "GIF扩展名（含大写和查询参数）、image/gif响应及伪装为PNG的GIF文件头均不入库；Cookie A/B及不同UV共用同URL资源，服务器缓存策略不限制本地TTL。\n"
            + "字体正常加载并复用；视频200与两个不同Range的206分别缓存，状态/Content-Range/正文匹配；浏览器进程归零。\n"
            + "磁盘文件租约在覆盖/清空期间保护活跃读取；超过总预算的单个文件仍保留，取消写入不发布残缺文件。\n"
            + "独立内存/磁盘库接入：内存命中、清空后的两个真实Chrome并发冷请求合并，同一main.js仅回源一次。" + startupReport + "\n输出目录：" + root;
    }

    private static async Task VerifyConcurrentBrowsersAsync(string root, AssetServer server, SharedStaticResourceCache cache,
        BusinessUvBrowserRunner runner, BrowserLaunchLimiter launches, CancellationToken token)
    {
        cache.Clear();
        launches.Configure(2);
        server.HoldMain = true;
        var hitsBefore = server.Hits.GetValueOrDefault("/main.js");
        var coalescedBefore = cache.Statistics.CoalescedRequests;
        var checks = new ConcurrentBag<Task>();
        runner.BrowserReadyForTest = (_, page) => checks.Add(VerifyPageAsync(page));
        var device = new BusinessDeviceProfile("cache-concurrent", "Test", "Offline", "", "14", 1080, 2400,
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/145.0.0.0 Mobile Safari/537.36", 8, "", "", null);
        var runs = Enumerable.Range(3, 2).Select(index =>
        {
            var directory = Path.Combine(root, "concurrent-" + index);
            Directory.CreateDirectory(directory);
            return runner.RunAsync(new(2, index, new(server.Url), directory, device, null,
                new() { IsHiddenMode = true, IsCacheMode = true, PageLoadingTimeout = 20 }, TimeSpan.FromSeconds(5), 1, null)
                { WorkerId = index }, token);
        }).ToArray();
        Exception? failure = null;
        try
        {
            await server.MainEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (cache.Statistics.CoalescedRequests <= coalescedBefore) await Task.Delay(10, deadline.Token);
        }
        catch (Exception ex) { failure = ex; }
        finally { server.ReleaseMain.TrySetResult(); }
        var results = await Task.WhenAll(runs);
        await Task.WhenAll(checks);
        if (failure is not null) throw new InvalidOperationException("Concurrent browser cache coordination did not complete.", failure);
        Check(results.All(result => result) && server.Hits.GetValueOrDefault("/main.js") - hitsBefore == 1,
            "two browsers share one cold origin request");
        Check(ChromeProcessJob.OwnedProcessCount == 0, "concurrent cache browser cleanup");
    }

    private static async Task VerifyWarmHitBypassAsync(IPage page, BusinessUvBrowserRunner runner,
        SharedStaticResourceCache cache, CancellationToken token)
    {
        var response = await page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == "/main.js");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var held = 0;
        try
        {
            await cache.BeginCaptureAsync(deadline.Token); held++;
            await cache.BeginCaptureAsync(deadline.Token); held++;
            // A known cached script must bypass occupied body-read slots. GIF-magic resources
            // are intentionally never stored, so other callbacks may still need those slots.
            await runner.RecordAsync(response, "", deadline.Token);
            Check(!deadline.IsCancellationRequested, "warm response bypasses occupied body-read slots");
        }
        finally { while (held-- > 0) cache.EndCapture(); }
        await VerifyPageAsync(page);
    }

    private static async Task VerifyPageAsync(IPage page)
    {
        await page.WaitForFunctionAsync("window.done === true", null, new() { Timeout = 15000 });
        Check(await page.EvaluateAsync<int>("window.largeLoaded") == 1, "cached compressed JS executes exactly once");
        Check(await page.EvaluateAsync<string>("getComputedStyle(document.body).color") == "rgb(1, 2, 3)", "cached CSS applied");
        Check(await page.EvaluateAsync<string>("window.cookieResults.join(',')") == "A,A,A", "URL-only script output across cookies");
        Check(await page.EvaluateAsync<int>("window.dedupLoaded") == 3, "deduplicated and extensionless scripts all execute");
    }
    private static async Task VerifyStorageAsync(BusinessOptions options, ILogger logger)
    {
        var clock = new CacheClock();
        using var cache = new SharedStaticResourceCache(options with { StaticCacheRoot = Path.Combine(options.StaticCacheRoot, "storage"), StaticCacheMaxMegabytes = 1, StaticCacheTtlHours = 2 }, logger, clock);
        var headers = new Dictionary<string, string> { ["cache-control"] = "max-age=60" };
        var a = new Dictionary<string, string> { ["cookie"] = "identity=A" };
        var b = new Dictionary<string, string> { ["cookie"] = "identity=B" };
        Check(SharedStaticResourceCache.RequestKey("http://test/", a, "direct") == SharedStaticResourceCache.RequestKey("http://test/", b, "proxy-b"), "cookie and proxy do not affect keys");
        Check(!SharedStaticResourceCache.RequestKey("http://test/", a, "direct").Contains("identity=A"), "keys must not contain raw cookies");
        using (var input = new MemoryStream(new byte[5 * 1024 * 1024], writable: false))
            await cache.StoreStreamAsync("oversized", "image/png", headers, input);
        using var original = await cache.TryGetFileAsync("oversized");
        Check(original is not null && new FileInfo(original.Path).Length == 5 * 1024 * 1024, "single oversized resource is retained beyond soft total budget");
        var oldPath = original!.Path;
        await cache.StoreAsync("oversized", "image/png", headers, [1, 2, 3]);
        Check(File.Exists(oldPath), "overwriting must not remove an active hit");
        original.Dispose();
        Check(!File.Exists(oldPath), "retired body is deleted when lease ends");
        using var active = await cache.TryGetFileAsync("oversized");
        var activePath = active!.Path;
        cache.Clear();
        Check(File.Exists(activePath), "clearing preserves active hit");
        active.Dispose();
        Check(!File.Exists(activePath), "cleared body released after lease");
        await cache.StoreAsync("gif", "image/png", headers, Encoding.ASCII.GetBytes("GIF89a-disguised"));
        Check(!cache.HasFresh("gif"), "GIF magic is excluded");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await cache.StoreAsync("cancel", "image/png", headers, [1, 2, 3], canceled.Token); }
        catch (OperationCanceledException) { }
        Check(!cache.HasFresh("cancel") && !Directory.EnumerateFiles(cache.Root, "*.tmp").Any(), "canceled writes leave no published or temporary body");
        foreach (var policy in new[] { "max-age=1", "max-age=0", "no-store, private, no-cache", "" })
        {
            var response = new Dictionary<string, string> { ["cache-control"] = policy, ["age"] = "999999", ["date"] = "Thu, 01 Jan 1970 00:00:00 GMT" };
            await cache.StoreAsync(policy, "image/png", response, [1, 2, 3]);
        }
        clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        foreach (var policy in new[] { "max-age=1", "max-age=0", "no-store, private, no-cache", "" })
            Check(cache.HasFresh(policy), "configured TTL overrides server policy/age/date: " + policy);
        using (var restarted = new SharedStaticResourceCache(options with { StaticCacheRoot = Path.Combine(options.StaticCacheRoot, "storage"), StaticCacheTtlHours = 3 }, logger, clock))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Check(!cache.HasFresh("max-age=1") && restarted.HasFresh("max-age=1"), "TTL boundary and changed setting apply to existing files after restart");
        }
        clock.Advance(TimeSpan.FromHours(1));
        Check(!cache.HasFresh("max-age=1"), "cache hits never slide expiration");

    }
    internal sealed class CacheClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Static cache regression: " + message); }

    private sealed class AssetServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _requests = new();
        private readonly Task _serving;
        private readonly byte[] _largeJs;
        private readonly byte[] _image;
        private readonly byte[] _font;
        private readonly byte[] _video = Enumerable.Range(0, 8192).Select(i => (byte)(i / 1024)).ToArray();
        private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        internal ConcurrentDictionary<string, int> Hits { get; } = new();
        internal bool HoldMain;
        internal readonly TaskCompletionSource MainEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseMain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        internal AssetServer(string executable)
        {
            var fontPath = Path.Combine(Path.GetDirectoryName(executable)!, "android_fonts", "xiaomi", "RobotoFlex-Regular.ttf");
            if (!File.Exists(fontPath)) fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            _font = File.ReadAllBytes(fontPath);
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            { var source = Encoding.UTF8.GetBytes("window.largeLoaded=(window.largeLoaded||0)+1;/*" + new string('x', 3 * 1024 * 1024) + "*/"); gzip.Write(source); }
            _largeJs = compressed.ToArray();
            _image = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/><!--" + new string('x', 3 * 1024 * 1024) + "--></svg>");
            _listener.Start(); _serving = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try { while (true) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _requests.Add(HandleAsync(client)); } }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token);
                if (first is null) return;
                var path = first.Split(' ')[1].Split('?')[0];
                string? line; var cookie = ""; var range = "";
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = line[7..].Trim();
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
                }
                Hits.AddOrUpdate(path, 1, (_, count) => count + 1);
                if (path == "/main.js" && HoldMain)
                { MainEntered.TrySetResult(); await ReleaseMain.Task.WaitAsync(_stop.Token); }
                var identity = cookie.Contains("identity=B") ? "B" : "A";
                var body = path switch
                {
                    "/" => Encoding.UTF8.GetBytes(Html),
                    "/asset.css" => Encoding.UTF8.GetBytes("@font-face{font-family:audit;src:url('/audit.ttf')}body{color:rgb(1,2,3)}.font{font-family:audit}"),
                    "/main.js" => Encoding.UTF8.GetBytes("window.mainLoaded=true;"),
                    "/large.js" => _largeJs,
                    "/photo.svg" => _image,
                    "/audit.ttf" => _font,
                    "/clip.mp4" => _video,
                    "/cookie.js" => Encoding.UTF8.GetBytes("window.identity='" + identity + "';"),
                    "/duplicate.JS" or "/duplicate" => Encoding.UTF8.GetBytes("window.dedupLoaded=(window.dedupLoaded||0)+1;"),
                    _ => Gif
                };
                var type = path switch { "/" => "text/html", "/asset.css" => "text/css", "/large.js" or "/main.js" or "/cookie.js" or "/duplicate.JS" or "/duplicate" => "application/javascript",
                    "/photo.svg" => "image/svg+xml", "/audit.ttf" => "font/ttf", "/clip.mp4" => "video/mp4", "/masked.png" => "image/gif", _ => "image/png" };
                var status = "200 OK"; var contentRange = "";
                if (path == "/clip.mp4" && range.StartsWith("bytes="))
                {
                    var bounds = range[6..].Split('-');
                    var firstByte = int.Parse(bounds[0]); var lastByte = int.Parse(bounds[1]);
                    body = _video[firstByte..(lastByte + 1)];
                    status = "206 Partial Content";
                    contentRange = $"Content-Range: bytes {firstByte}-{lastByte}/{_video.Length}\r\n";
                }
                var headers = "HTTP/1.1 " + status + "\r\n" + contentRange + "Content-Type: " + type + "\r\nCache-Control: " + (path == "/cookie.js" ? "private, no-store, no-cache, max-age=0" : path == "/main.js" ? "max-age=0" : path == "/" ? "no-store" : "max-age=1")
                    + "\r\nConnection: close\r\n" + (path == "/large.js" ? "Content-Encoding: gzip\r\n" : "")
                    + (path == "/asset.css" ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + body.Length + "\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                if (path == "/asset.css")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                    await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), _stop.Token);
                }
                else await stream.WriteAsync(body, _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await _serving; await Task.WhenAll(_requests); _stop.Dispose(); }
        private const string Html = """
            <!doctype html><html><body>cache test<span class="font">font loaded</span><script>
            const pause=()=>new Promise(r=>setTimeout(r,100));
            const script=url=>new Promise((resolve,reject)=>{const s=document.createElement('script');s.src=url;s.onload=resolve;s.onerror=reject;document.head.append(s)});
            const image=url=>new Promise((resolve,reject)=>{const i=new Image();i.src=url;i.onload=resolve;i.onerror=reject;document.body.append(i)});
            (async()=>{try{
              await new Promise((resolve,reject)=>{const l=document.createElement('link');l.rel='stylesheet';l.href='/asset.css';l.onload=resolve;l.onerror=reject;document.head.append(l)}); await pause();
              await document.fonts.load('12px audit');await pause();await script('/main.js'); await pause(); await script('/large.js'); await pause(); await image('/photo.svg'); await pause();
              await image('/asset.GIF?q=1'); await pause(); await image('/masked.png'); await pause(); await image('/magic.png'); await pause();
              const full=await fetch('/clip.mp4');const all=new Uint8Array(await full.arrayBuffer());if(full.status!==200||all.length!==8192||all[8191]!==7)throw Error('full video');await pause();
              for(const start of [0,1024]){const r=await fetch('/clip.mp4',{headers:{Range:`bytes=${start}-${start+1023}`}});const b=new Uint8Array(await r.arrayBuffer());if(r.status!==206||r.headers.get('Content-Range')!==`bytes ${start}-${start+1023}/8192`||b.length!==1024||b[0]!==start/1024)throw Error('video range');await pause()}
              window.cookieResults=[];document.cookie='identity=A; path=/';await script('/cookie.js');window.cookieResults.push(window.identity); await pause();
              await script('/cookie.js');window.cookieResults.push(window.identity);await pause();
              document.cookie='identity=B; path=/';await script('/cookie.js');window.cookieResults.push(window.identity);
              await script('/duplicate.JS?r=1');await pause();await script('/duplicate.JS?r=2');await pause();await script('/duplicate');window.done=true;
            }catch(e){window.fixtureError=String(e)}})();
            </script></body></html>
            """;
    }
}
