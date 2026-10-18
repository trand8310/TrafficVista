using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Serilog;
using System.Diagnostics;

namespace TrafficVista
{
    internal static class Program
    {
        internal static bool RestartRequested { get; set; }
        internal static string? RestartResumeToken { get; set; }
        [STAThread]
        static int Main(string[] args)
        {
            ServiceProvider? services = null;
            GlobalExceptionHandling? exceptionHandling = null;
            var exitCode = 0;
            var interactive = !args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)
                && !arg.Equals("--auto-start", StringComparison.OrdinalIgnoreCase)
                && !arg.StartsWith("--resume-restart=", StringComparison.Ordinal));
            try
            {
                AppLogging.Configure(testMode: !interactive);
                exceptionHandling = new GlobalExceptionHandling(Log.Logger, showDialogs: interactive);
                Log.Information("应用启动，启动参数：{Args}", args);

                if (args.Contains("--test-startup-failure"))
                    throw new InvalidOperationException("TrafficVista exception test: startup");
                if (args.Contains("--exception-test"))
                {
                    ApplicationConfiguration.Initialize();
                    var result = ExceptionHandlingTest.Run(exceptionHandling);
                    WriteTestResult("exception-test.txt", result);
                    Log.Information("全局异常处理自检通过");
                }
                else
                {
                    // 自检使用专用目录，不读写正式用户会话。
                    services = AppServices.Build(Log.Logger, interactive ? null :
                        Path.Combine(AppContext.BaseDirectory, "demo-output", "test-users"));
                    // Browser self-tests retain their own legacy manager; business drivers are created lazily.
                    if (args.Contains("--desktop-test"))
                    {
                        WriteTestResult("desktop-test.txt", DesktopIntegration.DesktopIntegrationTest.RunAsync().GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--log-test"))
                    {
                        ApplicationConfiguration.Initialize();
                        WriteTestResult("log-test.txt", UiLogging.LiveLogIntegrationTest.Run());
                    }
                    else if (args.Contains("--form-test"))
                    {
                        ApplicationConfiguration.Initialize();
                        WriteTestResult("form-test.txt", Configuration.FormBindingTest.Run(Log.Logger));
                    }
                    else if (args.Contains("--settings-test"))
                    {
                        WriteTestResult("settings-test.txt", Configuration.SettingsIntegrationTest.Run());
                        Log.Information("配置接入自检通过");
                    }
                    else if (args.Contains("--scheduler-test"))
                    {
                        // 调度自检不初始化浏览器驱动，也不访问真实业务接口。
                        var result = Scheduling.Examples.SchedulingIntegrationTest.RunAsync().GetAwaiter().GetResult();
                        WriteTestResult("scheduler-test.txt", result);
                        Log.Information("任务调度接入自检通过");
                    }
                    else if (args.Contains("--json-test"))
                    {
                        WriteTestResult("json-test.txt", Serialization.JsonIntegrationTest.RunAsync().GetAwaiter().GetResult());
                        Log.Information("Newtonsoft.Json 与旧缓存兼容自检通过");
                    }
                    else if (args.Contains("--ipzan-test"))
                    {
                        WriteTestResult("ipzan-test.txt", Net.IpzanIntegrationTest.Run());
                    }
                    else if (args.Contains("--proxy-request-test"))
                    {
                        WriteTestResult("proxy-request-test.txt", Net.ProxyRequestIntegrationTest.RunAsync(Log.Logger).GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--net-test"))
                    {
                        // Net 模块端到端：本地模拟文本/JSON 供应商与 IpInfo 接口，不访问真实代理供应商。
                        WriteTestResult("net-test.txt", Net.NetIntegrationTest.RunAsync(Log.Logger).GetAwaiter().GetResult());
                        Log.Information("Net 模块代理领取与 IpInfo 查询自检通过");
                    }
                    else if (args.Contains("--http644-test"))
                    {
                        WriteTestResult("http644-test.txt", Business.Http644IntegrationTest.RunAsync(Log.Logger).GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--static-cache-test"))
                    {
                        WriteTestResult("static-cache-test.txt", Business.StaticCacheIntegrationTest.RunAsync(Log.Logger).GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--browser-close-test"))
                    {
                        WriteTestResult("browser-close-test.txt", Business.ReliabilityIntegrationTest.RunBrowserCloseAsync(Log.Logger).GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--throughput-benchmark"))
                    {
                        WriteTestResult("throughput-benchmark.txt", Business.ReliabilityIntegrationTest.RunThroughputAsync(Log.Logger).GetAwaiter().GetResult());
                    }
                    else if (args.Contains("--reliability-test") || args.Contains("--benchmark") || args.Contains("--business-test"))
                    {
                        // Fully offline, isolated fixtures, including device APIs and real local Chrome.
                        var benchmark = args.Contains("--benchmark");
                        var result = Business.ReliabilityIntegrationTest.RunAsync(Log.Logger, benchmark).GetAwaiter().GetResult();
                        WriteTestResult(benchmark ? "benchmark.txt" : "reliability-test.txt", result);
                        Log.Information("离线业务验证通过，Benchmark={Benchmark}", benchmark);
                    }
                    else if (args.Contains("--admission-test"))
                    {
                        WriteTestResult("admission-test.txt", Business.TaskAdmissionIntegrationTest.RunAsync(Log.Logger).GetAwaiter().GetResult());
                        Log.Information("固定工作者、UV间隔投递与浏览器启动限制验证通过");
                    }
                    else if (args.Contains("--window-test"))
                    {
                        WriteTestResult("window-test.txt", Business.BrowserWindowIntegrationTest.RunAsync().GetAwaiter().GetResult());
                        Log.Information("单 UV 浏览器窗口数量验证通过");
                    }
                    else if (args.Contains("--viewport-test"))
                    {
                        WriteTestResult("viewport-test.txt", Business.ViewportIntegrationTest.Run(Log.Logger));
                        Log.Information("AndroidViewportMatcher 屏幕参数接入验证通过");
                    }
                    else
                    {
                    ChromeInstanceManager? manager = null;
                    if (args.Any(arg => arg is "--smoke-test" or "--touch-test" or "--isolation-test"
                        or "--isolation-test-headed" or "--concurrency-test"))
                        manager = services.GetRequiredService<ChromeInstanceManager>();
                    if (args.Contains("--smoke-test"))
                        exitCode = RunSmokeTestAsync(manager!).GetAwaiter().GetResult();
                    else if (args.Contains("--touch-test"))
                        exitCode = RunIsolationTestAsync(manager!, 1).GetAwaiter().GetResult();
                    else if (args.Contains("--isolation-test"))
                        exitCode = RunIsolationTestAsync(manager!, 2).GetAwaiter().GetResult();
                    else if (args.Contains("--isolation-test-headed"))
                        exitCode = RunIsolationTestAsync(manager!, 2, headless: false).GetAwaiter().GetResult();
                    else if (args.Contains("--concurrency-test"))
                        exitCode = RunIsolationTestAsync(manager!, 6).GetAwaiter().GetResult();
                    else
                    {
                        ApplicationConfiguration.Initialize();
                        var form = services.GetRequiredService<MainForm>();
                        form.AutoStartRequested = args.Contains("--auto-start", StringComparer.OrdinalIgnoreCase);
                        form.RestartResumeToken = args.SingleOrDefault(arg => arg.StartsWith("--resume-restart=", StringComparison.Ordinal))?[17..];
                        Application.Run(form);
                    }
                    }
                }
            }
            catch (Exception ex)
            {
                exitCode = 1;
                Log.Fatal(ex, "应用主入口执行失败");
                Trace.WriteLine(ex);
                if (args.Contains("--exception-test"))
                    WriteTestResult("exception-test.txt", ex.ToString());
                if (interactive)
                {
                    try
                    {
                        MessageBox.Show($"应用执行失败：{ex.Message}\n\n日志目录：{AppLogging.LogDirectory}",
                            "TrafficVista", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch (Exception dialogException) { Trace.WriteLine(dialogException); }
                }
            }
            finally
            {
                if (services is not null)
                {
                    try
                    {
                        if (!Business.BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
                            Log.Warning("退出前Playwright收尾仍在进行，即将释放共享驱动，Deferred={Deferred}", Business.BrowserCloseCoordinator.Shared.Snapshot.Deferred);
                        // 先关闭全部实例，再释放限流器和共享驱动；库方法不依赖 UI 同步上下文。
                        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        Business.BrowserCloseCoordinator.Shared.DrainAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        exitCode = 1;
                        Log.Error(ex, "释放应用服务失败");
                    }
                }
                exceptionHandling?.Dispose();
                Log.Information("应用退出，ExitCode={ExitCode}", exitCode);
                Business.SystemPressureProbe.StopShared();
                try { Log.CloseAndFlush(); }
                catch (Exception flushException)
                {
                    exitCode = 1;
                    Trace.WriteLine(flushException);
                }
            }
            if (RestartRequested && exitCode == 0)
            {
                try
                {
                    var launch = DesktopIntegration.StartupShortcutService.CurrentLaunch();
                    if (RestartResumeToken is null) throw new InvalidOperationException("缺少定时重启队列恢复标识。");
                    using var next = Process.Start(new ProcessStartInfo(launch.Executable, launch.Arguments + " --resume-restart=" + RestartResumeToken)
                    { WorkingDirectory = launch.WorkingDirectory, UseShellExecute = true });
                    if (next is null) throw new InvalidOperationException("无法启动下一轮应用。");
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    MessageBox.Show("重启失败，暂存任务已保留，请重新打开程序并点击启动：" + ex.Message, "TrafficVista");
                }
            }
            return exitCode;
        }

        private static void WriteTestResult(string fileName, string text)
        {
            var outputDirectory = Path.Combine(AppContext.BaseDirectory, "demo-output");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, fileName), text);
        }

        private static async Task<int> RunSmokeTestAsync(ChromeInstanceManager manager)
        {
            try
            {
                await using var demo = manager.CreateInstance(1);
                await demo.RunAsync(headless: true);
                WriteTestResult("smoke-test.txt", $"PASS\n网址：{ChromeStartup.StartUrl}\n共享 IPlaywright，H5 触屏模式。");
                Log.Information("浏览器启动自检通过");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "浏览器启动自检失败");
                WriteTestResult("smoke-test.txt", ex.ToString());
                return 1;
            }
        }

        private static async Task<int> RunIsolationTestAsync(ChromeInstanceManager manager, int count, bool headless = true)
        {
            var fileName = count switch { 1 => "touch-test.txt", 2 => "isolation-test.txt", _ => "concurrency-test.txt" };
            try
            {
                var result = await ChromeIsolationTest.RunAsync(manager, count, headless);
                if (manager.InstanceCount != 0)
                    throw new InvalidOperationException("关闭后仍有实例留在管理器中。");
                WriteTestResult(fileName, result);
                Log.Information("{Count} 实例隔离/触屏/并发自检通过", count);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Count} 实例自检失败", count);
                WriteTestResult(fileName, ex.ToString());
                return 1;
            }
        }
    }
}
