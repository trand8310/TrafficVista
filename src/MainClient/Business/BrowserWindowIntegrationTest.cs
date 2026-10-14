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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
            var context = await browser.NewContextAsync();
            await context.NewPageAsync();
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
        if (Directory.EnumerateDirectories(directory, "chrome-profile", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("窗口验证的临时 Chrome profile 尚未清理。");
        return "PASS\n" + report + "启动无默认窗口，一个 UV 只有一个业务页面；可见/隐藏模式及自有进程/profile清理通过。\n输出目录：" + directory;
    }
}
