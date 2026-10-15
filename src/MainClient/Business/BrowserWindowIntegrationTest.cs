using Microsoft.Playwright;

namespace TrafficVista.Business;

/// <summary>Visible-mode window regression in an isolated owned profile; no task, proxy or device API.</summary>
internal static class BrowserWindowIntegrationTest
{
    internal static async Task<string> RunAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "window-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("TRAFFICVISTA_TEST_CHROME")
            ?? Path.GetFullPath(new BusinessOptions().ChromeExecutablePath, AppContext.BaseDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var report = new System.Text.StringBuilder();
        foreach (var hidden in new[] { false, true })
        {
            var runDirectory = Path.Combine(directory, hidden ? "headless" : "visible");
            await using var session = await OwnedChromeSession.StartAsync(playwright, new()
            {
                ExecutablePath = executable, Headless = hidden,
                Args = ["--no-first-run", "--no-default-browser-check", "--disable-background-networking",
                    "--disable-component-update", "--disable-sync", "--window-position=-32000,-32000"]
            }, runDirectory, timeout.Token);
            var browser = session.Browser!;
            var initialPages = browser.Contexts.Sum(context => context.Pages.Count);
            var context = await session.Browser.NewContextAsync();
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            var cdp = await browser.NewBrowserCDPSessionAsync();
            var targets = (await cdp.SendAsync("Target.getTargets"))!.Value.GetProperty("targetInfos");
            var windows = new HashSet<int>();
            var pageTargets = 0;
            foreach (var target in targets.EnumerateArray())
            {
                if (target.GetProperty("type").GetString() != "page") continue;
                pageTargets++;
                if (!hidden)
                {
                    var window = (await cdp.SendAsync("Browser.getWindowForTarget", new Dictionary<string, object>
                        { ["targetId"] = target.GetProperty("targetId").GetString()! }))!.Value;
                    windows.Add(window.GetProperty("windowId").GetInt32());
                }
            }
            report.AppendLine($"模式={(hidden ? "headless" : "可见")}，启动默认页面={initialPages}，业务页面目标={pageTargets}，可见窗口={windows.Count}");
            await File.WriteAllTextAsync(Path.Combine(directory, "window-count.txt"), report.ToString());
            if (initialPages != 0 || pageTargets != 1 || (!hidden && windows.Count != 1))
                throw new InvalidOperationException($"一次 UV 应只有一个业务页面/窗口：默认页面={initialPages}，页面目标={pageTargets}，可见窗口={windows.Count}");
            await context.CloseAsync();
        }
        await VerifyIsolationAsync(playwright, executable, directory, timeout.Token);
        foreach (var cancelDuringLaunch in new[] { false, true })
        {
            var runDirectory = Path.Combine(directory, cancelDuringLaunch ? "launch-canceled" : "launch-failed");
            using var canceled = new CancellationTokenSource();
            var starting = OwnedChromeSession.StartAsync(playwright, new()
            {
                ExecutablePath = cancelDuringLaunch ? executable : Path.Combine(directory, "missing-chrome.exe"),
                Headless = true
            }, runDirectory, canceled.Token);
            if (cancelDuringLaunch) canceled.Cancel();
            try
            {
                await using var unexpected = await starting;
                throw new InvalidOperationException("启动失败/取消场景不应成功。");
            }
            catch (OperationCanceledException) when (cancelDuringLaunch) { }
            catch (PlaywrightException) when (!cancelDuringLaunch) { }
        }
        if (ChromeProcessJob.OwnedProcessCount != 0)
            throw new InvalidOperationException("启动/隔离验证后仍有所属浏览器进程。");
        if (Directory.EnumerateDirectories(directory, "chrome-profile", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("窗口验证的临时 Chrome profile 尚未清理。");
        return "PASS\n" + report + "直接启动无默认页面，一个 UV 只有一个业务页面；可见/隐藏模式、双UV Cookie/localStorage隔离、关闭互不影响、启动失败/取消及自有进程清理通过；临时profile由Playwright管理。\n输出目录：" + directory;
    }

    private static async Task VerifyIsolationAsync(IPlaywright playwright, string executable, string directory, CancellationToken token)
    {
        var firstDirectory = Path.Combine(directory, "isolation-a");
        await using var first = await OwnedChromeSession.StartAsync(playwright,
            new() { ExecutablePath = executable, Headless = true }, firstDirectory, token);
        await using var second = await OwnedChromeSession.StartAsync(playwright,
            new() { ExecutablePath = executable, Headless = true }, Path.Combine(directory, "isolation-b"), token);
        var firstContext = await first.Browser.NewContextAsync();
        var secondContext = await second.Browser.NewContextAsync();
        const string url = "https://uv-isolation.invalid/";
        foreach (var context in new[] { firstContext, secondContext })
        {
            await context.RouteAsync("**/*", route => route.FulfillAsync(new()
                { ContentType = "text/html", Body = "<html><body>offline isolation</body></html>" }));
            var page = await context.NewPageAsync();
            await page.GotoAsync(url);
        }
        await firstContext.AddCookiesAsync([new() { Name = "uv", Value = "first", Url = url }]);
        await firstContext.Pages[0].EvaluateAsync("localStorage.setItem('uv', 'first')");
        if ((await secondContext.CookiesAsync()).Count != 0
            || await secondContext.Pages[0].EvaluateAsync<string?>("localStorage.getItem('uv')") is not null)
            throw new InvalidOperationException("不同 UV 不得共享 Cookie/localStorage。");
        await first.Browser.CloseAsync();
        if (!second.Browser!.IsConnected || await secondContext.Pages[0].InnerTextAsync("body") != "offline isolation")
            throw new InvalidOperationException("关闭一个 UV 不得影响另一个 UV。");
    }
}
