using Microsoft.Playwright;
using TrafficVista.Business;
using TrafficVista.Scheduling;

namespace TrafficVista.DesktopIntegration;

internal static class DesktopIntegrationTest
{
    internal static async Task<string> RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "demo-output", "desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var startup = new StartupShortcutService(Path.Combine(root, "startup with spaces"));
        Check(!await startup.IsEnabledAsync(), "空测试目录不应启用开机启动");
        await startup.EnsureEnabledAsync();
        await startup.EnsureEnabledAsync();
        Check(await startup.IsEnabledAsync() && Directory.GetFiles(Path.GetDirectoryName(startup.ShortcutPath)!, "*.lnk").Length == 1,
            "快捷方式生成及重复点击幂等");
        var shortcut = await StartupShortcutService.ReadAsync(startup.ShortcutPath);
        Check(shortcut.Arguments.EndsWith("--auto-start") && !shortcut.Arguments.Contains("--desktop-test")
            && Path.GetFullPath(shortcut.Executable) == Path.GetFullPath(startup.Launch.Executable)
            && shortcut.WorkingDirectory.TrimEnd('\\') == AppContext.BaseDirectory.TrimEnd('\\'), "快捷方式目标/工作目录及自动启动参数");
        var old = new StartupShortcutService(Path.Combine(root, "old"), startup.Launch with { Arguments = "" });
        await old.EnsureEnabledAsync();
        var oldPath = Path.Combine(Path.GetDirectoryName(startup.ShortcutPath)!, "old-app.lnk");
        File.Move(old.ShortcutPath, oldPath);
        var unrelated = new StartupShortcutService(Path.Combine(root, "unrelated"),
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), "", root));
        await unrelated.EnsureEnabledAsync();
        var unrelatedPath = Path.Combine(Path.GetDirectoryName(startup.ShortcutPath)!, "unrelated.lnk");
        File.Move(unrelated.ShortcutPath, unrelatedPath);
        var cleaned = await startup.EnsureEnabledAsync();
        Check(cleaned.RemovedEntries == 1 && cleaned.Warnings.Count == 0 && !File.Exists(oldPath) && File.Exists(unrelatedPath),
            "清理同目标旧快捷方式并保留其它应用");
        Check(StartupEntryCleanup.MatchesCommand("\"" + startup.Launch.Executable + "\" " + startup.Launch.Arguments, startup.Launch),
            "启动命令识别");
        Check(!StartupEntryCleanup.MatchesApplication(unrelated.Launch.Executable, "", startup.Launch), "其它应用目标不能被清理");
        var dll = Path.Combine(startup.Launch.WorkingDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll");
        Check(StartupEntryCleanup.MatchesApplication("dotnet", "\"" + dll + "\" --auto-start", startup.Launch)
            && !StartupEntryCleanup.MatchesApplication("dotnet", "\"" + Path.Combine(root, "other.dll") + "\"", startup.Launch),
            "dotnet必须精确匹配本应用dll");
        var changed = new StartupShortcutService(Path.GetDirectoryName(startup.ShortcutPath), startup.Launch with { Arguments = "--fixture-only" });
        await changed.EnsureEnabledAsync();
        Check(!await startup.IsEnabledAsync(), "旧快捷方式参数不匹配应提示未启用");
        await startup.EnsureEnabledAsync();
        Check(await startup.IsEnabledAsync(), "更新已有快捷方式");
        var failed = new StartupShortcutService(Path.Combine(root, "failed"), startup.Launch with { Executable = Path.Combine(root, "missing.exe") });
        try { await failed.EnsureEnabledAsync(); throw new InvalidOperationException("缺失启动文件未报错"); }
        catch (FileNotFoundException) { }
        Check(!File.Exists(failed.ShortcutPath), "失败操作不应留下快捷方式");

        Check(MainForm.ApplicationState(SchedulerState.Stopped, false, false, false, false, false, false) == "应用：待启动", "初始待启动");
        Check(MainForm.ApplicationState(SchedulerState.Stopped, true, false, false, false, false) == "应用：启动中", "启动中");
        Check(MainForm.ApplicationState(SchedulerState.Running, false, false, true, false, false) == "应用：运行中", "运行中");
        Check(MainForm.ApplicationState(SchedulerState.Running, false, false, true, true, false).Contains("等待资源"), "资源等待状态");
        Check(MainForm.ApplicationState(SchedulerState.Running, true, false, true, false, false) == "应用：停止中", "停止中");
        Check(MainForm.ApplicationState(SchedulerState.Stopped, false, false, false, false, false) == "应用：已停止", "已停止");
        Check(MainForm.ApplicationState(SchedulerState.Faulted, false, false, false, false, false) == "应用：故障", "故障");

        using var counterProbe = new SystemPressureProbe(forceCounter: true);
        Check(!counterProbe.Read().Available, "PDH采样需要CPU差分基线");
        await Task.Delay(1200);
        var counterSample = counterProbe.Read();
        Check(counterSample.Available && counterSample.CpuPercent is >= 0 and <= 100 && counterSample.TotalPhysicalBytes > 0,
            "跨处理器组CPU计数器通道：" + counterSample.UnavailableReason);
        using var monitor = new DesktopStatusMonitor();
        await UntilAsync(() => monitor.Snapshot.Resources.Available && monitor.Snapshot.LogicalCores > 0);
        var resources = monitor.Snapshot;
        Check(resources.Resources.AvailablePhysicalBytes <= resources.Resources.TotalPhysicalBytes, "系统内存采样");
        Check(ChromeProcessJob.OwnedProcessCount == 0, "启动前所属浏览器Job为零");
        using var playwright = await Playwright.CreateAsync();
        var executable = Path.GetFullPath(new BusinessOptions().ChromeExecutablePath, AppContext.BaseDirectory);
        var session = await OwnedChromeSession.StartAsync(playwright, new() { ExecutablePath = executable, Headless = true,
            Args = ["--no-first-run", "--no-default-browser-check", "--disable-background-networking"] }, Path.Combine(root, "chrome"), CancellationToken.None);
        int observed;
        try
        {
            var context = await session.Browser!.NewContextAsync();
            await context.NewPageAsync();
            await UntilAsync(() => monitor.Snapshot.BrowserProcesses > 0);
            observed = monitor.Snapshot.BrowserProcesses!.Value;
            Check(ChromeProcessJob.OwnedProcessCount >= 1 && observed >= 1, "所属Chrome主进程/子进程计数");
            await context.CloseAsync();
        }
        finally { await session.DisposeAsync(); }
        await UntilAsync(() => monitor.Snapshot.BrowserProcesses == 0);
        Check(ChromeProcessJob.OwnedProcessCount == 0, "浏览器清理后进程计数归零");
        return $"PASS\n应用状态切换、后台共享CPU/内存采样、逻辑核心数={resources.LogicalCores}、跨组PDH计数器通道、真实Chrome所属进程计数（观测{observed}）及清理归零通过。\n快捷方式目标/工作目录、--auto-start参数、重复创建、旧值更新、同目标旧快捷方式清理、其它应用保留、dotnet目标识别和失败不留文件通过；所有快捷方式只创建在隔离测试目录，未修改真实Windows启动目录/注册表/计划任务，未打开资源管理器。";
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("桌面状态采样未达到预期。"); await Task.Delay(50); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
