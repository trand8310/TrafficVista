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
    internal Action<IBrowser, IPage>? BrowserReadyForTest { get; set; }

    public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken cancellationToken)
    {
        using var lifetime = new BrowserLifetimeGuard(cancellationToken);
        var token = lifetime.Token;
        OwnedChromeSession? session = null;
        IBrowserContext? context = null;
        IPage page = null!;
        int? latestDocumentStatus = null;
        string? latestDocumentFailure = null;
        EventHandler<IResponse> documentResponse = (_, response) =>
        {
            if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
            { latestDocumentStatus = response.Status; latestDocumentFailure = null; }
        };
        EventHandler<IRequest> documentFailure = (_, request) =>
        {
            if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                latestDocumentFailure = request.Failure;
        };
        var errors = new List<Exception>();
        var successful = false;
        var actualViews = 0;
        var stage = "launch";
        IDisposable? activeStage = null;
        void SetStage(string value)
        {
            activeStage?.Dispose();
            stage = value;
            var name = value switch
            {
                "viewport" => "环境设置",
                "launch" => "浏览器启动排队",
                "browserStart" => "浏览器启动",
                "context" => "会话创建",
                "page" => "页面创建",
                "cdp" => "CDP环境设置",
                "pageInit" => "页面初始化",
                "navigation" => "页面导航",
                "pageDelay" => "首次延时",
                "pageActions" => "页面操作",
                "dwell" => "停留",
                "storage" => "导出会话",
                "contentCheck" => "正文检查",
                "cacheDrain" => "缓存收尾",
                _ => value
            };
            activeStage = _metrics.Track(name);
        }
        var stages = new JObject();
        var navigationWarnings = new List<string>();
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
            SetStage("viewport");
            viewport = MatchViewport(run.Device);
            ipEnvironment = BrowserIpEnvironment.From(run);
            if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(run.Directory, "ip-environment.json"),
                JObject.FromObject(ipEnvironment).ToString(), token).ConfigureAwait(false);
            if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(run.Directory, "viewport.json"),
                JObject.FromObject(viewport).ToString(), token).ConfigureAwait(false);
            logger.Information("UV屏幕匹配，TaskId={TaskId}，UvId={UvId}，Make={Make}，Model={Model}，Physical={PhysicalWidth}x{PhysicalHeight}，CSS={CssWidth}x{CssHeight}，DPR={Dpr}，DprSource={DprSource}，Strategy={Strategy}",
                run.TaskId, run.UvId, run.Device.Make, run.Device.Model, viewport.PhysicalWidth, viewport.PhysicalHeight,
                viewport.CssWidth, viewport.CssHeight, viewport.DeviceScaleFactor, viewport.DprSource, viewport.MatchStrategy);
            if (viewport.IsFallback || viewport.IsSuspiciousInput)
                logger.Warning("UV屏幕映射超出估算覆盖范围，TaskId={TaskId}，UvId={UvId}，Viewport={Viewport}", run.TaskId, run.UvId, viewport);
            SetStage("launch");
            var timed = Stopwatch.GetTimestamp();
            await launches.ExecuteAsync(async () =>
            {
                SetStage("browserStart");
                var launchStarted = Stopwatch.GetTimestamp();
                stages["launchQueueMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
                _metrics.Stage("launchQueue", Stopwatch.GetElapsedTime(timed));
                session = await OwnedChromeSession.StartAsync(playwright, CreateLaunchOptions(run, viewport, ipEnvironment), run.Directory, token).ConfigureAwait(false);
                stages["browserLaunchMs"] = Stopwatch.GetElapsedTime(launchStarted).TotalMilliseconds;
                _metrics.Stage("browserLaunch", Stopwatch.GetElapsedTime(launchStarted));
                _metrics.BrowserOpened();
                lifetime.Watch(session.Browser!);
                var setupStarted = Stopwatch.GetTimestamp();
                await RunSetupAsync(async setupToken =>
                {
                    SetStage("context");
                    context = await AwaitAsync(session.Browser.NewContextAsync(CreateContextOptions(run, viewport, ipEnvironment)), setupToken).ConfigureAwait(false);
                    lifetime.Watch(context);
                    SetStage("page");
                    page = await AwaitAsync(context.NewPageAsync(), setupToken).ConfigureAwait(false);
                    lifetime.Watch(page);
                    page.Response += documentResponse;
                    page.RequestFailed += documentFailure;
                    SetStage("cdp");
                    //var cdp = await AwaitAsync(context.NewCDPSessionAsync(page), setupToken).ConfigureAwait(false);
                    //await AwaitAsync(cdp.SendAsync("Network.enable"), setupToken).ConfigureAwait(false);
                    //await AwaitAsync(cdp.SendAsync("Network.setBlockedURLs",
                    //    new Dictionary<string, object>
                    //    {
                    //        ["urls"] = new[]
                    //        {
                    //            "*.png",
                    //            "*.jpg",
                    //            "*.jpeg",
                    //            "*.webp",
                    //            "*.mp4",
                    //            "*.webm",
                    //            "*.m3u8"
                    //        }
                    //    }), setupToken).ConfigureAwait(false);

                    //await page.RouteAsync("**/*", async route =>
                    //{
                    //    if (route.Request.ResourceType == "image" ||
                    //        route.Request.ResourceType == "media")
                    //    {
                    //        await route.AbortAsync();
                    //        return;
                    //    }

                    //    await route.ContinueAsync();
                    //});

                    SetStage("pageInit");
                    page.SetDefaultTimeout(10000);
                    page.SetDefaultNavigationTimeout(run.Options.PageLoadingTimeout * 1000);
                    await AwaitAsync(page.AddInitScriptAsync(BuildInitScript(run)), setupToken).ConfigureAwait(false);
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
                        await AwaitAsync(page.RouteAsync("**/*", route => ServeAsync(route, partition, token)), setupToken).ConfigureAwait(false);
                    }
                }, _business.BrowserSetupTimeoutSeconds, () => stage, session.Abort, token).ConfigureAwait(false);
                stages["browserSetupMs"] = Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds;
                _metrics.Stage("browserSetup", Stopwatch.GetElapsedTime(setupStarted));
                stages["browserStartMs"] = Stopwatch.GetElapsedTime(launchStarted).TotalMilliseconds;
                _metrics.Stage("browserStart", Stopwatch.GetElapsedTime(launchStarted));
            }, token).ConfigureAwait(false);
            stages["launchMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
            _metrics.Stage("launch", Stopwatch.GetElapsedTime(timed));
            token.ThrowIfCancellationRequested();
            BrowserReadyForTest?.Invoke(session!.Browser!, page);
            var navigationMs = 0.0;
            for (var view = 1; view <= run.PageViews; view++)
            {
                token.ThrowIfCancellationRequested();
                SetStage("navigation");
                timed = Stopwatch.GetTimestamp();
                // One UV retains its page, cookies, storage and normal cache across PVs, like a user refreshing.
                if (view == 1)
                {
                    var opened = await InitialPageNavigation.OpenAsync(page, run.Target.AbsoluteUri, logger,
                        run.TaskId, run.UvId, token, run.Options.PageLoadingTimeout).ConfigureAwait(false);
                    navigationWarnings.AddRange(opened.Warnings);
                }
                else
                {
                    var response = await AwaitAsync(page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load }), token).ConfigureAwait(false);
                    if (response?.Status is 644 or 407) throw new PageHttpStatusException(response.Status, response.Url);
                    if (response is null || !response.Ok)
                        throw new InvalidOperationException($"页面加载失败：HTTP {response?.Status}（第 {view}/{run.PageViews} 次访问）。");
                }
                navigationMs += Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
                actualViews++;
                SetStage("pageDelay");
                if (view == 1 && run.Options.PageLoadingDelay > 0)
                    await Task.Delay(TimeSpan.FromSeconds((double)run.Options.PageLoadingDelay), token).ConfigureAwait(false);
                SetStage("pageActions");
                await OnPageLoadedAsync(page, run, view, token).ConfigureAwait(false);
                SetStage("dwell");
                var dwell = run.NextDwell();
                if (dwell > TimeSpan.Zero) await Task.Delay(dwell, token).ConfigureAwait(false);
            }
            stages["navigationMs"] = navigationMs;
            _metrics.Stage("navigation", TimeSpan.FromMilliseconds(navigationMs));
            SetStage("contentCheck");
            await PageContentInspector.EnsureFinalContentAsync(page, () => latestDocumentStatus,
                () => latestDocumentFailure, token).ConfigureAwait(false);
            successful = true;
            if (_business.ExportRunData)
            {
                SetStage("storage");
                timed = Stopwatch.GetTimestamp();
                using var exportTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                exportTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                Task<string>? snapshot = null;
                try
                {
                    snapshot = context!.StorageStateAsync(new() { IndexedDB = false });
                    var state = await snapshot.WaitAsync(exportTimeout.Token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(run.Directory, "storage-state.json"), state, exportTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    logger.Warning(ex, "会话快照未导出，跳过保存并继续关闭浏览器，TaskId={TaskId}，UvId={UvId}", run.TaskId, run.UvId);
                }
                finally
                {
                    if (snapshot is not null) _ = snapshot.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
                stages["storageMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
            }
            SetStage("cacheDrain");
            // Bound each cache capture. No background writes outlive the UV.
            Task[] pending;
            lock (cacheSync) pending = cacheTasks.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Classify only main-document browser failures, never failed images/scripts or cleanup messages.
            var proxyCode = ProxyUnavailableException.BrowserCode(latestDocumentFailure)
                ?? (stage == "navigation" && ex is PlaywrightException ? ProxyUnavailableException.BrowserCode(ex.Message) : null);
            errors.Add(!token.IsCancellationRequested && proxyCode is not null
                ? new ProxyUnavailableException(proxyCode, ex) : ex);
        }
        finally
        {
            if (page is not null)
            { page.Response -= documentResponse; page.RequestFailed -= documentFailure; }
            activeStage?.Dispose();
            using var cleanupStage = _metrics.Track("清理");
            lifetime.StopWatching();
            if (lifetime.Reason is { } closeReason && !cancellationToken.IsCancellationRequested)
            {
                errors.RemoveAll(error => error is OperationCanceledException);
                errors.Add(new InvalidOperationException(closeReason));
                successful = false;
                logger.Warning("业务浏览器非预期退出，TaskId={TaskId}，UvId={UvId}，Stage={Stage}，Reason={Reason}；结束当前UV并释放资源",
                    run.TaskId, run.UvId, stage, closeReason);
            }
            var closing = Stopwatch.GetTimestamp();
            captureStop.Cancel();
            if (context is not null)
            {
                try
                {
                    gracefulContextCloseTimedOut = !await CloseContextGracefullyAsync(() => context.CloseAsync(),
                    TimeSpan.FromSeconds(3), logger, run.TaskId, run.UvId).ConfigureAwait(false);
                }
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
            if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(run.Directory, "result.json"), new JObject
            {
                ["taskId"] = run.TaskId,
                ["uvId"] = run.UvId,
                ["deviceKey"] = run.Device.DeviceKey,
                ["hiddenMode"] = run.Options.IsHiddenMode,
                ["proxyServer"] = run.Proxy?.Server,
                ["pageViews"] = run.PageViews,
                ["actualPageViews"] = actualViews,
                ["success"] = successful && errors.Count == 0 && !token.IsCancellationRequested,
                ["canceled"] = cancellationToken.IsCancellationRequested,
                ["failureStage"] = errors.Count > 0 ? stage : null,
                ["browserInterrupted"] = lifetime.Reason is not null && !cancellationToken.IsCancellationRequested,
                ["browserInterruptionReason"] = lifetime.Reason,
                ["profileCleanupDeferred"] = cleanupDeferred,
                ["browserPeakCommittedBytes"] = browserPeakBytes,
                ["gracefulContextCloseTimedOut"] = gracefulContextCloseTimedOut,
                ["viewport"] = viewport is null ? JValue.CreateNull() : JObject.FromObject(viewport),
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ["stages"] = stages,
                ["errors"] = new JArray(errors.Select(error => error.Message).ToArray()),
                ["navigationWarnings"] = new JArray(navigationWarnings)
            }.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Count > 0)
        {
            _metrics.Failure(stage);
            throw new AggregateException("业务 UV 执行或清理失败。", errors);
        }
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

    internal async Task ServeAsync(IRoute route, string partition, CancellationToken token)
    {
        if (route.Request.Method == "GET" && (IsCacheableResource(route.Request.ResourceType) || IsVideoUrl(route.Request.Url)) && !IsGifUrl(route.Request.Url))
        {
            var headers = new Dictionary<string, string>(await AwaitAsync(route.Request.AllHeadersAsync(), token).ConfigureAwait(false));
            // URL-only reuse is explicitly enabled, independent of request identity.
            using var hit = await cache.TryGetFileAsync(SharedStaticResourceCache.RequestKey(route.Request.Url, headers, partition), token).ConfigureAwait(false);
            _metrics.CacheLookup(hit is not null);
            if (hit is not null)
            {
                await route.FulfillAsync(new()
                {
                    Status = hit.Status, ContentType = hit.Headers.GetValueOrDefault("content-type", hit.ContentType),
                    Headers = hit.Headers, Path = hit.Path
                }).ConfigureAwait(false);
                return;
            }
        }
        await route.FallbackAsync().ConfigureAwait(false);
    }

    internal async Task RecordAsync(IResponse response, string partition, CancellationToken token)
    {
        try
        {
            if (response.Request.Method != "GET" || !(IsCacheableResource(response.Request.ResourceType) || IsVideoUrl(response.Request.Url))
                || response.Status is not (200 or 206) || IsGifUrl(response.Request.Url)) return;
            var headers = new Dictionary<string, string>(await AwaitAsync(response.AllHeadersAsync(), token).ConfigureAwait(false));
            var request = new Dictionary<string, string>(await AwaitAsync(response.Request.AllHeadersAsync(), token).ConfigureAwait(false));
            var contentType = headers.GetValueOrDefault("content-type", "").Split(';', 2)[0].Trim();
            if (!IsCacheableContentType(contentType)) return;
            var key = SharedStaticResourceCache.RequestKey(response.Request.Url, request, partition);
            using (var existing = await cache.TryGetFileAsync(key, token).ConfigureAwait(false))
                if (existing is not null) return; // Do not re-capture hits or extend their configured TTL.
            // Playwright returns the decoded body, including gzip/br and chunked responses.
            // There is no per-resource size or two-second capture limit; UV cancellation still applies.
            var bodyTask = response.BodyAsync();
            byte[] body;
            try { body = await bodyTask.WaitAsync(token).ConfigureAwait(false); }
            catch { _ = bodyTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
            if (!headers.ContainsKey("content-encoding") && long.TryParse(headers.GetValueOrDefault("content-length"), out var length)
                && body.LongLength != length) return;
            if (response.Status == 206)
            {
                var range = System.Text.RegularExpressions.Regex.Match(headers.GetValueOrDefault("content-range", ""),
                    @"^bytes (\d+)-(\d+)/(\d+|\*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!request.ContainsKey("range") || headers.ContainsKey("content-encoding") || !range.Success
                    || !long.TryParse(range.Groups[1].Value, out var first) || !long.TryParse(range.Groups[2].Value, out var last)
                    || last < first || last - first != body.LongLength - 1
                    || (range.Groups[3].Value != "*" && (!long.TryParse(range.Groups[3].Value, out var total) || last >= total))) return;
            }
            foreach (var header in new[] { "content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive",
                "proxy-authenticate", "proxy-authorization", "te", "trailer", "upgrade", "content-md5", "digest", "etag", "set-cookie" }) headers.Remove(header);
            await cache.StoreAsync(key,
                contentType, headers, body, token, response.Status).ConfigureAwait(false);
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

    internal static async Task RunSetupAsync(Func<CancellationToken, Task> initialize, int seconds,
        Func<string> stage, Action abort, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        Task? operation = null;
        try
        {
            operation = initialize(deadline.Token);
            await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Kill this UV's process tree before another launch can take its setup slot.
            abort();
            if (operation is not null) _ = operation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (ex is OperationCanceledException && !token.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException($"浏览器准备超时（{seconds}秒），卡在 {stage()}，已终止本UV浏览器并释放启动名额。", ex);
            throw;
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
    private static bool IsCacheableResource(string type) => type is "image" or "stylesheet" or "script" or "font" or "media";
    private static bool IsCacheableContentType(string type) =>
        (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
        || type.Equals("text/css", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
        || type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
        || type.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/font", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/x-font", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/vnd.ms-fontobject", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    private static bool IsVideoUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && new[] { ".mp4", ".webm", ".m3u8", ".mpd", ".ts", ".m4s" }.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
    private static bool IsGifUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Uri.UnescapeDataString(uri.AbsolutePath).EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
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
            //"--blink-settings=imagesEnabled=false",
            "--lang=zh-CN", "--no-first-run",
            "--show-avatar-button=never", "--hide-bad-flags", "--hide-crashed-bubble",
            // 出口 IP 泄露兜底：只允许走代理的 UDP，直连时也不暴露真实内网地址。
            "--webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--touch-events=enabled", "--max-touch-points=5",
            "--screen-color-depth=24", "--window-position=0,0",
            $"--window-size={width},{height}",
            $"--device-pixel-ratio={scale.ToString(CultureInfo.InvariantCulture)}",
            $"--screen-size={width},{height}", $"--screen-avail-size={width},{height}",
            // 指纹启动参数按设备接口逐台生成，与 ChromeStartup 的演示参数同一套自定义 Chrome 能力。
            $"--user-agent={device.UserAgent}",
            $"--hardware-concurrency={device.CpuCores}",
            "--device-memory=8",
            "--incognito",
            "--enable-incognito-themes",
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
            HasTouch = true,
            IsMobile = true,
            UserAgent = run.Device.UserAgent,
            Proxy = run.Proxy?.HasProxy != true ? null : new Proxy
            {
                Server = run.Proxy.PlaywrightServer!,
                Username = run.Proxy.Username,
                Password = run.Proxy.Password
            },
            ServiceWorkers = run.Options.IsCacheMode ? ServiceWorkerPolicy.Block : ServiceWorkerPolicy.Allow,
            ViewportSize = new() { Width = width, Height = height },
            ScreenSize = new() { Width = width, Height = height },
            DeviceScaleFactor = scale,
            Locale = "zh-CN",
            IgnoreHTTPSErrors = true,
            TimezoneId = ipEnvironment.TimezoneId,
            Geolocation = ipEnvironment.Latitude is { } latitude && ipEnvironment.Longitude is { } longitude
                ? new Geolocation { Latitude = (float)latitude, Longitude = (float)longitude } : null,
            Permissions = ipEnvironment.Latitude is not null ? new[] { "geolocation" } : null
        };
    }

    /// <summary>One deterministic Android model/resolution match, shared by launch flags and context emulation.</summary>
    private static DeviceProfileResult MatchViewport(BusinessDeviceProfile device) =>
        AndroidViewportMatcher.Match(device.ScreenWidth, device.ScreenHeight, device.Make, device.Model, device.DevicePixelRatio);

    /// <summary>jstext 原样解析为 JObject 注入页面；页面脚本据此执行各自的广告/交互逻辑，空任务为 null。</summary>
    private static string BuildInitScript(BusinessUvRun run) => "window.__trafficVistaTask = " + new JObject
    {
        ["taskId"] = run.TaskId,
        ["uvId"] = run.UvId,
        ["deviceKey"] = run.Device.DeviceKey,
        ["pageViews"] = run.PageViews,
        ["jstext"] = run.Jstext
    }.ToString(Newtonsoft.Json.Formatting.None);
}
