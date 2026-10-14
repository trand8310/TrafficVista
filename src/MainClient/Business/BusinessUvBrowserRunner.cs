using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Microsoft.Playwright;
using Serilog;
using TrafficVista.Common;

namespace TrafficVista.Business;

/// <summary>Independent, owned Chrome process per UV, with bounded cleanup and tracked cache writes.</summary>
public sealed class BusinessUvBrowserRunner(IPlaywright playwright, BrowserLaunchLimiter launches,
    SharedStaticResourceCache cache, ILogger logger, BusinessMetrics? metrics = null, BusinessOptions? business = null)
    : IBusinessUvRunner
{
    private readonly BusinessMetrics _metrics = metrics ?? new();
    private readonly BusinessOptions _business = business ?? new();

    public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken token)
    {
        OwnedChromeSession? session = null;
        IBrowserContext? context = null;
        var errors = new List<Exception>();
        var successful = false;
        var actualViews = 0;
        var stage = "launch";
        var stages = new JObject();
        var cacheTasks = new HashSet<Task>();
        var cacheSync = new object();
        var cleanupDeferred = false;
        var gracefulContextCloseTimedOut = false;
        DeviceProfileResult? viewport = null;
        BrowserIpEnvironment? ipEnvironment = null;
        ulong browserPeakBytes = 0;
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            stage = "viewport";
            viewport = MatchViewport(run.Device);
            ipEnvironment = BrowserIpEnvironment.From(run);
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "ip-environment.json"),
                JObject.FromObject(ipEnvironment).ToString(), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "viewport.json"),
                JObject.FromObject(viewport).ToString(), token).ConfigureAwait(false);
            logger.Information("UV屏幕匹配，TaskId={TaskId}，UvId={UvId}，Make={Make}，Model={Model}，Physical={PhysicalWidth}x{PhysicalHeight}，CSS={CssWidth}x{CssHeight}，DPR={Dpr}",
                run.TaskId, run.UvId, run.Device.Make, run.Device.Model, viewport.PhysicalWidth, viewport.PhysicalHeight,
                viewport.CssWidth, viewport.CssHeight, viewport.DeviceScaleFactor);
            if (viewport.IsFallback || viewport.IsSuspiciousInput)
                logger.Warning("UV屏幕参数匹配存在回退或异常比例，TaskId={TaskId}，UvId={UvId}，Viewport={Viewport}", run.TaskId, run.UvId, viewport);
            stage = "launch";
            var timed = Stopwatch.GetTimestamp();
            await launches.ExecuteAsync(async () =>
            {
                var launchStarted = Stopwatch.GetTimestamp();
                stages["launchQueueMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
                _metrics.Stage("launchQueue", Stopwatch.GetElapsedTime(timed));
                session = await OwnedChromeSession.StartAsync(playwright, CreateLaunchOptions(run, viewport, ipEnvironment), run.Directory, token).ConfigureAwait(false);
                _metrics.BrowserOpened();
                context = await AwaitAsync(session.Browser!.NewContextAsync(CreateContextOptions(run, viewport, ipEnvironment)), token).ConfigureAwait(false);
                stages["browserStartMs"] = Stopwatch.GetElapsedTime(launchStarted).TotalMilliseconds;
                _metrics.Stage("browserStart", Stopwatch.GetElapsedTime(launchStarted));
            }, token).ConfigureAwait(false);
            stages["launchMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
            _metrics.Stage("launch", Stopwatch.GetElapsedTime(timed));
            token.ThrowIfCancellationRequested();
            stage = "page";
            var page = await AwaitAsync(context!.NewPageAsync(), token).ConfigureAwait(false);
            page.SetDefaultTimeout(10000);
            page.SetDefaultNavigationTimeout(run.Options.PageLoadingTimeout * 1000);
            await AwaitAsync(page.AddInitScriptAsync(BuildInitScript(run)), token).ConfigureAwait(false);
            var partition = (run.Proxy?.PlaywrightServer ?? "direct") + "\n" + run.Proxy?.IpInfo?.ToString(Newtonsoft.Json.Formatting.None);
            if (run.Options.IsCacheMode)
            {
                page.Response += (_, response) =>
                {
                    if (!cache.TryBeginCapture()) return;
                    var write = RecordAsync(response, partition, captureStop.Token);
                    lock (cacheSync)
                    {
                        cacheTasks.RemoveWhere(task => task.IsCompleted);
                        cacheTasks.Add(write);
                    }
                };
                await page.RouteAsync("**/*", route => ServeAsync(route, partition, token)).ConfigureAwait(false);
            }
            var navigationMs = 0.0;
            for (var view = 1; view <= run.PageViews; view++)
            {
                token.ThrowIfCancellationRequested();
                stage = "navigation";
                timed = Stopwatch.GetTimestamp();
                // One UV retains its page, cookies, storage and normal cache across PVs, like a user refreshing.
                var response = view == 1
                    ? await AwaitAsync(page.GotoAsync(run.Target.AbsoluteUri,
                        new() { WaitUntil = WaitUntilState.Load }), token).ConfigureAwait(false)
                    : await AwaitAsync(page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load }), token).ConfigureAwait(false);
                navigationMs += Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
                if (response is null || !response.Ok)
                    throw new InvalidOperationException($"页面加载失败：HTTP {response?.Status}（第 {view}/{run.PageViews} 次访问）。");
                actualViews++;
                stage = "pageDelay";
                if (view == 1 && run.Options.PageLoadingDelay > 0)
                    await Task.Delay(TimeSpan.FromSeconds((double)run.Options.PageLoadingDelay), token).ConfigureAwait(false);
                stage = "pageActions";
                await OnPageLoadedAsync(page, run, view, token).ConfigureAwait(false);
                stage = "dwell";
                var dwell = run.NextDwell();
                if (dwell > TimeSpan.Zero) await Task.Delay(dwell, token).ConfigureAwait(false);
            }
            stages["navigationMs"] = navigationMs;
            _metrics.Stage("navigation", TimeSpan.FromMilliseconds(navigationMs));
            stage = "storage";
            successful = await AwaitAsync(page.Locator("body").IsVisibleAsync(), token).ConfigureAwait(false);
            timed = Stopwatch.GetTimestamp();
            await AwaitAsync(context.StorageStateAsync(new()
                { Path = Path.Combine(run.Directory, "storage-state.json"), IndexedDB = true }), token).ConfigureAwait(false);
            stages["storageMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
            // Bound each cache capture. No background writes outlive the UV.
            Task[] pending;
            lock (cacheSync) pending = cacheTasks.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            var closing = Stopwatch.GetTimestamp();
            captureStop.Cancel();
            if (context is not null)
            {
                try { gracefulContextCloseTimedOut = !await CloseContextGracefullyAsync(() => context.CloseAsync(),
                    TimeSpan.FromSeconds(3), logger, run.TaskId, run.UvId).ConfigureAwait(false); }
                catch (Exception ex) when (token.IsCancellationRequested) { logger.Debug(ex, "取消后 Context 已退出"); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (session is not null)
            {
                browserPeakBytes = session.PeakMemoryBytes;
                _metrics.BrowserMemory(browserPeakBytes);
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
                finally { _metrics.BrowserClosed(); }
                cleanupDeferred = session.ProfileCleanupDeferred;
            }
            Task[] remaining;
            lock (cacheSync) remaining = cacheTasks.ToArray();
            await Task.WhenAll(remaining).ConfigureAwait(false);
            stages["cleanupMs"] = Stopwatch.GetElapsedTime(closing).TotalMilliseconds;
            _metrics.Stage("cleanup", Stopwatch.GetElapsedTime(closing));
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "result.json"), new JObject
            {
                ["taskId"] = run.TaskId, ["uvId"] = run.UvId, ["deviceKey"] = run.Device.DeviceKey,
                ["proxyServer"] = run.Proxy?.Server, ["pageViews"] = run.PageViews, ["actualPageViews"] = actualViews,
                ["success"] = successful && errors.Count == 0 && !token.IsCancellationRequested,
                ["canceled"] = token.IsCancellationRequested, ["failureStage"] = errors.Count > 0 ? stage : null,
                ["profileCleanupDeferred"] = cleanupDeferred, ["browserPeakCommittedBytes"] = browserPeakBytes,
                ["gracefulContextCloseTimedOut"] = gracefulContextCloseTimedOut,
                ["viewport"] = viewport is null ? JValue.CreateNull() : JObject.FromObject(viewport),
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ["stages"] = stages, ["errors"] = new JArray(errors.Select(error => error.Message).ToArray())
            }.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (errors.Count > 0) throw new AggregateException("业务 UV 执行或清理失败。", errors);
        logger.Information("业务 UV 完成，TaskId={TaskId}，UvId={UvId}，ElapsedMs={ElapsedMs}",
            run.TaskId, run.UvId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return successful;
    }

    /// <summary>
    /// 每个 PV 加载完成后的业务入口；首次 PageLoadingDelay 已结束，sleep 尚未开始。
    /// </summary>
    private static Task OnPageLoadedAsync(IPage page, BusinessUvRun run, int pageView, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // TODO: 在这里添加点击、滚动、执行脚本等页面操作。
        // page 是当前 UV 共用的页面；run 包含任务、设备、IP 和 Jstext；pageView 从 1 开始。
        // 异步操作必须 await 并响应 token，完成后才进入本次 PV 的 sleep 停留。
        return Task.CompletedTask;
    }

    private async Task ServeAsync(IRoute route, string partition, CancellationToken token)
    {
        var headers = await AwaitAsync(route.Request.AllHeadersAsync(), token).ConfigureAwait(false);
        if (route.Request.Method == "GET" && IsCacheableResource(route.Request.ResourceType)
            && !headers.ContainsKey("cookie") && !headers.ContainsKey("authorization") && !headers.ContainsKey("range"))
        {
            var hit = await cache.TryGetAsync(SharedStaticResourceCache.RequestKey(route.Request.Url,
                new Dictionary<string, string>(headers), partition), token).ConfigureAwait(false);
            _metrics.CacheLookup(hit is not null);
            if (hit is not null)
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = hit.ContentType,
                    Headers = hit.Headers, BodyBytes = hit.Body }).ConfigureAwait(false);
                return;
            }
        }
        await route.FallbackAsync().ConfigureAwait(false);
    }

    private async Task RecordAsync(IResponse response, string partition, CancellationToken token)
    {
        var capturedAt = Stopwatch.GetTimestamp();
        try
        {
            if (response.Request.Method != "GET" || !IsCacheableResource(response.Request.ResourceType) || response.Status != 200) return;
            var headers = new Dictionary<string, string>(await AwaitAsync(response.AllHeadersAsync(), token).ConfigureAwait(false));
            var request = new Dictionary<string, string>(await AwaitAsync(response.Request.AllHeadersAsync(), token).ConfigureAwait(false));
            var lifetime = SharedStaticResourceCache.PublicLifetime(request, headers);
            // Unknown/chunked sizes, compressed bodies and media are never buffered into the shared cache.
            if (lifetime is null || headers.ContainsKey("content-encoding") || headers.ContainsKey("transfer-encoding")
                || !long.TryParse(headers.GetValueOrDefault("content-length"), out var length)
                || length is < 1 or > SharedStaticResourceCache.MaxEntryBytes) return;
            var contentType = headers.GetValueOrDefault("content-type", "").Split(';', 2)[0].Trim();
            if (!IsCacheableContentType(contentType)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var bodyTask = response.BodyAsync();
            byte[] body;
            try { body = await bodyTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch { _ = bodyTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
            if (body.LongLength != length) return;
            headers.Remove("content-length");
            await cache.StoreAsync(SharedStaticResourceCache.RequestKey(response.Request.Url, request, partition),
                contentType, headers, body, lifetime.Value - Stopwatch.GetElapsedTime(capturedAt), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { logger.Debug(ex, "静态缓存记录已放弃"); }
        finally { cache.EndCapture(); }
    }

    internal static async Task<bool> CloseContextGracefullyAsync(Func<Task> closeContext, TimeSpan timeout,
        ILogger logger, long taskId, long uvId)
    {
        Task? close = null;
        try { close = closeContext(); await close.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException ex)
        {
            // The caller must still kill and verify the owned Chrome job, and report any forced-cleanup failure.
            if (close is not null) _ = close.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            logger.Warning(ex, "Context 优雅关闭超时，继续终止并确认自有 Chrome 进程树退出，TaskId={TaskId}，UvId={UvId}", taskId, uvId);
            return false;
        }
    }

    private static async Task<T> AwaitAsync<T>(Task<T> operation, CancellationToken token)
    {
        try { return await operation.WaitAsync(token).ConfigureAwait(false); }
        catch { _ = operation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
    }
    private static async Task AwaitAsync(Task operation, CancellationToken token)
    {
        try { await operation.WaitAsync(token).ConfigureAwait(false); }
        catch { _ = operation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
    }
    private static bool IsCacheableResource(string type) => type is "image" or "stylesheet" or "font";
    private static bool IsCacheableContentType(string type) =>
        type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || type.Equals("text/css", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("font/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/font", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/x-font", StringComparison.OrdinalIgnoreCase) || type.Equals("application/vnd.ms-fontobject", StringComparison.OrdinalIgnoreCase);

    internal BrowserTypeLaunchOptions CreateLaunchOptions(BusinessUvRun run, DeviceProfileResult? viewport = null, BrowserIpEnvironment? ipEnvironment = null)
    {
        var device = run.Device;
        var executable = Path.GetFullPath(_business.ChromeExecutablePath, AppContext.BaseDirectory);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Chrome 145。", executable);
        viewport ??= MatchViewport(device);
        ipEnvironment ??= BrowserIpEnvironment.From(run);
        var (width, height, scale) = (viewport.CssWidth, viewport.CssHeight, viewport.DeviceScaleFactor);
        var args = new List<string>
        {
            "--lang=zh-CN", "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
            "--disable-component-update", "--disable-sync", "--ignore-certificate-errors", "--enable-unsafe-swiftshader",
            "--show-avatar-button=never", "--hide-bad-flags", "--hide-crashed-bubble",
            "--force-prefers-no-reduced-motion",
            // 出口 IP 泄露兜底：只允许走代理的 UDP，直连时也不暴露真实内网地址。
            "--webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--touch-events=enabled", "--max-touch-points=5",
            "--netinfo-type=4g", "--netinfo-effective=4g", "--netinfo-rtt=200",
            "--enable-battery-charging=0", "--battery-level=0.85",
            "--screen-color-depth=24", "--window-position=0,0",
            $"--window-size={width},{height}",
            $"--device-pixel-ratio={scale.ToString(CultureInfo.InvariantCulture)}",
            $"--screen-size={width},{height}", $"--screen-avail-size={width},{height}",
            // 指纹启动参数按设备接口逐台生成，与 ChromeStartup 的演示参数同一套自定义 Chrome 能力。
            $"--user-agent={device.UserAgent}",
            $"--hardware-concurrency={device.CpuCores}",
            "--device-memory=8",
            $"--fingerprint={System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(device.DeviceKey))) % 1000000}"
        };
        if (device.Model.Length > 0) args.Add($"--product-model={device.Model}");
        if (device.Make.Length > 0) args.Add($"--make={device.Make}");
        if (device.OsVersion.Length > 0) args.Add($"--platform-version={device.OsVersion}");
        args.Add("--platform=Android");
        if (device.Renderer.Length > 0)
        {
            var vendor = device.Vendor.Length > 0 ? device.Vendor : device.Make;
            args.Add($"--webgl-vendor=Google Inc. ({vendor})");
            args.Add($"--webgl-renderer=ANGLE ({vendor}, {device.Renderer}, OpenGL ES 3.2)");
        }
        Proxy? proxy = null;
        if (run.Proxy?.HasProxy == true)
        {
            proxy = new Proxy { Server = run.Proxy.PlaywrightServer!, Username = run.Proxy.Username, Password = run.Proxy.Password };
            // Chrome 默认对本机地址直连；代理模式下连 127.0.0.1 的目标也必须从代理出口走，否则自检无法验证代理路径。
            args.Add("--proxy-bypass-list=<-loopback>");
        }
        if (ipEnvironment.RealIp is { } realIp) args.Add($"--webrtc-ip={realIp}");
        return new BrowserTypeLaunchOptions
        {
            ExecutablePath = executable,
            Headless = run.Options.IsHiddenMode,
            SlowMo = 0,
            Timeout = 30_000,
            Proxy = proxy,
            Args = args
        };
    }

    internal static BrowserNewContextOptions CreateContextOptions(BusinessUvRun run, DeviceProfileResult? viewport = null, BrowserIpEnvironment? ipEnvironment = null)
    {
        viewport ??= MatchViewport(run.Device);
        ipEnvironment ??= BrowserIpEnvironment.From(run);
        var (width, height, scale) = (viewport.CssWidth, viewport.CssHeight, viewport.DeviceScaleFactor);
        return new BrowserNewContextOptions
        {
            HasTouch = true, IsMobile = true, UserAgent = run.Device.UserAgent,
            Proxy = run.Proxy?.HasProxy != true ? null : new Proxy { Server = run.Proxy.PlaywrightServer!,
                Username = run.Proxy.Username, Password = run.Proxy.Password },
            ServiceWorkers = run.Options.IsCacheMode ? ServiceWorkerPolicy.Block : ServiceWorkerPolicy.Allow,
            ViewportSize = new() { Width = width, Height = height },
            ScreenSize = new() { Width = width, Height = height },
            DeviceScaleFactor = scale, Locale = "zh-CN", IgnoreHTTPSErrors = true,
            TimezoneId = ipEnvironment.TimezoneId,
            Geolocation = ipEnvironment.Latitude is { } latitude && ipEnvironment.Longitude is { } longitude
                ? new Geolocation { Latitude = (float)latitude, Longitude = (float)longitude } : null,
            Permissions = ipEnvironment.Latitude is not null ? new[] { "geolocation" } : null
        };
    }

    /// <summary>One deterministic Android model/resolution match, shared by launch flags and context emulation.</summary>
    private static DeviceProfileResult MatchViewport(BusinessDeviceProfile device) =>
        AndroidViewportMatcher.Match(device.ScreenWidth, device.ScreenHeight, device.Make, device.Model);

    /// <summary>jstext 原样解析为 JObject 注入页面；页面脚本据此执行各自的广告/交互逻辑，空任务为 null。</summary>
    private static string BuildInitScript(BusinessUvRun run) => "window.__trafficVistaTask = " + new JObject
    {
        ["taskId"] = run.TaskId, ["uvId"] = run.UvId, ["deviceKey"] = run.Device.DeviceKey,
        ["pageViews"] = run.PageViews, ["jstext"] = run.Jstext
    }.ToString(Newtonsoft.Json.Formatting.None);
}
