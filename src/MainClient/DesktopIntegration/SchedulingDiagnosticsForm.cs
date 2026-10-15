using System.Text;
using TrafficVista.Scheduling;

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
        var a = _scheduler.Admission;
        var text = new StringBuilder();
        text.AppendLine("应用目录：" + AppContext.BaseDirectory);
        text.AppendLine("统计范围：本轮运行；点击启动后重置。所有失败/取消也计入总完成量。");
        text.AppendLine($"任务：累计提交{s.Submitted}  已领取{s.Claimed}  完成{s.Completed + s.Canceled}  成功{s.Succeeded}  失败{s.Failed}  取消{s.Canceled}");
        text.AppendLine($"任务在途：排队{s.Queued}  准入等待{s.Preparing}  执行{s.Running}  Worker{s.Workers}  待恢复{s.Cached}");
        text.AppendLine($"任务接口：错误{s.SourceErrors}  超时任务{s.TimedOut}  放弃{s.Abandoned}");
        text.AppendLine($"UV：计划{p.PlannedUvs}  已投递{p.DispatchedUvs}  完成{p.FinishedUvs}  成功{p.SucceededUvs}  失败{p.FailedUvs}  取消{p.CanceledUvs}");
        text.AppendLine($"UV：在途{_scheduler.ActiveUvSessions}  结束任务未投递{p.UndispatchedUvs}  浏览器会话{p.ActiveBrowsers}  峰值{p.PeakBrowsers}");
        text.AppendLine($"UV投递：最近60秒{p.DispatchesPerMinute:F0}个（按秒统计，启动不足60秒显示实际数量），累计{p.DispatchedUvs}个");
        text.AppendLine($"本轮平均吞吐：总完成{p.AttemptsPerMinute:F1}/分  成功{p.UvsPerMinute:F1}/分  成功率{(p.FinishedUvs == 0 ? 0 : p.SucceededUvs * 100.0 / p.FinishedUvs):F1}%  P95{p.UvP95Ms / 1000:F1}秒");
        if (a is not null)
        {
            text.AppendLine($"并发池：{a.Reason}  硬上限{a.HardLimit}  使用中{a.ReservedSlots}  空闲UV名额{a.MemoryCapacity}");
            text.AppendLine($"已有IP待投递UV{a.PendingUvs}  投递中任务{a.DispatchingTasks}  等待任务{a.WaitingTasks}");
            text.AppendLine($"单UV历史峰值提交内存{p.PeakSessionBrowserCommittedMiB:F0}MiB（仅参考，不缩减并发）");
            text.AppendLine($"可用物理{a.AvailableMemoryMiB:F0}MiB  提交余量{a.CommitAvailableMiB:F0}MiB  CPU趋势{a.CpuPercent:F1}%");
        }
        text.AppendLine("\r\n当前阶段（数量 / 最久等待或执行秒数）：");
        foreach (var item in p.ActiveStages.OrderByDescending(item => item.Value.OldestSeconds))
            text.AppendLine($"  {item.Key}: {item.Value.Count} / {item.Value.OldestSeconds:F1}s");
        if (p.ActiveStages.Count == 0) text.AppendLine("  当前无在途阶段；结合任务接口、任务队列与调度状态判断供给。");
        text.AppendLine("\r\n累计阶段失败：");
        foreach (var item in p.StageFailures.OrderByDescending(item => item.Value))
            text.AppendLine($"  {item.Key}: {item.Value}");
        text.AppendLine("\r\n已完成阶段平均耗时（不是当前等待时间）：");
        foreach (var item in p.AverageStageMs.OrderByDescending(item => item.Value))
            text.AppendLine($"  {item.Key}: {item.Value / 1000:F2}s");
        var value = text.ToString();
        if (_text.Text != value) _text.Text = value;
    }
}

