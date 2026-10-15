using Serilog;
using TrafficVista.Scheduling;
using TrafficVista.Scheduling.Producers;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Contracts;

namespace TrafficVista.Configuration;

internal static class FormBindingTest
{
    public static string Run(ILogger logger)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "form-tests", DateTime.UtcNow.Ticks.ToString());
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(directory, "appsettings.json"));
        var settings = new TrafficVistaSettings(directory);
        var expected = settings.Value with
        {
            TaskName = "界面绑定测试", TaskFetchInterval = 250, UvExecutionInterval = 500,
            MaxConcurrency = 8, TaskWorkerCount = 50, PageLoadingDelay = 0.5m, PageLoadingTimeout = 30,
            Multiple = 2, IpTtl = 60, IsHiddenMode = true, IsProxyMode = true, IsCacheMode = true, VerboseLog = true,
            ResolveRealIp = false, FetchIpDetails = true
        };
        settings.Save(expected);
        using var client = new HttpClient(new EmptyTasksHandler());
        var scheduler = new TaskSchedulingService(logger, settings, new HttpTaskSource(client, logger),
            new DelegateTaskConsumer<TaskEnvelope>((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }));
        try
        {
            using var form = new MainForm(logger, scheduler, settings);
            if (form.ReadUiSettings() != expected) throw new InvalidOperationException("界面恢复与配置不一致。");
            ((TextBox)form.Controls.Find("textBox_TaskName", true).Single()).Text = "修改后的任务名称";
            ((NumericUpDown)form.Controls.Find("numericUpDown_MaxConcurrency", true).Single()).Value = 12;
            ((NumericUpDown)form.Controls.Find("numericUpDown_TaskWorkers", true).Single()).Value = 96;
            ((CheckBox)form.Controls.Find("checkBox_IsHiddenMode", true).Single()).Checked = false;
            ((CheckBox)form.Controls.Find("checkBox_IsCacheMode", true).Single()).Checked = false;
            ((CheckBox)form.Controls.Find("checkBox_RealIp", true).Single()).Checked = true;
            ((CheckBox)form.Controls.Find("checkBox_IpDetails", true).Single()).Checked = false;
            form.SaveUiSettings();
            var updated = expected with { TaskName = "修改后的任务名称", MaxConcurrency = 12, TaskWorkerCount = 96, IsHiddenMode = false, IsCacheMode = false,
                ResolveRealIp = true, FetchIpDetails = false };
            if (new TrafficVistaSettings(directory).Value != updated) throw new InvalidOperationException("界面事件或保存绑定失败。");
            if (settings.CreateSchedulerOptions(updated).WorkerCount != 96)
                throw new InvalidOperationException("界面Worker数没有作用于调度器。");
            if (form.Controls.Find("_firstUserId", true).Length != 0 || form.Controls.Find("_instanceCount", true).Length != 0
                || form.Controls.Find("btnStartStop", true).Single().Parent?.Name != "panel1")
                throw new InvalidOperationException("旧动态控件或按钮布局仍在干扰设计器。");
            // “IP获取重试次数”双向绑定：配置 → 界面恢复；界面 → 用户文件 Net 节 + 运行时 Net 单例即时生效。
            settings.Net.AcquireRetryCount = 7;
            form.ApplyUiSettings(settings.Value);
            var retryControl = (NumericUpDown)form.Controls.Find("numericUpDown_AcquireRetry", true).Single();
            if (retryControl.Value != 7m) throw new InvalidOperationException("IP获取重试次数界面恢复失败。");
            retryControl.Value = 3;
            form.SaveUiSettings();
            if (new TrafficVistaSettings(directory).Net.AcquireRetryCount != 3)
                throw new InvalidOperationException("IP获取重试次数保存或重启恢复失败。");
            if (settings.Net.AcquireRetryCount != 3)
                throw new InvalidOperationException("IP获取重试次数未即时作用于运行时 Net 配置单例。");
            // 创建可见控件句柄供离屏绘制；透明且不出现在任务栏中。
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            var liveLogs = (UiLogging.LiveLogView)form.Controls.Find("liveLogView", true).Single();
            var marker = "MainForm实时日志接线验证 " + Guid.NewGuid().ToString("N");
            logger.Information("{Marker}", marker);
            var logWait = System.Diagnostics.Stopwatch.StartNew();
            while (AppLogging.LiveLogs.ReadAfter(0, AppLogging.LiveLogs.Capacity, out _).All(entry => !entry.Text.Contains(marker))
                && logWait.Elapsed < TimeSpan.FromSeconds(3)) { Application.DoEvents(); Thread.Sleep(2); }
            liveLogs.FlushPending();
            if (liveLogs.RowCount == 0 || !form.Controls.Find("listView_Logs", true).Cast<ListView>().Single().VirtualMode)
                throw new InvalidOperationException("MainForm实时日志或虚拟列表未接入。");
            if (liveLogs.Top < form.Controls.Find("panel1", true).Single().Bottom || liveLogs.Height < 200)
                throw new InvalidOperationException("实时日志布局被配置面板遮挡。");
            var status = (Label)form.Controls.Find("label_Status", true).Single();
            var waitForDesktop = System.Diagnostics.Stopwatch.StartNew();
            while (!form.DesktopStatus.Resources.Available && waitForDesktop.Elapsed < TimeSpan.FromSeconds(5))
            { Application.DoEvents(); Thread.Sleep(5); }
            form.RefreshStatusBar();
            var strip = (StatusStrip)form.Controls.Find("statusStrip1", true).Single();
            var texts = strip.Items.Cast<ToolStripItem>().Select(item => item.Text ?? "").ToArray();
            if (!texts[0].Contains("待启动") || !texts[1].Contains("逻辑核") || !texts[1].Contains('%')
                || !texts[2].Contains("GB") || !texts[3].Contains("浏览器进程：0"))
                throw new InvalidOperationException("四个状态栏指标未接入或采样失败。");
            if (liveLogs.Bottom > strip.Top) throw new InvalidOperationException("日志区域遮挡状态栏。");
            status.Text = MainForm.BuildTaskStatusText(new() { State = SchedulerState.Running, Submitted = 12345,
                Queued = 128, Running = 4, Completed = 12001, Failed = 1, Cached = 128 },
                new Business.BusinessMetricsSnapshot(12345, 12, 0, 4, 4, 34.4, 4000, 8600, 0, 0, new()), 8,
                new(8, 4, 9999, 1, 4, 60, 4096, "优先投递已领 IP 的剩余 UV"), "测试页面超时。悬停查看详细异常。");
            var measuredStatus = TextRenderer.MeasureText(status.Text, status.Font,
                new Size(status.ClientSize.Width - status.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak);
            if (measuredStatus.Height > status.ClientSize.Height - status.Padding.Vertical)
                throw new InvalidOperationException("任务、UV 指标及错误摘要在状态区域被裁切。");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "demo-output", "mainform-preview.png"));
            form.Hide();
            if (scheduler.IsRunning) throw new InvalidOperationException("普通启动不应自动开始任务。");
            using (var automatic = new MainForm(logger, scheduler, settings) { AutoStartRequested = true, ShowInTaskbar = false, Opacity = 0 })
            {
                automatic.Show();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!scheduler.IsRunning && wait.Elapsed < TimeSpan.FromSeconds(5))
                { Application.DoEvents(); Thread.Sleep(10); }
                if (!scheduler.IsRunning) throw new InvalidOperationException("--auto-start 未触发按钮启动逻辑。");
                var hidden = (CheckBox)automatic.Controls.Find("checkBox_IsHiddenMode", true).Single();
                if (!hidden.Enabled) throw new InvalidOperationException("运行中隐藏模式控件被禁用。");
                hidden.Checked = true;
                automatic.SaveUiSettings();
                if (!scheduler.IsRunning || !new TrafficVistaSettings(directory).Value.IsHiddenMode)
                    throw new InvalidOperationException("运行中隐藏模式切换未保存，或中断调度。");
                hidden.Checked = false;
                automatic.SaveUiSettings();
                if (!scheduler.IsRunning || new TrafficVistaSettings(directory).Value.IsHiddenMode)
                    throw new InvalidOperationException("运行中隐藏模式无法切回可见。");
                scheduler.StopAsync().GetAwaiter().GetResult();
                automatic.Hide();
                automatic.Show();
                Application.DoEvents();
                if (scheduler.IsRunning) throw new InvalidOperationException("窗体再次显示不应重复自动启动。");
            }
            return "PASS\n设计器控件恢复、文本/数值/开关事件保存、Worker/IP重试双向绑定、实时日志、四项状态栏后台采样、摘要及布局渲染通过；自动启动按钮接线及仅触发一次通过。任务HTTP全部使用空列表桩，无浏览器启动、无真实接口请求、未创建真实开机启动快捷方式。";
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AppLogging.SetVerbose(false);
        }
    }
    private sealed class EmptyTasksHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"success\":true,\"data\":[]}") });
        }
    }
}
