using Newtonsoft.Json;
using Microsoft.Playwright;
using Serilog;

namespace TrafficVista.LocalTesting;

/// <summary>只执行本机页面；每个 UV 拥有独立进程、Context、设备和状态文件。</summary>
public sealed class LocalUvBrowserRunner(IPlaywright playwright, BrowserLaunchLimiter launches, ILogger logger) : IUvBrowserRunner
{
    public async Task<bool> RunAsync(UvBrowserRun run, CancellationToken token)
    {
        LocalEndpointPolicy.RequireLoopback(run.Target.AbsoluteUri, "测试页面");
        if (run.Proxy is not null) LocalEndpointPolicy.RequireLoopback(run.Proxy.Server, "本地代理");
        IBrowser? browser = null;
        IBrowserContext? context = null;
        var errors = new List<Exception>();
        var successful = false;
        try
        {
            await launches.ExecuteAsync(async () =>
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "File", "chrome-win", "145.0.7632.110", "chrome.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Chrome 145。", executable);
                browser = await playwright.Chromium.LaunchAsync(new()
                {
                    ExecutablePath = executable,
                    Headless = run.Options.IsHiddenMode,
                    Timeout = 30000,
                    // 本机 H5 和 localhost 资源直连；本机测试代理只用于其他请求，且外部请求仍会被 Route 拦截。
                    Proxy = run.Proxy is null ? null : new Proxy { Server = run.Proxy.Server, Bypass = "localhost,127.0.0.1,::1" },
                    // 本地测试只用标准 Chrome 参数，不增加硬件/身份指纹注入。
                    Args = ["--no-first-run", "--no-default-browser-check", "--disable-background-networking",
                        "--disable-component-update", "--disable-sync", "--enable-unsafe-swiftshader"]
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                context = await browser.NewContextAsync(new()
                {
                    HasTouch = true, IsMobile = true, UserAgent = run.Device.UserAgent,
                    ViewportSize = new() { Width = run.Device.Width, Height = run.Device.Height },
                    ScreenSize = new() { Width = run.Device.Width, Height = run.Device.Height },
                    DeviceScaleFactor = run.Device.Scale, Locale = "zh-CN", IgnoreHTTPSErrors = true,
                    ServiceWorkers = ServiceWorkerPolicy.Block
                }).ConfigureAwait(false);
                await context.RouteAsync("**/*", route => LocalEndpointPolicy.IsLoopback(route.Request.Url)
                    ? route.ContinueAsync() : route.AbortAsync()).ConfigureAwait(false);
                await context.RouteWebSocketAsync("**/*", socket =>
                {
                    // 不连接服务器就不会发出外部 WebSocket 请求。
                    if (LocalEndpointPolicy.IsLoopback(socket.Url, allowWebSocket: true)) socket.ConnectToServer();
                }).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var page = await context!.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultNavigationTimeout(run.Options.PageLoadingTimeout * 1000);
            await page.AddInitScriptAsync("window.__trafficVistaLocalTest = " + JsonConvert.SerializeObject(new
                { taskId = run.TaskId, uvId = run.UvId, deviceId = run.Device.DeviceId })).ConfigureAwait(false);
            var response = await page.GotoAsync(run.Target.AbsoluteUri,
                new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            if (response is null || !response.Ok) throw new InvalidOperationException($"本地页面加载失败：{response?.Status}");
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds((double)run.Options.PageLoadingDelay) + run.Dwell, token).ConfigureAwait(false);
            // 本地 H5 的断言/操作写在这里。不执行接口内的任意 jstext 或广告点击。
            successful = await page.Locator("body").IsVisibleAsync().ConfigureAwait(false);
            await context.StorageStateAsync(new()
                { Path = Path.Combine(run.Directory, "storage-state.json"), IndexedDB = true }).ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            // 关闭失败也继续关闭浏览器，且在释放 UV 并发许可前完成清理。
            if (context is not null)
            {
                try { await context.CloseAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
            }
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
            }
            await File.WriteAllTextAsync(Path.Combine(run.Directory, "result.json"), JsonConvert.SerializeObject(new
            {
                run.TaskId, run.UvId, run.Device.DeviceId, ProxyServer = run.Proxy?.Server,
                Success = successful && errors.Count == 0, Errors = errors.Select(error => error.Message).ToArray()
            }), CancellationToken.None).ConfigureAwait(false);
        }
        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
        if (errors.Count > 0) throw new AggregateException("本地 UV 执行或清理失败。", errors);
        logger.Information("本地 UV 完成，TaskId={TaskId}，UvId={UvId}，DeviceId={DeviceId}", run.TaskId, run.UvId, run.Device.DeviceId);
        return successful;
    }
}
