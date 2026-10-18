using System.Diagnostics;
using System.Collections.Concurrent;
using TrafficVista.Caching;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Microsoft.Playwright;
using Serilog;
using TrafficVista.Common;
using TrafficVista.Scheduling;
using TrafficVista.Configuration;

namespace TrafficVista.Business;

/// <summary>Independent, owned Chrome process per UV, with bounded cleanup and tracked cache writes.</summary>
public sealed class BusinessUvBrowserRunner(IPlaywright? playwright, BrowserLaunchLimiter launches,
    SharedStaticResourceCache cache, ILogger logger, BusinessMetrics? metrics = null, BusinessOptions? business = null,
    BusinessPlaywrightDrivers? drivers = null)
    : IBusinessUvRunner
{
    private readonly BusinessMetrics _metrics = metrics ?? new();
    private readonly BusinessOptions _business = business ?? new();
    internal Action<IBrowser, IPage>? BrowserReadyForTest { get; set; }
    private sealed class DriverUse : IDisposable
    {
        internal BusinessPlaywrightDrivers.Lease? Lease;
        public void Dispose() => Lease?.Dispose();
    }

    public async Task<bool> RunAsync(BusinessUvRun run, CancellationToken cancellationToken)
    {
        using var worker = WorkerExecutionContext.Enter(run.WorkerId);
        using var driverUse = new DriverUse();
        var driverId = drivers?.DriverIdFor(run.WorkerId) ?? 1;
        using var lifetime = new BrowserLifetimeGuard(cancellationToken);
        var token = lifetime.Token;
        OwnedChromeSession? session = null;
        IBrowserContext? context = null;
        IPage page = null!;
        var origins = new ConcurrentDictionary<IRequest, CacheAccess>(ReferenceEqualityComparer.Instance);
        int? latestDocumentStatus = null;
        string? latestDocumentFailure = null;
        EventHandler<IResponse> documentResponse = (_, response) =>
        {
            if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
            { latestDocumentStatus = response.Status; latestDocumentFailure = null; }
        };
        EventHandler<IRequest> documentFailure = (_, request) =>
        {
            if (origins.TryRemove(request, out var abandoned)) abandoned.Dispose();
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
                "playwrightLaunch" => "Playwright启动",
                "driverStart" => "Playwright驱动准备",
                "launchCdpConnect" => "启动CDP连接",
                "launchProcessQuery" => "启动进程查询",
                "launchProcessAttach" => "启动进程绑定",
                "launchMainProcessAttach" => "启动主进程绑定",
                "launchProcessSnapshot" => "启动子进程扫描",
                "launchChildProcessAttach" => "启动子进程绑定",
                "launchCdpDetach" => "启动CDP断开",
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
        // Diagnostic CDP teardown overlaps page setup; timing writers must not mutate a shared JObject.
        var stages = new ConcurrentDictionary<string, double>();
        JObject StageSnapshot() => JObject.FromObject(stages.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value));
        IDisposable TrackLaunchPhase(string phase)
        {
            SetStage(phase);
            return new StartupTiming(activeStage!, elapsed =>
            {
                stages[phase + "Ms"] = elapsed.TotalMilliseconds;
                if (elapsed.TotalSeconds >= 3)
                    logger.Warning("浏览器启动慢阶段结束（含失败或取消），Phase={Phase}，ElapsedMs={ElapsedMs:F0}，TaskId={TaskId}，UvId={UvId}，资源={ResourceState}",
                        phase, elapsed.TotalMilliseconds, run.TaskId, run.UvId, ResourceState());
            });
        }
        IDisposable TrackCleanupPhase(string phase)
        {
            var name = phase switch
            {
                "cleanupContextClose" => "清理Context关闭",
                "cleanupCloseQueue" => "清理驱动收尾排队",
                "cleanupCancelRegistration" => "清理取消回调",
                "cleanupBrowserClose" => "清理Browser关闭",
                "cleanupTerminate" => "清理终止进程树",
                "cleanupMainProcessExit" => "清理主进程退出",
                "cleanupChildProcessExit" => "清理子进程退出",
                "cleanupJobDispose" => "清理Job释放",
                "cleanupCacheDrain" => "清理缓存回调",
                "launchCdpDetach" => "启动CDP断开（后台）",
                _ => phase
            };
            return new StartupTiming(_metrics.Track(name), elapsed =>
            {
                stages[phase + "Ms"] = elapsed.TotalMilliseconds;
                if (elapsed.TotalSeconds >= 1)
                    logger.Warning("浏览器辅助/清理慢阶段结束（含失败或取消），Phase={Phase}，ElapsedMs={ElapsedMs:F0}，TaskId={TaskId}，UvId={UvId}，资源={ResourceState}",
                        phase, elapsed.TotalMilliseconds, run.TaskId, run.UvId, ResourceState());
            });
        }
        void CleanupNotice(string message, Exception? error)
        {
            if (error is null)
                logger.Information("浏览器清理诊断，TaskId={TaskId}，UvId={UvId}，Detail={Detail}，资源={ResourceState}", run.TaskId, run.UvId, message, ResourceState());
            else
                logger.Warning(error, "浏览器清理异常，TaskId={TaskId}，UvId={UvId}，Detail={Detail}，资源={ResourceState}", run.TaskId, run.UvId, message, ResourceState());
        }
        string ResourceState()
        {
            var sample = SystemPressureProbe.Shared.Latest;
            return (sample.Available ? $"CPU={sample.CpuPercent:F1}%，可用物理内存={sample.AvailablePhysicalBytes / 1048576.0:F0}MiB，可用提交量={sample.AvailableCommitBytes / 1048576.0:F0}MiB"
                : "资源采样尚不可用：" + sample.UnavailableReason)
                + $"，启动排队={launches.WaitingStarts}，启动上限={launches.MaxConcurrentStarts}，当前资源启动额度={launches.EffectiveStartLimit}，DriverId={driverId}";
        }
        var navigationWarnings = new List<string>();
        EventHandler<IResponse>? cacheResponse = null;
        var cleanupDeferred = false;
        var gracefulContextCloseTimedOut = false;
        DeviceProfileResult? viewport = null;
        BrowserIpEnvironment? ipEnvironment = null;
        ulong browserPeakBytes = 0;
        int? browserProcessId = null;
        await using var cacheCallbacks = new UvCacheCallbacks(token, ex =>
            logger.Warning(ex, "UV缓存回调异常已隔离，TaskId={TaskId}，UvId={UvId}，DriverId={DriverId}", run.TaskId, run.UvId, driverId));
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
                stages["launchQueueMs"] = Stopwatch.GetElapsedTime(timed).TotalMilliseconds;
                _metrics.Stage("launchQueue", Stopwatch.GetElapsedTime(timed));
                var selectedPlaywright = playwright;
                if (drivers is not null)
                {
                    using (TrackLaunchPhase("driverStart"))
                        driverUse.Lease = await drivers.AcquireAsync(run.WorkerId, token).ConfigureAwait(false);
                    driverId = driverUse.Lease.Id;
                    selectedPlaywright = driverUse.Lease.Driver;
                }
                if (selectedPlaywright is null) throw new InvalidOperationException("未配置Playwright驱动。");
                SetStage("browserStart");
                var launchStarted = Stopwatch.GetTimestamp();
                var launchPressure = ResourceState();
                session = await OwnedChromeSession.StartAsync(selectedPlaywright, CreateLaunchOptions(run, viewport, ipEnvironment), run.Directory, token,
                    TrackLaunchPhase, TrackCleanupPhase, CleanupNotice, elapsed =>
                    {
                        stages["launchProcessAttachMs"] = elapsed.TotalMilliseconds;
                        _metrics.Stage("启动进程绑定", elapsed);
                    }, TimeSpan.FromMilliseconds(_business.BrowserCloseGraceMilliseconds), _metrics.BrowserStartAttempted,
                    TrackCleanupPhase).ConfigureAwait(false);
                browserProcessId = session.ProcessId;
                stages["browserLaunchMs"] = Stopwatch.GetElapsedTime(launchStarted).TotalMilliseconds;
                _metrics.Stage("browserLaunch", Stopwatch.GetElapsedTime(launchStarted));
                logger.Information("浏览器启动诊断，TaskId={TaskId}，UvId={UvId}，PID={PID}，阶段毫秒={StageMilliseconds}，启动前={Before}，启动后={After}",
                    run.TaskId, run.UvId, session.ProcessId, StageSnapshot().ToString(Newtonsoft.Json.Formatting.None), launchPressure, ResourceState());
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
                        var cachePolicy = new ResourceCachePolicy(run.Options);
                        cacheResponse = (_, response) =>
                        {
                            using var callbackWorker = WorkerExecutionContext.Enter(run.WorkerId);
                            _ = cacheCallbacks.RunAsync(captureToken => RecordAsync(response, partition, captureToken, origins,
                                run.TaskId, run.UvId, cachePolicy));
                        };
                        page.Response += cacheResponse;
                        await AwaitAsync(page.RouteAsync("**/*", route =>
                        {
                            using var callbackWorker = WorkerExecutionContext.Enter(run.WorkerId);
                            return cacheCallbacks.RunAsync(async captureToken =>
                            {
                                try { await ServeAsync(route, partition, captureToken, origins, cachePolicy).ConfigureAwait(false); }
                                catch (Exception) when (captureToken.IsCancellationRequested) { }
                                catch (Exception ex)
                                {
                                    logger.Debug(ex, "静态缓存路由失败，尝试原始网络请求，TaskId={TaskId}，UvId={UvId}", run.TaskId, run.UvId);
                                    await AwaitAsync(route.FallbackAsync(), captureToken).ConfigureAwait(false);
                                }
                            });
                        }), setupToken).ConfigureAwait(false);
                    }
                }, _business.BrowserSetupTimeoutSeconds, () => stage, session.Abort, token).ConfigureAwait(false);
                stages["browserSetupMs"] = Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds;
                _metrics.Stage("browserSetup", Stopwatch.GetElapsedTime(setupStarted));
                stages["browserStartMs"] = Stopwatch.GetElapsedTime(launchStarted).TotalMilliseconds;
                _metrics.Stage("browserStart", Stopwatch.GetElapsedTime(launchStarted));
            }, token, run.BrowserStartQueued).ConfigureAwait(false);
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
                    _metrics.UvExposed();
                }
                else
                {
                    var response = await AwaitAsync(page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }), token).ConfigureAwait(false);
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
            // Finish current captures; teardown below closes admission and drains any late arrivals.
            await cacheCallbacks.WaitCurrentAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex.Data["BrowserStartupPhase"] is string startupPhase)
            {
                stage = startupPhase;
                logger.Warning(ex, "浏览器启动阶段失败，TaskId={TaskId}，UvId={UvId}，Phase={Phase}，阶段毫秒={StageMilliseconds}，资源={ResourceState}",
                    run.TaskId, run.UvId, stage, StageSnapshot().ToString(Newtonsoft.Json.Formatting.None), ResourceState());
            }
            // Classify only main-document browser failures, never failed images/scripts or cleanup messages.
            var proxyCode = ProxyUnavailableException.BrowserCode(latestDocumentFailure)
                ?? (stage == "navigation" && ex is PlaywrightException ? ProxyUnavailableException.BrowserCode(ex.Message) : null);
            errors.Add(!token.IsCancellationRequested && proxyCode is not null
                ? new ProxyUnavailableException(proxyCode, ex) : ex);
        }
        finally
        {
            if (page is not null)
            {
                page.Response -= documentResponse; page.RequestFailed -= documentFailure;
                if (cacheResponse is not null) page.Response -= cacheResponse;
            }
            cacheCallbacks.Stop();
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
            if (context is not null)
            {
                try
                {
                    using (TrackCleanupPhase("cleanupContextClose"))
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
            using (TrackCleanupPhase("cleanupCacheDrain"))
            {
                await cacheCallbacks.DrainAsync().ConfigureAwait(false);
                foreach (var origin in origins.Values) origin.Dispose();
                origins.Clear();
            }
            stages["cleanupMs"] = Stopwatch.GetElapsedTime(closing).TotalMilliseconds;
            _metrics.Stage("cleanup", Stopwatch.GetElapsedTime(closing));
            logger.Information("浏览器清理汇总，TaskId={TaskId}，UvId={UvId}，ContextCloseTimedOut={ContextCloseTimedOut}，阶段毫秒={StageMilliseconds}，资源={ResourceState}",
                run.TaskId, run.UvId, gracefulContextCloseTimedOut, StageSnapshot().ToString(Newtonsoft.Json.Formatting.None), ResourceState());
            if (_business.ExportRunData) await File.WriteAllTextAsync(Path.Combine(run.Directory, "result.json"), new JObject
            {
                ["taskId"] = run.TaskId,
                ["uvId"] = run.UvId,
                ["workerId"] = run.WorkerId,
                ["driverId"] = driverId,
                ["browserProcessId"] = browserProcessId,
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
                ["startupCdpDetachDeferred"] = session?.StartupCdpDetachDeferred ?? false,
                ["browserPeakCommittedBytes"] = browserPeakBytes,
                ["gracefulContextCloseTimedOut"] = gracefulContextCloseTimedOut,
                ["viewport"] = viewport is null ? JValue.CreateNull() : JObject.FromObject(viewport),
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ["stages"] = StageSnapshot(),
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

    internal async Task ServeAsync(IRoute route, string partition, CancellationToken token,
        ConcurrentDictionary<IRequest, CacheAccess>? origins = null, ResourceCachePolicy? policy = null)
    {
        if (policy?.Bypass(route.Request.ResourceType, route.Request.Url) == true)
        { await AwaitAsync(route.FallbackAsync(), token).ConfigureAwait(false); return; }
        if (route.Request.Method == "GET" && (IsCacheableResource(route.Request.ResourceType) || IsVideoUrl(route.Request.Url)) && !IsGifUrl(route.Request.Url))
        {
            var headers = new Dictionary<string, string>(await AwaitAsync(route.Request.AllHeadersAsync(), token).ConfigureAwait(false));
            var key = SharedStaticResourceCache.RequestKey(route.Request.Url, headers, partition);
            CacheAccess? access = null;
            try { access = await cache.AcquireAsync(key, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.Debug(ex, "静态缓存协调失败，继续浏览器原始请求"); }
            if (access is not null)
            {
                try
                {
                    var hit = access.Hit;
                    _metrics.CacheLookup(hit is not null);
                    if (hit is not null)
                    {
                        var responseHeaders = cache.Headers(hit);
                        await AwaitAsync(route.FulfillAsync(new()
                        {
                            Status = hit.Metadata.StatusCode, ContentType = responseHeaders.GetValueOrDefault("content-type", hit.Metadata.ContentType),
                            Headers = responseHeaders, Path = hit.FilePath,
                            BodyBytes = hit.Tier == CacheTier.Memory ? hit.Body.ToArray() : null
                        }), token).ConfigureAwait(false);
                        return;
                    }
                    if (access.IsOwner && origins is not null && origins.TryAdd(route.Request, access))
                    {
                        // The response callback owns this ticket until its body has been published.
                        try { await AwaitAsync(route.FallbackAsync(), token).ConfigureAwait(false); }
                        catch { if (origins.TryRemove(route.Request, out var failed)) failed.Dispose(); throw; }
                        access = null; // Ownership was transferred to the response callback.
                    }
                    else await AwaitAsync(route.FallbackAsync(), token).ConfigureAwait(false);
                }
                finally { access?.Dispose(); }
                return;
            }
        }
        await AwaitAsync(route.FallbackAsync(), token).ConfigureAwait(false);
    }

    internal async Task RecordAsync(IResponse response, string partition, CancellationToken token,
        ConcurrentDictionary<IRequest, CacheAccess>? origins = null, long taskId = 0, long uvId = 0, ResourceCachePolicy? policy = null)
    {
        try
        {
            if (policy?.Bypass(response.Request.ResourceType, response.Url) == true) return;
            if (response.Request.Method != "GET" || !(IsCacheableResource(response.Request.ResourceType) || IsVideoUrl(response.Request.Url))
                || response.Status is not (200 or 206) || IsGifUrl(response.Request.Url)) return;
            Dictionary<string, string> headers, request;
            using (TrackCapture("缓存响应头读取", response.Url, taskId, uvId))
            {
                headers = new(await AwaitAsync(response.AllHeadersAsync(), token).ConfigureAwait(false));
                request = new(await AwaitAsync(response.Request.AllHeadersAsync(), token).ConfigureAwait(false));
            }
            var contentType = headers.GetValueOrDefault("content-type", "").Split(';', 2)[0].Trim();
            if (!IsCacheableContentType(contentType)) return;
            var key = SharedStaticResourceCache.RequestKey(response.Request.Url, request, partition);
            using (TrackCapture("缓存采集查重", response.Url, taskId, uvId))
            using (var existing = await cache.TryGetResourceAsync(key, token).ConfigureAwait(false))
                if (existing is not null) return; // Do not re-capture hits or extend their configured TTL.
            // Playwright returns the decoded body, including gzip/br and chunked responses.
            // There is no per-resource size or two-second capture limit; UV cancellation still applies.
            byte[] body;
            using (TrackCapture("缓存采集排队", response.Url, taskId, uvId))
                await cache.BeginCaptureAsync(token).ConfigureAwait(false);
            try
            {
                using (TrackCapture("缓存正文读取", response.Url, taskId, uvId))
                {
                    var bodyTask = response.BodyAsync();
                    try { body = await bodyTask.WaitAsync(token).ConfigureAwait(false); }
                    catch { _ = bodyTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
                }
            }
            finally { cache.EndCapture(); }
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
            using (TrackCapture("缓存写入", response.Url, taskId, uvId, body.LongLength))
            {
                var deduplicateBody = (policy?.DeduplicateJavaScript ?? true)
                    && SharedStaticResourceCache.ShouldDeduplicateJavaScript(response.Request.Method, response.Url);
                if (origins is not null && origins.TryGetValue(response.Request, out var owner) && owner.IsOwner)
                    await cache.StoreOwnedAsync(owner, key, contentType, headers, body, token, response.Status, deduplicateBody).ConfigureAwait(false);
                else await cache.StoreAsync(key, contentType, headers, body, token, response.Status, deduplicateBody).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { logger.Debug(ex, "静态缓存记录已放弃"); }
        finally
        {
            if (origins is not null && origins.TryRemove(response.Request, out var completed)) completed.Dispose();
        }
    }
    private sealed class StartupTiming(IDisposable active, Action<TimeSpan> finished) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            active.Dispose();
            finished(Stopwatch.GetElapsedTime(_started));
        }
    }

    private IDisposable TrackCapture(string phase, string url, long taskId, long uvId, long? bytes = null)
        => new CaptureTiming(_metrics, logger, new(phase, url, taskId, uvId, WorkerExecutionContext.WorkerId, 0), bytes);

    private sealed class CaptureTiming : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly ILogger _logger;
        private readonly CacheResourceStage _resource;
        private readonly long? _bytes;
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        internal CaptureTiming(BusinessMetrics metrics, ILogger logger, CacheResourceStage resource, long? bytes)
        { _logger = logger; _resource = resource; _bytes = bytes; _scope = metrics.Track(resource.Stage, resource); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _scope.Dispose();
            var elapsed = Stopwatch.GetElapsedTime(_started);
            if (elapsed.TotalSeconds >= 3)
                _logger.Warning("缓存采集慢阶段结束（含失败或取消），Phase={Phase}，ElapsedMs={ElapsedMs:F0}，TaskId={TaskId}，UvId={UvId}，Bytes={Bytes}，Url={Url}",
                    _resource.Stage, elapsed.TotalMilliseconds, _resource.TaskId, _resource.UvId, _bytes, _resource.Url);
        }
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
        ["workerId"] = run.WorkerId,
        ["deviceKey"] = run.Device.DeviceKey,
        ["pageViews"] = run.PageViews,
        ["jstext"] = run.Jstext
    }.ToString(Newtonsoft.Json.Formatting.None);
}
