using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Business;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;
using TrafficVista.Net.Vendors;

namespace TrafficVista.Net;

internal static class ProxyRequestIntegrationTest
{
    public static async Task<string> RunAsync(ILogger logger)
    {
        var probe = new AcquisitionProbe();
        var serialOptions = new NetOptions { DefaultVendor = "a", Vendors = new()
        {
            ["a"] = new() { ApiUrl = "https://offline.invalid/a", MaxConcurrency = 4 },
            ["b"] = new() { ApiUrl = "https://offline.invalid/b", MaxConcurrency = 4 }
        } };
        using (var serial = new ProxyService(serialOptions,
            new([new SerialAdapter("a", probe), new SerialAdapter("b", probe)], serialOptions), new Info(), logger))
        {
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => serial.AcquireAsync(
                new ProxyAcquireRequest { Settings = new() { ResolveRealIp = false, FetchIpDetails = false } }, i % 2 == 0 ? "a" : "b")));
            Check(probe.Peak == 1 && probe.Calls == 12, "跨供应商、跨任务全局串行取IP");
        }
        var task = new TaskEnvelope(23, "region-test", "http://offline.invalid/", DateTime.UtcNow)
        { Payload = new JObject { ["regions"] = "北京[0],上海[2],湖南[23]:长沙[267]", ["other"] = new JArray(1, 2) } };
        foreach (var real in new[] { false, true })
        foreach (var details in new[] { false, true })
        foreach (var supplier in new[] { false, true })
        {
            var adapter = new Adapter { SupplierIp = supplier ? "203.0.113.8" : null };
            var info = new Info();
            using var service = Service(adapter, info, logger);
            var request = new ProxyAcquireRequest { Task = task, Settings = new() { ResolveRealIp = real, FetchIpDetails = details }, Count = 3 };
            var result = await service.AcquireAsync(request);
            var queried = details || (real && !supplier);
            Check(info.Calls == (queried ? 1 : 0) && result.IpInfoQueried == queried, "开关查询策略");
            Check(result.RealIp == (queried ? "198.51.100.9" : supplier ? "203.0.113.8" : null), "真实IP来源");
            Check(adapter.Last?.Count == 3 && adapter.Last.Settings?.ResolveRealIp == real
                && JToken.DeepEquals(adapter.Last.Task?.Payload, task.Payload), "完整任务/配置/数量传递");
            adapter.Last!.Task!.Payload!["regions"] = "adapter-mutated";
            Check(task.Payload!["regions"]!.Value<string>()!.StartsWith("北京"), "请求快照隔离");
        }
        var retryAdapter = new Adapter(); var retryInfo = new Info { Failures = 1 };
        Check(new NetOptions().AcquireRetryIntervalMilliseconds == 1000, "默认重试间隔1000毫秒");
        using (var service = Service(retryAdapter, retryInfo, logger))
        {
            var result = await service.AcquireAsync(new ProxyAcquireRequest { Task = task, Settings = new() { FetchIpDetails = true } });
            Check(retryAdapter.Calls == 2 && retryInfo.Ports.Distinct().Count() == 2 && result.Proxy.Port == 8002, "查询失败重新领取新代理");
            Check(System.Diagnostics.Stopwatch.GetElapsedTime(retryAdapter.Attempts[0], retryAdapter.Attempts[1])
                >= TimeSpan.FromMilliseconds(1000), "失败后等待配置间隔再重试");
        }
        using (var stop = new CancellationTokenSource())
        {
            var adapter = new Adapter();
            using var service = Service(adapter, new Info { Failures = 99, OnFailure = () => stop.CancelAfter(50) }, logger, 5000);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try { await service.AcquireAsync(cancellationToken: stop.Token); throw new InvalidOperationException("重试等待未响应取消"); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { Check(adapter.Calls == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(2), "取消中断重试等待且不再请求"); }
        }
        var failedAdapter = new Adapter();
        using (var service = Service(failedAdapter, new Info { Failures = 99 }, logger))
        {
            try { await service.AcquireAsync(); throw new InvalidOperationException("未报告重试耗尽"); }
            catch (ProxyServiceException) { Check(failedAdapter.Calls == 2, "重试上限"); }
        }
        using (var stop = new CancellationTokenSource())
        {
            var adapter = new Adapter();
            using var service = Service(adapter, new Info { Cancel = stop }, logger);
            try { await service.AcquireAsync(cancellationToken: stop.Token); throw new InvalidOperationException("未响应取消"); }
            catch (OperationCanceledException) { Check(adapter.Calls == 1, "取消不重领"); }
        }
        var invalidAdapter = new Adapter { SupplierIp = "not-an-ip" }; var fallback = new Info();
        using (var service = Service(invalidAdapter, fallback, logger))
        {
            await service.AcquireAsync(new ProxyAcquireRequest { Settings = new() });
            Check(fallback.Calls == 1, "无效供应商真实IP回退查询");
        }
        var businessAdapter = new Adapter();
        using (var service = Service(businessAdapter, new Info(), logger))
        {
            var provider = new NetProxyProvider(service, logger, new Info());
            var proxy = await provider.AcquireAsync(task, new() { IsProxyMode = true,
                ProxyIpUrl = "https://offline.invalid/get", FetchIpDetails = true }, CancellationToken.None, count: 5);
            Check(businessAdapter.Last?.Count == 5 && businessAdapter.Last.Task?.Id == task.Id
                && proxy?.RealIp == "198.51.100.9" && proxy.IpInfo?["unknown"]?["keep"]?.Value<bool>() == true,
                "业务入口传入完整任务、数量并保留真实IP及详情");
        }
        const string url = "https://offline.invalid/get?num=1&key=fixture&real=old&real=duplicate";
        foreach (var real in new[] { false, true })
        foreach (var details in new[] { false, true })
        {
            var adapter = new Adapter(); var info = new Info();
            using var service = Service(adapter, info, logger);
            var provider = new NetProxyProvider(service, logger, info);
            var options = new TrafficVistaOptions { IsProxyMode = false, ResolveRealIp = real, FetchIpDetails = details,
                ProxyIpUrl = "not-a-proxy-url", IpTtl = 1 };
            var network = await provider.AcquireAsync(task, options, CancellationToken.None);
            Check(adapter.Calls == 0 && info.Calls == 0 && info.DirectCalls == (real || details ? 1 : 0), "直连开关只查询一次且不领取代理");
            if (real || details)
            {
                Check(network is { HasProxy: false, Server: null, RealIp: "198.51.100.9", RealIpSource: "direct-ipinfo" }
                    && network.Deadline(options.IpTtl) is null, "直连出口信息不得产生代理路由或租赁TTL");
                var probeRun = new BusinessUvRun(1, 1, new("https://offline.invalid/"), "unused",
                    new("direct-test", "Samsung", "SM-G991B", "", "14", 1080, 2400, "test", 8, "", "", null),
                    network, options, TimeSpan.Zero, 1, null);
                var directEnvironment = BrowserIpEnvironment.From(probeRun);
                var directContext = BusinessUvBrowserRunner.CreateContextOptions(probeRun);
                Check(directContext.Proxy is null && directEnvironment.RealIp == "198.51.100.9"
                    && (directEnvironment.TimezoneId is not null) == details && (directContext.Geolocation is not null) == details,
                    "直连真实IP/地理环境按开关应用");
            }
            else Check(network is null, "直连全部关闭不查询");
        }
        {
            var adapter = new Adapter(); var info = new Info { Failures = 1 };
            using var service = Service(adapter, info, logger);
            var provider = new NetProxyProvider(service, logger, info);
            try { await provider.AcquireAsync(task, new(), CancellationToken.None); throw new InvalidOperationException("直连失败未报告"); }
            catch (IOException) { Check(adapter.Calls == 0 && info.DirectCalls == 1, "直连失败不得改成领取代理"); }
        }
        using (var stop = new CancellationTokenSource())
        {
            var adapter = new Adapter(); var info = new Info { Cancel = stop };
            using var service = Service(adapter, info, logger);
            var provider = new NetProxyProvider(service, logger, info);
            try { await provider.AcquireAsync(task, new(), stop.Token); throw new InvalidOperationException("直连取消未传播"); }
            catch (OperationCanceledException) { Check(adapter.Calls == 0 && info.DirectCalls == 1, "直连取消不查询第二次"); }
        }
        var req = new ProxyAcquireRequest { Task = task, Settings = new() { ResolveRealIp = false }, Count = 5 };
        Check(new ProxyAcquireRequest().Count == 1, "默认数量1");
        var ipzanUrl = IpzanProxyAdapter.BuildRequestUrl(url, new(), req);
        Check(ipzanUrl.Contains("num=5") && ipzanUrl.Contains("format=txt") && ipzanUrl.EndsWith("realIp=0"), "ipzan数量及关闭真实IP参数");
        Check(VendorBProxyAdapter.BuildRequestUrl(url, new(), req) == url, "vendor-b地区和数量占位不改URL");
        var hooked = VendorBProxyAdapter.BuildRequestUrl(url, new() { RealIpParameterName = "real" }, req);
        Check(hooked.Contains("num=1&key=fixture") && hooked.EndsWith("real=0") && !hooked.Contains("duplicate"), "供应商真实IP请求参数替换");
        var vendorBUrl = VendorBProxyAdapter.BuildRequestUrl(url, new() { RealIpParameterName = "return_ip",
            RealIpParameterOnValue = "yes", RealIpParameterOffValue = "no" }, req);
        Check(vendorBUrl.Contains("real=old&real=duplicate") && vendorBUrl.EndsWith("return_ip=no"), "不同供应商参数独立处理");
        Check(VendorBProxyAdapter.BuildRequestUrl(url, new() { RealIpParameterName = "return_ip", RealIpParameterOnValue = "yes" },
            req with { Settings = req.Settings! with { ResolveRealIp = true } }).EndsWith("return_ip=yes"), "供应商自定义开启值");
        try { (req with { Count = 0 }).Validate(); throw new InvalidOperationException("未验证数量"); }
        catch (ArgumentOutOfRangeException) { }

        var device = new BusinessDeviceProfile("ip-env", "Samsung", "SM-G991B", "", "14", 1080, 2400,
            "Mozilla/5.0 (Linux; Android 14) Mobile", 8, "", "", null);
        var raw = new JObject { ["query"] = "198.51.100.9", ["timezone"] = "Asia/Tokyo", ["loc"] = "35.6,139.7", ["unknown"] = new JObject { ["keep"] = true } };
        var run = new BusinessUvRun(1, 1, new("https://offline.invalid/"), "unused", device, new(null, raw),
            new() { FetchIpDetails = true, IsHiddenMode = true }, TimeSpan.Zero, 1, null);
        var environment = BrowserIpEnvironment.From(run);
        Check(environment.RealIp == "198.51.100.9" && environment.TimezoneId == "Asia/Tokyo"
            && environment.Latitude == 35.6 && JToken.DeepEquals(environment.Details, raw), "完整详情及环境归一化");
        var contextOptions = BusinessUvBrowserRunner.CreateContextOptions(run);
        var runner = new BusinessUvBrowserRunner(null!, null!, null!, logger);
        var launchOptions = runner.CreateLaunchOptions(run);
        Check(launchOptions.Proxy is null && contextOptions.Proxy is null, "直连详情完整进入环境设置但不创建浏览器代理");
        Check(launchOptions.Args!.Contains("--webrtc-ip=198.51.100.9"), "真实IP传入Chrome");
        var onlyReal = BrowserIpEnvironment.From(run with { Options = run.Options with { FetchIpDetails = false } });
        Check(onlyReal.TimezoneId is null && onlyReal.Latitude is null && onlyReal.Details is null, "仅真实IP不应用详情");
        var off = BrowserIpEnvironment.From(run with { Options = run.Options with { FetchIpDetails = false, ResolveRealIp = false } });
        Check(off.RealIp is null, "关闭开关不应用真实IP");
        var malformed = BrowserIpEnvironment.From(run with { Proxy = new("proxy.invalid:80", new JObject
            { ["timezone"] = "invalid-zone", ["location"] = "unknown", ["lat"] = 100, ["lon"] = 139 }) });
        Check(malformed.TimezoneId is null && malformed.Latitude is null, "异常详情跳过无效环境字段");
        // Local real Chrome only: verify context emulation without contacting any provider or business URL.
        using var playwright = await Playwright.CreateAsync();
        await using var session = await OwnedChromeSession.StartAsync(playwright, launchOptions,
            Path.Combine(AppContext.BaseDirectory, "demo-output", "ip-env-test", Guid.NewGuid().ToString("N")), CancellationToken.None);
        var context = await session.Browser!.NewContextAsync(contextOptions);
        try
        {
            var page = await context.NewPageAsync();
            await page.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<html><body>offline environment test</body></html>" }));
            await page.GotoAsync("https://localhost/");
            Check(await page.EvaluateAsync<string>("Intl.DateTimeFormat().resolvedOptions().timeZone") == "Asia/Tokyo", "真实浏览器时区");
            var coordinates = await page.EvaluateAsync<double[]>("() => new Promise((resolve, reject) => navigator.geolocation.getCurrentPosition(p => resolve([p.coords.latitude, p.coords.longitude]), e => reject(new Error(e.message)), {timeout: 3000}))");
            Check(Math.Abs(coordinates[0] - 35.6) < 0.0001 && Math.Abs(coordinates[1] - 139.7) < 0.0001, "真实浏览器地理位置及权限");
        }
        finally { await context.CloseAsync(); }
        return "PASS\n代理及直连四种开关组合、真实IP回退、请求快照、直连单次查询/失败/取消且不领代理不应用代理TTL、完整详情及Chrome环境参数、真实直连Chrome时区与地理位置验证通过。全部使用离线测试数据。";
    }

    private static ProxyService Service(Adapter adapter, Info info, ILogger logger, int retryInterval = 1000)
    {
        var options = new NetOptions { DefaultVendor = "fake", AcquireRetryCount = 1,
            AcquireRetryIntervalMilliseconds = retryInterval,
            Vendors = new() { ["fake"] = new() { Enabled = true, ApiUrl = "https://offline.invalid/get" } } };
        return new(options, new([adapter], options), info, logger);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class AcquisitionProbe { public int Active, Peak, Calls; }
    private sealed class SerialAdapter(string id, AcquisitionProbe probe) : IProxyVendorAdapter
    {
        public string VendorId => id;
        public Task<ProxyEndpoint> AcquireProxyAsync(string url, CancellationToken token) => AcquireProxyAsync(url, new(), token);
        public async Task<ProxyEndpoint> AcquireProxyAsync(string url, ProxyAcquireRequest request, CancellationToken token)
        {
            var active = Interlocked.Increment(ref probe.Active);
            Interlocked.Increment(ref probe.Calls);
            int previous;
            do { previous = probe.Peak; if (previous >= active) break; }
            while (Interlocked.CompareExchange(ref probe.Peak, active, previous) != previous);
            try
            {
                await Task.Delay(20, token);
                return new() { VendorId = id, Host = "127.0.0.1", Port = 8000, RealIp = "203.0.113.8" };
            }
            finally { Interlocked.Decrement(ref probe.Active); }
        }
    }
    private sealed class Adapter : IProxyVendorAdapter
    {
        public string VendorId => "fake";
        public string? SupplierIp; public int Calls; public ProxyAcquireRequest? Last;
        public List<long> Attempts = [];
        public Task<ProxyEndpoint> AcquireProxyAsync(string url, CancellationToken token) => AcquireProxyAsync(url, new(), token);
        public Task<ProxyEndpoint> AcquireProxyAsync(string url, ProxyAcquireRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Last = request; Calls++;
            Attempts.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            return Task.FromResult(new ProxyEndpoint { VendorId = VendorId, Host = "127.0.0.1", Port = 8000 + Calls, RealIp = SupplierIp });
        }
    }
    private sealed class Info : IProxyIpInfoProvider
    {
        public int Calls; public int Failures; public CancellationTokenSource? Cancel; public List<int> Ports = [];
        public Action? OnFailure;
        public int DirectCalls;
        public Task<JObject> QueryDirectAsync(CancellationToken token)
        {
            DirectCalls++; Cancel?.Cancel(); token.ThrowIfCancellationRequested();
            if (DirectCalls <= Failures) throw new IOException("offline direct ipinfo fixture failed");
            return Task.FromResult(new JObject { ["ip"] = "198.51.100.9", ["timezone"] = "Asia/Tokyo", ["loc"] = "35.6,139.7" });
        }
        public Task<JObject> QueryAsync(ProxyEndpoint proxy, CancellationToken token)
        {
            Calls++; Ports.Add(proxy.Port); Cancel?.Cancel(); token.ThrowIfCancellationRequested();
            if (Calls <= Failures) { OnFailure?.Invoke(); throw new IOException("offline ipinfo fixture failed"); }
            return Task.FromResult(new JObject { ["ip"] = "198.51.100.9", ["unknown"] = new JObject { ["keep"] = true } });
        }
    }
}
