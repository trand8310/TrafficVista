using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// Net 模块端到端自检：本地模拟文本供应商（ipzan 协议）与 JSON 供应商（VendorB 协议）+ 本机假代理上的 IpInfo 接口。
/// 覆盖：默认/显式供应商路由与路由错误、领取失败（HTTP 500、无有效行、业务 code 失败）、
/// IpInfo 失败（无效 JSON、HTTP 404、超时明确区分取消）、调用取消、
/// 代理有效期从领取时计算且查询耗时计入（到期整体失败）、每供应商独立并发与 IpInfo 总体上限互不串扰、
/// 同一家供应商仅换 URL 不改路由、IpInfo 未知字段/嵌套/原始值完整保留（JObject）、
/// IpInfo 多接口并行竞速谁快用谁的（全失败报 ipinfo 阶段）、白名单接口就位（出口 IP + {ip} 占位，不自动调用）、
/// 瑕疵代理按 AcquireRetryCount 丢弃换新代理重试（次数可验证）、领取 URL 由调用方传入并对所有供应商统一生效（未传回退配置）。
/// </summary>
internal static class NetIntegrationTest
{
    private const string IpInfoJson =
        """{"query":"203.0.113.7","country":"中国","isp":"示例运营商","nested":{"deep":{"value":42,"flag":true},"list":[1,2,3,"x"]},"unknown_future_field":"keep-me","nullable":null,"bignum":1234567890123}""";

