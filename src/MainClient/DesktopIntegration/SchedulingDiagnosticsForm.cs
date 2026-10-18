using System.Text;
using TrafficVista.Scheduling;
using TrafficVista.Scheduling.Producers;

namespace TrafficVista.DesktopIntegration;

internal sealed class SchedulingDiagnosticsForm : Form
{
    private readonly TaskSchedulingService _scheduler;
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    internal SchedulingDiagnosticsForm(TaskSchedulingService scheduler)
    {
        _scheduler = scheduler;
        Text = "本轮累计统计 / 调度诊断（每秒刷新）";
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new(850, 650);
        _text.Font = new Font("Consolas", 11);
        Controls.Add(_text);
        _timer.Tick += (_, _) => RefreshSnapshot();
        Shown += (_, _) => { RefreshSnapshot(); _timer.Start(); };
        FormClosed += (_, _) => { _timer.Dispose(); };
    }
    private void RefreshSnapshot()
    {
        var s = _scheduler.Snapshot;
        var p = _scheduler.Performance;
        var a = _scheduler.Dispatch;
        var text = new StringBuilder();
        text.AppendLine("应用目录：" + AppContext.BaseDirectory);
        text.AppendLine("统计范围：本轮运行；点击启动后重置。所有失败/取消也计入总完成量。");
        text.AppendLine($"调度状态：{s.State}");
        if (_scheduler.EffectiveBrowserStarts is { } starts)
            text.AppendLine($"资源保护：当前允许同时启动{starts}个Chrome（CPU繁忙时收敛、内存余量不足时暂缓；已有UV继续运行）");
        text.AppendLine($"运行指标：运行时间{p.RunElapsedText}  提交数量（UV开始运行）{p.DispatchedUvs}  执行数量（浏览器开始启动）{p.BrowserStartAttempts}  曝光数量（首次页面打开成功）{p.ExposedUvs}");
        text.AppendLine("运行指标口径：执行含启动失败；曝光每UV最多一次，通过首次页面打开检查后计数，后续失败/取消不回退，PV刷新不重复累计。");
        text.AppendLine("任务成功口径：至少一个UV最终成功即任务成功；仍等待所有已投递UV结束并清理，主动停止按取消统计。");
        if (_scheduler.PlaywrightDrivers is { } drivers)
            text.AppendLine($"Playwright驱动：{(drivers.Configured == 0 ? "跟随任务并发" : "指定数量" + drivers.Configured)}  本轮上限{drivers.Effective}  已创建{drivers.Created}  创建中{drivers.Initializing}  使用中UV{drivers.ActiveUvs}");
        var closing = _scheduler.BrowserClosing;
        text.AppendLine($"Playwright关闭收尾：请求在途{closing.InFlight}/{closing.Capacity}  后台跟踪{closing.Deferred}  最久{closing.OldestDeferredSeconds:F1}秒（独立于UV在途）");
        text.AppendLine($"任务：累计提交{s.Submitted}  已领取{s.Claimed}  完成{s.Completed + s.Canceled}  成功{s.Succeeded}  失败{s.Failed}  取消{s.Canceled}");
        text.AppendLine($"任务在途：排队{s.Queued}  准备中{s.Preparing}  执行{s.Running}  Worker{s.Workers}  待恢复{s.Cached}");
        if (_scheduler.TaskIntake is { } intake)
            text.AppendLine($"任务领取：{(!intake.Enabled ? "背压已关闭（开放领取）" : intake.Paused ? "暂停（等待启动需求回落）" : "开放（背压已开启）")}  等待领取工作者{intake.WaitingWorkers}  启动需求合计{intake.Pressure}（启动排队{intake.WaitingBrowserStarts}＋已领取待投递UV{intake.PendingUvs}＋投递后准备中UV{intake.PreparingUvs}）  配置阈值：达到{intake.PauseThreshold}暂停/降至{intake.ResumeThreshold}恢复（仅开启时生效）"
                + (intake.Paused ? $"  已暂停{intake.PausedSeconds:F1}秒" : ""));
        text.Append(BuildFetchDiagnostics(s, _scheduler.TaskFetch));
        text.AppendLine($"任务来源错误{s.SourceErrors}  执行超时任务{s.TimedOut}  放弃{s.Abandoned}");
        text.AppendLine($"UV：计划{p.PlannedUvs}  已投递{p.DispatchedUvs}  完成{p.FinishedUvs}  成功{p.SucceededUvs}  失败{p.FailedUvs}  取消{p.CanceledUvs}");
        text.AppendLine($"UV：在途{_scheduler.ActiveUvSessions}  结束任务未投递{p.UndispatchedUvs}  浏览器会话{p.ActiveBrowsers}  峰值{p.PeakBrowsers}");
        text.AppendLine($"UV投递：最近60秒{p.DispatchesPerMinute:F0}个（按秒统计，启动不足60秒显示实际数量），累计{p.DispatchedUvs}个");
        text.AppendLine($"本轮平均吞吐：总完成{p.AttemptsPerMinute:F1}/分  成功{p.UvsPerMinute:F1}/分  成功率{(p.FinishedUvs == 0 ? 0 : p.SucceededUvs * 100.0 / p.FinishedUvs):F1}%  P95{p.UvP95Ms / 1000:F1}秒");
        if (a is not null)
        {
            text.AppendLine($"按任务UV间隔投递；待投递UV{a.PendingUvs}  执行中任务{a.ActiveTasks}");
            text.AppendLine($"单UV历史峰值提交内存{p.PeakSessionBrowserCommittedMiB:F0}MiB（仅参考）");
        }
        if (_scheduler.ResourceCache is { } cache)
        {
            text.AppendLine($"资源缓存（应用生命周期累计查询）：内存命中{cache.MemoryHits}  磁盘命中{cache.DiskHits}  未命中{cache.Misses}");
            text.AppendLine($"缓存协调（当前快照）：正在回源{cache.ActiveOriginFills}  合并等待{cache.WaitingRequests}");
            text.AppendLine($"缓存累计：回源发起{cache.OriginFills}  合并等待次数{cache.CoalescedRequests}  存储错误{cache.Errors}");
            text.AppendLine($"缓存当前：内存正文{cache.MemoryBytes / 1048576.0:F1}MiB/{cache.MemoryEntries}项  磁盘{cache.DiskBytes / 1048576.0:F1}MiB/{cache.DiskEntries}项  写入队列{cache.QueuedWrites}  待完成写入{cache.PendingWrites}");
        }
        text.AppendLine("\r\n当前阶段（数量 / 最久等待或执行秒数）：");
        foreach (var item in p.ActiveStages.OrderByDescending(item => item.Value.OldestSeconds))
            text.AppendLine($"  {item.Key}: {item.Value.Count} / {item.Value.OldestSeconds:F1}s");
        if (p.ActiveStages.Count == 0) text.AppendLine("  当前无在途阶段；结合任务接口、任务队列与调度状态判断供给。");
        text.AppendLine("\r\n缓存采集：资源级计数，与UV阶段数量不可相加；下列为当前最久的5项：");
        foreach (var item in p.ActiveCacheResources)
            text.AppendLine($"  {item.Stage}: {item.Seconds:F1}s  Worker{item.WorkerId}  Task{item.TaskId}  UV{item.UvId}  {item.Url}");
        if (p.ActiveCacheResources.Length == 0) text.AppendLine("  当前无缓存采集等待或执行。");
        text.AppendLine("\r\n累计阶段失败：");
        foreach (var item in p.StageFailures.OrderByDescending(item => item.Value))
            text.AppendLine($"  {item.Key}: {item.Value}");
        text.AppendLine("\r\n已完成阶段平均耗时（不是当前等待时间）：");
        text.AppendLine("  启动含Playwright/CDP及主进程绑定、子进程扫描/绑定；诊断CDP断开与页面准备并行；清理含Context/Browser关闭、终止、等待主/子进程退出。各阶段含失败/取消样本，子阶段与总耗时不可重复相加。");
        foreach (var item in p.AverageStageMs.OrderByDescending(item => item.Value))
            text.AppendLine($"  {item.Key}: {item.Value / 1000:F2}s");
        var value = text.ToString();
        if (_text.Text != value) _text.Text = value;
    }