    public static async Task<string> RunAsync(ILogger logger)
    {
        var report = new StringBuilder();
        using var proxy = new ProxyStub(IpInfoJson);

        // 文本供应商：首行无效，次行给出假代理地址；带 250ms 延迟与并发计数供隔离验证。
        using var textPrimary = new HttpStub(async ctx =>
        {
            await Task.Delay(250);
            await RespondAsync(ctx, 200, $"not an ip line\r\n127.0.0.1:{proxy.Port}\r\n");
        });
        using var textSecondary = new HttpStub(async ctx =>
            await RespondAsync(ctx, 200, $"127.0.0.1:{proxy.Port}\r\n"));
        using var text500 = new HttpStub(async ctx => await RespondAsync(ctx, 500, "boom"));
        using var textGarbage = new HttpStub(async ctx => await RespondAsync(ctx, 200, "hello world\r\n"));
        using var jsonVendor = new HttpStub(async ctx =>
        {
            var body = ctx.Request.Url!.AbsolutePath switch
            {
                "/ok" => $$"""{"code":0,"proxy":"127.0.0.1:{{proxy.Port}}","username":"u1","password":"p1","expire":600}""",
                "/fail" => """{"code":1001,"msg":"余额不足"}""",
                "/expire1" => $$"""{"code":0,"ip":"127.0.0.1","port":"{{proxy.Port}}","expire":1}""",
                "/real" => new JObject { ["code"] = 0, ["proxy"] = $"127.0.0.1:{proxy.Port}", ["exit"] = new JObject { ["ip"] = "203.0.113.8" } }.ToString(),
                "/bad-real" => new JObject { ["code"] = 0, ["proxy"] = $"127.0.0.1:{proxy.Port}", ["exit"] = new JObject { ["ip"] = new JObject { ["invalid"] = true } } }.ToString(),
                _ => """{"code":0,"proxy":"bad-entry"}"""
            };
            await RespondAsync(ctx, 200, body);
        });

        var ipInfoOk = "http://ipinfo.test/ip";
        // stub.Url 以 "/" 结尾，路径直接续拼。
        var jsonOk = jsonVendor.Url + "ok";
        var jsonFail = jsonVendor.Url + "fail";
        var jsonExpire1 = jsonVendor.Url + "expire1";

        // 场景一：默认供应商 ipzan（format=txt 文本协议）端到端；IpInfo 完整 JSON；请求行证明通道走了代理且无需 DNS。
        {
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            if (!service.Vendors.Contains("ipzan") || !service.Vendors.Contains("vendor-b"))
                throw new InvalidOperationException($"可用供应商列表不正确：{string.Join(",", service.Vendors)}。");
            var result = await service.AcquireAsync();
            Expect(result.Proxy.VendorId == "ipzan" && result.Proxy.Host == "127.0.0.1"
                   && result.Proxy.Port == proxy.Port, "场景一：代理连接信息与文本供应商返回不一致。");
            Expect(result.Proxy.Ttl == TimeSpan.FromSeconds(300) && result.Proxy.ExpiresAt > DateTimeOffset.UtcNow,
                "场景一：兜底有效期应从领取时开始计算。");
            AssertIpInfo(result.IpInfo, "场景一");
            Expect(proxy.LastRequest?.StartsWith($"GET {ipInfoOk} ", StringComparison.Ordinal) == true,
                $"场景一：IpInfo 请求应经代理且为绝对 URI，实际“{proxy.LastRequest}”。");
            report.AppendLine("场景一 ipzan 文本供应商端到端：代理 + IpInfo 完整 JSON，请求经代理 ✔");
        }

        // 场景二：显式 vendor-b（JSON 协议），proxy 字段 + username/password + expire。
        {
            using var provider = Build(Options("", textPrimary.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var result = await service.AcquireAsync("vendor-b");
            Expect(result.Proxy.VendorId == "vendor-b" && result.Proxy.Port == proxy.Port, "场景二：JSON 供应商解析结果不正确。");
            Expect(result.Proxy.Username == "u1" && result.Proxy.Password == "p1", "场景二：认证信息未带入 ProxyEndpoint。");
            Expect(result.Proxy.ToString().Contains("p1") is false, "场景二：ToString/日志不应输出密码。");
            Expect(result.Proxy.Ttl == TimeSpan.FromSeconds(600), "场景二：expire 应作为从领取时开始的有效期。");
            AssertIpInfo(result.IpInfo, "场景二");
            report.AppendLine("场景二 vendor-b JSON 供应商端到端：proxy/认证/expire 统一转换 ✔");
        }

        // 场景三：路由错误——无默认供应商、未知供应商。
        {
            using var provider = Build(Options("", textPrimary.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex1 = await ExpectFailureAsync(() => service.AcquireAsync(), "场景三");
            Expect(ex1.Phase == "route" && ex1.VendorId == "", "场景三：未配置默认供应商应报路由阶段失败。");
            var ex2 = await ExpectFailureAsync(() => service.AcquireAsync("vendor-x"), "场景三");
            Expect(ex2.Phase == "route" && ex2.VendorId == "vendor-x", "场景三：未知供应商应报路由阶段失败。");
            report.AppendLine("场景三 路由错误：缺省默认供应商 / 未知供应商标识 ✔");
        }

        // 场景四：领取失败——HTTP 500。
        {
            using var provider = Build(Options("ipzan", text500.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync(), "场景四");
            Expect(ex.Phase == "acquire" && ex.VendorId == "ipzan" && ex.Message.Contains("500"),
                $"场景四：领取阶段应报 HTTP 500，实际 {ex.Message}");
            report.AppendLine("场景四 领取失败：领取接口 HTTP 500 明确报告 ✔");
        }

        // 场景五：领取失败——无有效 ip:port 行。
        {
            using var provider = Build(Options("ipzan", textGarbage.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync(), "场景五");
            Expect(ex.Phase == "acquire" && ex.Message.Contains("ip:port"),
                $"场景五：应报无有效 ip:port 行，实际 {ex.Message}");
            report.AppendLine("场景五 领取失败：返回内容无有效 ip:port ✔");
        }

        // 场景六：JSON 供应商业务失败（code 非 0），错误信息含 code 与 msg。
        {
            using var provider = Build(Options("", textPrimary.Url, jsonFail, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync("vendor-b"), "场景六");
            Expect(ex.Phase == "acquire" && ex.Message.Contains("1001") && ex.Message.Contains("余额不足"),
                $"场景六：应报接口业务失败，实际 {ex.Message}");
            report.AppendLine("场景六 领取失败：JSON 供应商 code 非 0 报告 msg ✔");
        }

        // 场景七：IpInfo 无效 JSON → 查询阶段失败。
        {
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk, "http://ipinfo.test/ip-badjson"), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync(), "场景七");
            Expect(ex.Phase == "ipinfo" && ex.Message.Contains("JSON"),
                $"场景七：应报 IpInfo 无效 JSON，实际 {ex.Message}");
            report.AppendLine("场景七 IpInfo 失败：正文不是有效 JSON ✔");
        }

        // 场景八：IpInfo HTTP 404 → 查询阶段失败。
        {
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk, "http://ipinfo.test/ip-404"), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync(), "场景八");
            Expect(ex.Phase == "ipinfo" && ex.Message.Contains("404"),
                $"场景八：应报 IpInfo HTTP 404，实际 {ex.Message}");
            report.AppendLine("场景八 IpInfo 失败：HTTP 404 ✔");
        }

        // 场景九：IpInfo 超时（1 秒）区别于取消；整体耗时接近超时值。
        {
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk,
                "http://ipinfo.test/ip-slow", ipInfoTimeoutSeconds: 1), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var started = Stopwatch.GetTimestamp();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync(), "场景九");
            var elapsed = Stopwatch.GetElapsedTime(started);
            Expect(ex.Phase == "ipinfo" && ex.Message.Contains("超时") && ex.InnerException is ProxyRequestTimeoutException,
                $"场景九：应报请求超时且区别于取消，实际 {ex.Message}");
            Expect(elapsed < TimeSpan.FromSeconds(2.5), $"场景九：应在 1 秒超时附近失败，实际 {elapsed.TotalSeconds:0.##} 秒。");
            report.AppendLine($"场景九 IpInfo 失败：1 秒超时明确报告（实测 {elapsed.TotalSeconds:0.##}s）✔");
        }

        // 场景十：调用取消原样抛出，不包装成业务失败。
        {
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await service.AcquireAsync(cancellationToken: cts.Token);
                throw new InvalidOperationException("场景十：取消应抛出 OperationCanceledException。");
            }
            catch (OperationCanceledException) { }
            report.AppendLine("场景十 取消：取消令牌原样传播 ✔");
        }

        // 场景十一：有效期从领取时计算、查询耗时计入——expire=1 秒 + 查询 1.6 秒 → 整体失败（ttl 阶段）。
        {
            using var provider = Build(Options("", textPrimary.Url, jsonExpire1,
                "http://ipinfo.test/ip-delay", ipInfoTimeoutSeconds: 8), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(() => service.AcquireAsync("vendor-b"), "场景十一");
            Expect(ex.Phase == "ttl", $"场景十一：查询结束时应报有效期已过，实际 {ex.Message}");
            report.AppendLine("场景十一 有效期：领取后计时、查询耗时计入、到期整体失败 ✔");
        }

        // 场景十二：并发隔离——ipzan 上限 2（模拟接口观测 ≤2），IpInfo 总上限 1（假代理观测 ==1），4 路并发全部成功。
        {
            textPrimary.ResetCounters();
            proxy.ResetCounters();
            using var provider = Build(Options("ipzan", textPrimary.Url, jsonOk, ipInfoOk,
                vendorAMaxConcurrency: 2, ipInfoMaxConcurrency: 1), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var results = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => service.AcquireAsync(cancellationToken: CancellationToken.None)));
            Expect(results.All(r => r.IpInfo["query"]?.Value<string>() == "203.0.113.7"), "场景十二：4 路并发应全部成功。");
            Expect(Volatile.Read(ref textPrimary.MaxConcurrent) == 2,
                $"场景十二：供应商并发应为 2，实际 {textPrimary.MaxConcurrent}。");
            Expect(Volatile.Read(ref proxy.MaxConcurrent) == 1,
                $"场景十二：IpInfo 总体并发应为 1，实际 {proxy.MaxConcurrent}。");
            report.AppendLine("场景十二 并发隔离：供应商上限 2 / IpInfo 总上限 1，互不串扰 ✔");
        }

        // 场景十三：同一家供应商只换 URL 不改路由——ipzan 指向第二台文本接口即换源。
        {
            using var provider = Build(Options("ipzan", textSecondary.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var result = await service.AcquireAsync();
            Expect(result.Proxy.Port == proxy.Port, "场景十三：换 URL 后应仍由同一路由取到代理。");
            Expect(textSecondary.Hits == 1, $"场景十三：新 URL 应被访问一次，实际 {textSecondary.Hits} 次。");
            report.AppendLine("场景十三 配置换源：供应商标识不变，仅改 ApiUrl ✔");
        }

        // 场景十四：出厂配置接线——appsettings.json 已含 ipzan 提取接口（界面代理接口同值）；只验证配置与脱敏，不访问外网。
        {
            var settings = new TrafficVistaSettings(AppContext.BaseDirectory);
            Expect(settings.Net.DefaultVendor == "ipzan", "场景十四：出厂默认供应商应为 ipzan。");
            var ipzan = settings.Net.Vendors.TryGetValue("ipzan", out var shipped) ? shipped : null;
            Expect(ipzan is { Enabled: true } && ipzan.ApiUrl.Contains("service.ipzan.com"),
                "场景十四：出厂配置应包含启用状态的 ipzan 领取接口。");
            Expect(ipzan!.SecretValues().Count > 0, "场景十四：领取接口 URL 中的 secret 应被识别用于脱敏。");
            Expect(settings.Net.IpInfo.EffectiveApiUrls().Count >= 4, "场景十四：出厂应配置多个 IpInfo 竞速接口。");
            using var provider = Build(settings.Net, logger);
            var service = provider.GetRequiredService<IProxyService>();
            Expect(service.Vendors.Contains("ipzan"), "场景十四：ipzan 应在可用供应商列表中。");
            report.AppendLine("场景十四 出厂配置：ipzan 已接线，secret 可脱敏，多 IpInfo 接口竞速，路由可用 ✔");
        }

        // 场景十五：IpInfo 多接口竞速谁快用谁的——慢（1600ms）/坏 JSON/即时成功同时发出，即时的胜出，慢接口被取消。
        {
            var raceUrls = new[]
            {
                "http://ipinfo.test/ip-delay", "http://ipinfo.test/ip-badjson", "http://ipinfo.test/ip"
            };
            using var provider = Build(
                Options("ipzan", textSecondary.Url, jsonOk, "http://ipinfo.test/ip-delay", ipInfoUrls: raceUrls), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var started = Stopwatch.GetTimestamp();
            var result = await service.AcquireAsync(cancellationToken: CancellationToken.None);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AssertIpInfo(result.IpInfo, "场景十五");
            Expect(elapsedMs < 1_500, $"场景十五：应取最快接口结果（<1500ms），实际 {elapsedMs:F0}ms。");
            var waitStarted = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds < 1_500 &&
                   (!proxy.PathHits.ContainsKey("/ip-badjson") || !proxy.PathHits.ContainsKey("/ip-delay")))
                await Task.Delay(25);
            Expect(proxy.PathHits.ContainsKey("/ip-badjson"), "场景十五：坏 JSON 接口也应被同时请求。");
            Expect(proxy.PathHits.ContainsKey("/ip-delay"), "场景十五：慢接口也应被同时请求（随后被取消）。");
            report.AppendLine($"场景十五 IpInfo 多接口竞速：谁快用谁的（{elapsedMs:F0}ms 胜出），其余取消 ✔");
        }

        // 场景十六：IpInfo 全部接口失败 → 明确报 ipinfo 阶段。
        {
            using var provider = Build(
                Options("ipzan", textSecondary.Url, jsonOk, "http://ipinfo.test/ip-404",
                    ipInfoUrls: new[] { "http://ipinfo.test/ip-404", "http://ipinfo.test/ip-badjson" }), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var ex = await ExpectFailureAsync(
                () => service.AcquireAsync(cancellationToken: CancellationToken.None), "场景十六");
            Expect(ex.Phase == "ipinfo", $"场景十六：应报 ipinfo 阶段，实际 {ex.Phase}。");
            report.AppendLine("场景十六 IpInfo 全部接口失败：明确报 ipinfo 阶段错误 ✔");
        }

        // 场景十七：白名单接口就位（预留，业务不自动调用）——直连竞速取出口 IP + {ip} 占位调用白名单接口；未配置即跳过。
        {
            using var egressStub = new HttpStub(async ctx =>
                await RespondAsync(ctx, 200, """{"ip":"8.8.8.8","country":"中国"}"""));
            string? whitelistQuery = null;
            using var whitelistStub = new HttpStub(async ctx =>
            {
                whitelistQuery = ctx.Request.Url!.Query;
                await RespondAsync(ctx, 200, """{"code":"0","msg":"ok"}""");
            });
            var wlOptions = Options("ipzan", textSecondary.Url, jsonOk, egressStub.Url);
            wlOptions.Vendors["ipzan"].Parameters["WhitelistApiUrl"] = whitelistStub.Url + "?secret=dummy-secret-value&ip={ip}";
            using var provider = Build(wlOptions, logger);
            var whitelist = provider.GetRequiredService<IProxyWhitelistService>();
            var egressIp = await whitelist.QueryCurrentEgressIpAsync();
            Expect(egressIp == "8.8.8.8", $"场景十七：应取到出口 IP，实际 {egressIp ?? "null"}。");
            var refreshed = await whitelist.RefreshWhitelistAsync("ipzan");
            Expect(refreshed, "场景十七：配置了白名单接口应返回 true。");
            Expect(whitelistQuery?.Contains("ip=8.8.8.8") == true,
                $"场景十七：白名单接口应收到替换后的出口 IP，实际 {whitelistQuery ?? "无请求"}。");
            using var bareProvider = Build(Options("ipzan", textSecondary.Url, jsonOk, egressStub.Url), logger);
            var skipped = await bareProvider.GetRequiredService<IProxyWhitelistService>().RefreshWhitelistAsync("ipzan");
            Expect(!skipped, "场景十七：未配置 WhitelistApiUrl 应跳过并返回 false。");
            report.AppendLine("场景十七 白名单接口就位：出口 IP 直连竞速 + {ip} 占位登记，未配置即跳过（不自动调用） ✔");
        }

        // 场景十八：瑕疵代理重试——前两次拿不到真实 IP，换全新代理第三次成功；一直瑕疵则 1+2 次后失败；领取失败同样重试。
        {
            using var provider = Build(
                Options("ipzan", textSecondary.Url, jsonOk, "http://ipinfo.test/ip-defect", acquireRetryCount: 5), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var result = await service.AcquireAsync(cancellationToken: CancellationToken.None);
            AssertIpInfo(result.IpInfo, "场景十八");
            Expect(proxy.PathHits.TryGetValue("/ip-defect", out var defectHits) && defectHits == 3,
                $"场景十八：应在第 3 次尝试成功，实际尝试 {defectHits} 次。");

            using var noipProvider = Build(
                Options("ipzan", textSecondary.Url, jsonOk, "http://ipinfo.test/ip-noip", acquireRetryCount: 2), logger);
            var noipService = noipProvider.GetRequiredService<IProxyService>();
            var noipEx = await ExpectFailureAsync(
                () => noipService.AcquireAsync(cancellationToken: CancellationToken.None), "场景十八b");
            Expect(noipEx.Phase == "ipinfo", $"场景十八b：应报 ipinfo 阶段，实际 {noipEx.Phase}。");
            Expect(proxy.PathHits.TryGetValue("/ip-noip", out var noipHits) && noipHits == 3,
                $"场景十八b：应共尝试 1+2=3 次，实际 {noipHits} 次。");

            var hitsBefore = text500.Hits;
            using var acqProvider = Build(
                Options("ipzan", text500.Url, jsonOk, ipInfoOk, acquireRetryCount: 2), logger);
            var acqService = acqProvider.GetRequiredService<IProxyService>();
            var acqEx = await ExpectFailureAsync(
                () => acqService.AcquireAsync(cancellationToken: CancellationToken.None), "场景十八c");
            Expect(acqEx.Phase == "acquire", $"场景十八c：应报 acquire 阶段，实际 {acqEx.Phase}。");
            Expect(text500.Hits - hitsBefore == 3, $"场景十八c：领取应共尝试 3 次，实际 {text500.Hits - hitsBefore} 次。");
            report.AppendLine("场景十八 瑕疵代理重试：拿不到真实 IP 即丢弃换新代理，次数受 AcquireRetryCount 控制 ✔");
        }

        // 场景十九：领取 URL 由调用方传入并对所有供应商统一生效，覆盖配置默认；未传回退配置；传入不合法报 route 且不发领取请求。
        {
            using var provider = Build(Options("ipzan", text500.Url, jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var hitsBefore500 = text500.Hits;
            var hitsBeforeSecondary = textSecondary.Hits;
            var result = await service.AcquireAsync(apiUrl: textSecondary.Url, cancellationToken: CancellationToken.None);
            Expect(result.Proxy.VendorId == "ipzan", "场景十九：供应商仍由路由选择，不受 URL 传入影响。");
            Expect(textSecondary.Hits - hitsBeforeSecondary == 1,
                $"场景十九：应按调用方传入 URL 领取，实际命中 {textSecondary.Hits - hitsBeforeSecondary} 次。");
            Expect(text500.Hits == hitsBefore500, "场景十九：配置默认 URL（500）不应被访问。");

            var routeEx = await ExpectFailureAsync(
                () => service.AcquireAsync(apiUrl: "not a url", cancellationToken: CancellationToken.None), "场景十九b");
            Expect(routeEx.Phase == "route", $"场景十九b：传入不合法 URL 应报路由阶段，实际 {routeEx.Phase}。");
            Expect(text500.Hits == hitsBefore500, "场景十九b：路由校验失败不应发出领取请求。");

            var jsonHitsBefore = jsonVendor.Hits;
            var bEx = await ExpectFailureAsync(
                () => service.AcquireAsync("vendor-b", textSecondary.Url, CancellationToken.None), "场景十九c");
            Expect(bEx.Phase == "acquire", $"场景十九c：vendor-b 按传入文本 URL 领取应解析失败，实际 {bEx.Phase}。");
            Expect(jsonVendor.Hits == jsonHitsBefore, "场景十九c：vendor-b 也必须用传入 URL，配置的 JSON 接口不应被访问。");
            report.AppendLine("场景十九 领取 URL 调用方传入优先（对所有供应商统一生效），未传回退配置，不合法报 route ✔");
        }

        // Confirmed supplier exit-IP path is separate from the proxy connection host.
        {
            var options = Options("vendor-b", textPrimary.Url, jsonVendor.Url + "real", ipInfoOk);
            options.Vendors["vendor-b"].RealIpJsonPath = "exit.ip";
            using var provider = Build(options, logger);
            var service = provider.GetRequiredService<IProxyService>();
            var request = new ProxyAcquireRequest { Settings = new TrafficVista.Configuration.TrafficVistaOptions() };
            var supplied = await service.AcquireAsync(request);
            Expect(supplied.RealIp == "203.0.113.8" && supplied.RealIpSource == "vendor"
                && !supplied.IpInfoQueried && supplied.IpInfo.Count == 0, "场景二十：供应商真实IP应跳过详情查询。");
            var malformed = await service.AcquireAsync(request, apiUrl: jsonVendor.Url + "bad-real");
            Expect(malformed.IpInfoQueried && malformed.RealIpSource == "ipinfo", "场景二十：非字符串出口字段应回退查询。");
            report.AppendLine("场景二十 供应商JSON出口字段提取、跳过详情查询、异常字段回退查询 ✔");
        }
        {
            string? lastQuery = null;
            var expiry = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
            using var ipzanJson = new HttpStub(async ctx =>
            {
                lastQuery = ctx.Request.Url!.Query;
                var path = ctx.Request.Url.AbsolutePath;
                if (path == "/txt") { await RespondAsync(ctx, 200, $"127.0.0.1:{proxy.Port}"); return; }
                var item = new JObject { ["ip"] = "127.0.0.1", ["port"] = proxy.Port.ToString(), ["net"] = "移动",
                    ["expired"] = path == "/expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : expiry,
                    ["real_ip"] = path == "/missing-real" ? null : "198.51.100.18" };
                await RespondAsync(ctx, 200, new JObject { ["code"] = path == "/fail" ? 1001 : 0, ["status"] = 200,
                    ["data"] = new JObject { ["list"] = path == "/empty" ? new JArray() : new JArray(item) } }.ToString());
            });
            using var provider = Build(Options("ipzan", ipzanJson.Url + "real", jsonOk, ipInfoOk), logger);
            var service = provider.GetRequiredService<IProxyService>();
            var request = new ProxyAcquireRequest { Settings = new() { ResolveRealIp = true, FetchIpDetails = false } };
            var supplied = await service.AcquireAsync(request);
            Expect(supplied.Proxy.Host == "127.0.0.1" && supplied.RealIp == "198.51.100.18" && !supplied.IpInfoQueried
                && supplied.Proxy.ExpiresAt?.ToUnixTimeMilliseconds() == expiry, "场景二十一：ipzan JSON真实IP及绝对截止时间。");
            Expect(lastQuery?.Contains("num=1") == true && lastQuery.Contains("format=json") && lastQuery.Contains("realIp=1"),
                "场景二十一：实际领取请求未应用ipzan参数。");
            var missing = await service.AcquireAsync(request, apiUrl: ipzanJson.Url + "missing-real");
            Expect(missing.IpInfoQueried && missing.RealIp == "203.0.113.7", "场景二十一：ipzan缺失真实IP应回退代理查询。");
            var details = await service.AcquireAsync(request with { Settings = request.Settings! with { FetchIpDetails = true } });
            Expect(details.IpInfoQueried && details.RealIpSource == "ipinfo", "场景二十一：详情开关应覆盖供应商跳过查询逻辑。");
            var txt = await service.AcquireAsync(request with { Settings = new() { ResolveRealIp = false } }, apiUrl: ipzanJson.Url + "txt");
            Expect(!txt.IpInfoQueried && txt.RealIp is null && lastQuery?.Contains("format=txt") == true
                && lastQuery.Contains("realIp=0"), "场景二十一：关闭真实IP的TXT请求与解析。");
            foreach (var path in new[] { "expired", "empty", "fail" })
            {
                var failure = await ExpectFailureAsync(() => service.AcquireAsync(request, apiUrl: ipzanJson.Url + path), "场景二十一 " + path);
                Expect(failure.Phase == "acquire", "场景二十一：供应商无可用代理应报领取阶段失败。");
            }
            report.AppendLine("场景二十一 ipzan实际请求参数、JSON出口IP及毫秒截止时间、缺失出口回退、强制详情、TXT关闭开关、过期/空列表/业务失败 ✔");
        }
        {
            using var directFast = new HttpStub(async ctx => { await Task.Delay(15); await RespondAsync(ctx, 200, IpInfoJson); });
            using var directInvalid = new HttpStub(async ctx => await RespondAsync(ctx, 200, "{\"ip\":\"invalid\"}"));
            using var directSlow = new HttpStub(async ctx => { await Task.Delay(750); await RespondAsync(ctx, 200, IpInfoJson); });
            var options = Options("ipzan", textPrimary.Url, jsonOk, "", ipInfoUrls: [directInvalid.Url, directFast.Url, directSlow.Url]);
            var hitsBefore = textPrimary.Hits;
            using (var provider = Build(options, logger))
            {
                var info = provider.GetRequiredService<IProxyIpInfoProvider>();
                var business = new TrafficVista.Business.NetProxyProvider(provider.GetRequiredService<IProxyService>(), logger, info);
                var network = await business.AcquireAsync(new TrafficVista.Contracts.TaskEnvelope(1, "direct-offline", "http://offline.invalid/", DateTime.UtcNow),
                    new() { IsProxyMode = false, ResolveRealIp = true, FetchIpDetails = true }, CancellationToken.None);
                Expect(network is { HasProxy: false, Server: null, RealIp: "203.0.113.7" }
                    && network.Deadline(1) is null && textPrimary.Hits == hitsBefore, "场景二十二：直连出口查询不得领取/设置代理或应用代理TTL。");
                AssertIpInfo(network!.IpInfo!, "场景二十二");
            }
            var failedOptions = Options("ipzan", textPrimary.Url, jsonOk, "", ipInfoUrls: [directInvalid.Url, text500.Url]);
            using (var provider = Build(failedOptions, logger))
            {
                try { await provider.GetRequiredService<IProxyIpInfoProvider>().QueryDirectAsync(CancellationToken.None);
                    throw new InvalidOperationException("场景二十二：直连全部失败未报错。"); }
                catch (ProxyProtocolException) { }
            }
            var cancelOptions = Options("ipzan", textPrimary.Url, jsonOk, directSlow.Url);
            using (var provider = Build(cancelOptions, logger))
            using (var stop = new CancellationTokenSource(30))
            {
                try { await provider.GetRequiredService<IProxyIpInfoProvider>().QueryDirectAsync(stop.Token);
                    throw new InvalidOperationException("场景二十二：直连未响应取消。"); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            report.AppendLine("场景二十二 实际HTTP直连竞速、完整出口详情、不领代理/不设路由/不使用代理TTL、全部失败及取消传播 ✔");
        }
        // 新供应商只验证已支持的 TXT，不假设两家的 JSON 与 ipzan 相同。
        foreach (var id in new[] { "shenlong", "xingyu" })
        {
            string? lastQuery = null;
            using var supplier = new HttpStub(async ctx =>
            {
                lastQuery = ctx.Request.Url!.Query;
                await RespondAsync(ctx, 200, $"invalid\r\nuser:pass@127.0.0.1:{proxy.Port}\r\n");
            });
            var options = Options(id, textPrimary.Url, jsonOk, ipInfoOk);
            foreach (var added in new[] { "shenlong", "xingyu" })
                options.Vendors[added] = new() { ApiUrl = supplier.Url + "?keep=a%26b&exit=old&exit=duplicate",
                    DefaultTtlSeconds = 120, RealIpParameterName = "exit" };
            using var provider = Build(options, logger);
            var service = provider.GetRequiredService<IProxyService>();
            Expect(service.Vendors.Count == 4 && service.Vendors.Contains(id), "场景二十三：新增供应商没有注册。");
            var request = new ProxyAcquireRequest { Settings = new() { ResolveRealIp = false, FetchIpDetails = false } };
            var result = await service.AcquireAsync(request);
            Expect(result.Proxy.VendorId == id && result.Proxy.Port == proxy.Port
                && result.Proxy.Username == "user" && result.Proxy.Password == "pass"
                && result.Proxy.Ttl == TimeSpan.FromSeconds(120) && !result.IpInfoQueried && result.RealIp is null,
                "场景二十三：TXT 认证、有效期或关闭查询开关错误。");
            Expect(lastQuery?.Contains("keep=a%26b") == true && lastQuery.Contains("exit=0")
                && !lastQuery.Contains("old") && !lastQuery.Contains("duplicate")
                && !lastQuery.Contains("realIp=") && !lastQuery.Contains("format="),
                "场景二十三：应保留原参数、替换确认的真实IP参数，不套用ipzan协议。");
            proxy.PathHits.TryGetValue("/ip", out var before);
            var details = await service.AcquireAsync(request with { Settings = new() { ResolveRealIp = true, FetchIpDetails = true } },
                apiUrl: textSecondary.Url);
            proxy.PathHits.TryGetValue("/ip", out var after);
            Expect(details.RealIp == "203.0.113.7" && details.IpInfoQueried && after == before + 1,
                "场景二十三：真实IP与详情同时开启只能补查一次。");
            var unsupported = await ExpectFailureAsync(() => service.AcquireAsync(request, apiUrl: jsonOk), "场景二十三");
            Expect(unsupported.Phase == "acquire" && unsupported.Message.Contains("JSON"), "场景二十三：未知JSON协议应明确失败。");
            var invalid = await ExpectFailureAsync(() => service.AcquireAsync(request, apiUrl: textGarbage.Url), "场景二十三");
            Expect(invalid.Phase == "acquire", "场景二十三：无效TXT应领取失败。");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            try
            {
                await service.AcquireAsync(request, cancellationToken: stop.Token);
                throw new InvalidOperationException("场景二十三：取消未传播。");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            options.Vendors[id].Enabled = false;
            var disabled = await ExpectFailureAsync(() => service.AcquireAsync(request), "场景二十三");
            Expect(disabled.Phase == "route" && !service.Vendors.Contains(id), "场景二十三：禁用供应商不应可领取。");
            report.AppendLine($"场景二十三 {id} 默认路由、TXT认证/TTL、真实IP开关、URL覆盖、单次详情补查、未知JSON/无效TXT失败、取消和禁用 ✔");
        }
        return $"Net 模块端到端自检：\n{report}\n全部通过：Vendors=4，Timeout=distinct，Cancel=propagated，TTL=expiry-fails，Concurrency=isolated，IpInfoRace=fastest-wins，Whitelist=standby，Retry=defect-reacquire，UrlOverride=caller-wins。";
    }

    private static ServiceProvider Build(NetOptions options, ILogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton(logger);
        services.AddTrafficVistaNet(options);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static NetOptions Options(
        string defaultVendor, string textUrl, string jsonUrl, string ipInfoUrl,
        int vendorAMaxConcurrency = 4, int vendorBMaxConcurrency = 4,
        int ipInfoMaxConcurrency = 8, int ipInfoTimeoutSeconds = 15, string[]? ipInfoUrls = null,
        int acquireRetryCount = 0) => new()
        {
            DefaultVendor = defaultVendor,
            IpInfoMaxConcurrency = ipInfoMaxConcurrency,
            AcquireRetryCount = acquireRetryCount,
            IpInfo = new IpInfoOptions
            {
                ApiUrl = ipInfoUrl,
                ApiUrls = ipInfoUrls is null ? new List<string>() : ipInfoUrls.ToList(),
                RequestTimeoutSeconds = ipInfoTimeoutSeconds
            },
            Vendors = new Dictionary<string, ProxyVendorOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ipzan"] = new() { Enabled = true, ApiUrl = textUrl, MaxConcurrency = vendorAMaxConcurrency },
                ["vendor-b"] = new() { Enabled = true, ApiUrl = jsonUrl, MaxConcurrency = vendorBMaxConcurrency }
            }
        };

    private static void AssertIpInfo(JObject info, string scenario)
    {
        Expect(info["query"]?.Value<string>() == "203.0.113.7", $"{scenario}：IpInfo 缺少 query。");
        Expect(info["country"]?.Value<string>() == "中国", $"{scenario}：中文值未完整保留。");
        Expect(info["nested"]?["deep"]?["value"]?.Value<int>() == 42, $"{scenario}：嵌套对象未完整保留。");
        Expect(info["nested"]?["deep"]?["flag"]?.Value<bool>() == true, $"{scenario}：布尔原始值丢失。");
        Expect(info["nested"]?["list"] is JArray { Count: 4 } list && list[3].Value<string>() == "x",
            $"{scenario}：数组混合元素未完整保留。");
        Expect(info["unknown_future_field"]?.Value<string>() == "keep-me", $"{scenario}：未知字段未保留。");
        Expect(info["nullable"]?.Type == JTokenType.Null, $"{scenario}：null 原始值丢失。");
        Expect(info["bignum"]?.Value<long>() == 1234567890123L, $"{scenario}：大整数精度丢失。");
    }

    private static async Task<ProxyServiceException> ExpectFailureAsync(Func<Task> action, string scenario)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (ProxyServiceException ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"{scenario}：应抛出 ProxyServiceException。");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static void UpdateMax(ref int max, int candidate)
    {
        while (true)
        {
            var seen = Volatile.Read(ref max);
            if (candidate <= seen || Interlocked.CompareExchange(ref max, candidate, seen) == seen) return;
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>供应商接口桩：HTTP 监听，记录命中与并发峰值。</summary>
    private sealed class HttpStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerContext, Task> _handler;
        public int Hits;
        public int Concurrent;
        public int MaxConcurrent;
        public string Url { get; }

        public HttpStub(Func<HttpListenerContext, Task> handler)
        {
            _handler = handler;
            Url = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public void ResetCounters()
        {
            Volatile.Write(ref Hits, 0);
            Volatile.Write(ref Concurrent, 0);
            Volatile.Write(ref MaxConcurrent, 0);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(context));
                }
            }
            catch { /* 监听器已停止 */ }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var current = Interlocked.Increment(ref Concurrent);
            UpdateMax(ref MaxConcurrent, current);
            Interlocked.Increment(ref Hits);
            try
            {
                await _handler(context);
            }
            catch
            {
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    /// <summary>假代理桩：接收绝对 URI 请求（证明通道确实经过代理），按路径模拟 IpInfo 接口行为。</summary>
    private sealed class ProxyStub : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _ipInfoBody;
        public int Concurrent;
        public int MaxConcurrent;
        public volatile string? LastRequest;
        /// <summary>各路径被请求的次数（验证多接口竞速同时发起）。</summary>
        public readonly ConcurrentDictionary<string, int> PathHits = new();
        public int Port { get; }

        public ProxyStub(string ipInfoBody)
        {
            _ipInfoBody = ipInfoBody;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public void ResetCounters()
        {
            Volatile.Write(ref Concurrent, 0);
            Volatile.Write(ref MaxConcurrent, 0);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch { /* 已停止 */ }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var current = Interlocked.Increment(ref Concurrent);
            UpdateMax(ref MaxConcurrent, current);
            try
            {
                using (client)
                {
                    await using var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    if (head is null) return;
                    LastRequest = head.Split('\n')[0].Trim();
                    var parts = LastRequest.Split(' ');
                    var path = parts.Length >= 2 && Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)
                        ? uri.AbsolutePath : "";
                    var hitCount = path.Length > 0 ? PathHits.AddOrUpdate(path, 1, (_, n) => n + 1) : 0;
                    var delayMs = path switch { "/ip-slow" => 3_000, "/ip-delay" => 1_600, _ => 0 };
                    if (delayMs > 0) await Task.Delay(delayMs, _stop.Token);
                    int status;
                    byte[] body;
                    switch (path)
                    {
                        case "/ip-404":
                            status = 404;
                            body = Array.Empty<byte>();
                            break;
                        case "/ip-badjson":
                            status = 200;
                            body = Encoding.UTF8.GetBytes("this is { not json");
                            break;
                        case "/ip-noip": // 有效 JSON 但没有真实 IP —— 瑕疵代理
                            status = 200;
                            body = Encoding.UTF8.GetBytes("""{"status":"fail","msg":"网关未透传出口"}""");
                            break;
                        case "/ip-defect" when hitCount <= 2: // 前两次坏，第三次起正常
                            status = 200;
                            body = Encoding.UTF8.GetBytes("gateway broken body");
                            break;
                        default:
                            status = 200;
                            body = Encoding.UTF8.GetBytes(_ipInfoBody);
                            break;
                    }
                    var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                                 "Content-Type: application/json; charset=utf-8\r\n" +
                                 $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch { /* 客户端超时断开或已停止 */ }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
            }
        }

        private async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var builder = new StringBuilder();
            var one = new byte[1];
            while (builder.Length < 8192)
            {
                if (await stream.ReadAsync(one, _stop.Token) <= 0)
                    return builder.Length > 0 ? builder.ToString() : null;
                builder.Append((char)one[0]);
                if (builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            return builder.ToString();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