    internal static string BuildFetchDiagnostics(SchedulerSnapshot scheduler, TaskFetchSnapshot fetch)
    {
        static string Age(double? seconds) => seconds is { } value ? $"{value:F1}秒" : "尚无记录";
        var text = new StringBuilder();
        text.AppendLine($"任务接口：请求{fetch.Requests}  有效响应{fetch.Responses}  有任务{fetch.NonEmptyResponses}  空响应{fetch.EmptyResponses}  失败{fetch.FailedRequests}  中断{fetch.InterruptedRequests}  拉取超时{scheduler.SourceTimeouts}");
        text.AppendLine($"任务接口累计返回原始任务{fetch.ReceivedTasks}项（倍率展开前）；最近/当前请求状态：{fetch.Stage}");
        text.AppendLine($"最近完成请求：HTTP{fetch.LastHttpStatus?.ToString() ?? "--"}  返回{fetch.LastTaskCount?.ToString() ?? "--"}项  耗时{(fetch.LastDurationMs is { } ms ? $"{ms / 1000:F2}秒" : "--")}  本轮最大耗时{fetch.MaxDurationMs / 1000:F2}秒");
        text.AppendLine($"距最近请求开始：{Age(fetch.SecondsSinceLastRequest)}；距最近有效响应：{Age(fetch.SecondsSinceLastResponse)}");
        text.AppendLine($"任务获取状态：{scheduler.Producer.Stage}，已持续{scheduler.Producer.StageSeconds:F1}秒");
        if (fetch.InFlight) text.AppendLine($"当前HTTP请求：{fetch.Stage}，该阶段已持续{fetch.StageSeconds:F1}秒");
        text.AppendLine(scheduler.Producer.NextFetchInSeconds is { } next
            ? $"下次拉取倒计时：{next:F1}秒（等待结束后仍需满足队列条件）"
            : "下次拉取倒计时：未安排，取决于当前请求、入队或调度状态");
        if (fetch.LastError is { } error)
            text.AppendLine("最近请求错误：" + error.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(error.Length, 240)]);
        return text.ToString();
    }
}

