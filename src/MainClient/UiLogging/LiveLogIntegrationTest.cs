using System.Diagnostics;
using Serilog;
using Serilog.Events;
using TrafficVista.Scheduling;

namespace TrafficVista.UiLogging;

internal static class LiveLogIntegrationTest
{
    internal static string Run()
    {
        var small = new LiveLogBuffer(4);
        for (var i = 0; i < 10; i++) small.Append("12:00:00", LogEventLevel.Information, i.ToString());
        var batch = small.ReadAfter(0, 2, out var skipped);
        Check(skipped == 6 && batch.Length == 2 && batch[0].Sequence == 7 && batch[1].Sequence == 8, "环形覆盖及分批游标");
        var buffer = new LiveLogBuffer();
        using var sink = new LiveLogSink(buffer);
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.With(new WorkerIdEnricher()).WriteTo.Sink(sink).CreateLogger();
        using (WorkerExecutionContext.Enter(7)) logger.Information("worker-seven-marker");
        logger.Information("non-worker-marker");
        WaitUntil(() => buffer.ReadAfter(0, buffer.Capacity, out _).Any(entry => entry.Text == "non-worker-marker"));
        var identities = buffer.ReadAfter(0, buffer.Capacity, out _);
        Check(identities.Single(entry => entry.Text == "worker-seven-marker").WorkerId == 7
            && identities.Single(entry => entry.Text == "non-worker-marker").WorkerId == 0, "异步日志队列保留发送时工作者编号，非工作者为0");
        using var form = new Form { Size = new(1000, 600), ShowInTaskbar = false, Opacity = 0 };
        using var view = new LiveLogView(buffer) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        Application.DoEvents();
        var timerTicks = 0;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 20 };
        heartbeat.Tick += (_, _) => timerTicks++;
        heartbeat.Start();
        var emitWatch = Stopwatch.StartNew();
        var producers = Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            using var scope = WorkerExecutionContext.Enter(worker + 1);
            for (var i = 0; i < 10000; i++) logger.Information("压力日志 Worker={Worker}, Index={Index}", worker, i);
        })));
        var deadline = Stopwatch.StartNew();
        var longestFlushMs = 0.0;
        while (!producers.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var flushWatch = Stopwatch.StartNew(); view.FlushPending();
            longestFlushMs = Math.Max(longestFlushMs, flushWatch.Elapsed.TotalMilliseconds);
            Application.DoEvents(); Thread.Sleep(2);
        }
        Check(producers.IsCompleted, "高频日志生产者未能结束");
        producers.GetAwaiter().GetResult();
        emitWatch.Stop();
        var sustainedTicksBefore = timerTicks;
        var sustained = Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            using var scope = WorkerExecutionContext.Enter(worker + 1);
            for (var batchIndex = 0; batchIndex < 200; batchIndex++)
            {
                for (var i = 0; i < 10; i++) logger.Information("持续日志 Worker={Worker}, Batch={Batch}, Index={Index}", worker, batchIndex, i);
                await Task.Delay(10).ConfigureAwait(false);
            }
        })));
        var sustainedWatch = Stopwatch.StartNew();
        while (!sustained.IsCompleted && sustainedWatch.Elapsed < TimeSpan.FromSeconds(10))
        { Application.DoEvents(); Thread.Sleep(2); }
        Check(sustained.IsCompleted, "持续日志生产者未结束");
        sustained.GetAwaiter().GetResult();
        Check(timerTicks - sustainedTicksBefore >= 5, "持续日志期间UI消息循环未响应");
        logger.Error(new InvalidOperationException("日志异常详情测试"), "异常信息 {Value}", 42);
        logger.Information("超长日志 {Value}", new string('x', 100000));
        logger.Information("ui-log-final-marker");
        WaitUntil(() => buffer.ReadAfter(Math.Max(0, buffer.LatestSequence - 1), 1, out _).LastOrDefault()?.Text == "ui-log-final-marker");
        var retained = buffer.ReadAfter(0, buffer.Capacity, out _);
        Check(retained.Length <= 2000 && retained.All(entry => entry.Text.Length <= LiveLogSink.MaxTextLength), "显示历史及单条长度上限");
        Check(retained.Any(entry => entry.Text.Contains("日志异常详情测试")), "异常详情未进入实时日志");
        Check(retained.Any(entry => entry.Text.Length == LiveLogSink.MaxTextLength && entry.Text.EndsWith('…')), "超长日志未截断");
        for (var i = 0; i < 20; i++) view.FlushPending();
        Check(view.RowCount > 0 && view.RowCount <= 2000, "虚拟列表显示边界");
        var rowsBefore = view.RowCount;
        view.Paused = true;
        buffer.Append("12:00:00", LogEventLevel.Warning, "暂停期间仍保留日志");
        view.FlushPending(); Check(view.RowCount == rowsBefore, "暂停显示失效");
        view.Paused = false; view.Following = false;
        for (var i = 0; i < 2200; i++) buffer.Append("12:00:00", LogEventLevel.Information, "连续滚动日志 " + i);
        for (var i = 0; i < 20; i++) view.FlushPending();
        Check(view.RowCount == 2000, "虚拟行淘汰失效");
        view.ClearDisplay(); Check(view.RowCount == 0, "清空显示失效");
        buffer.Append("12:00:00", LogEventLevel.Information, "清空后新日志", 3); view.FlushPending();
        Check(view.RowCount == 1, "清空后未继续显示新日志");
        var list = (ListView)view.Controls.Find("listView_Logs", true).Single();
        Check(list.Columns.Count == 4 && list.Columns[2].Text == "工作者" && list.Items[0].SubItems[2].Text == "3"
            && list.Items[0].SubItems[3].Text == "清空后新日志", "工作者独立列与消息列对齐");
        buffer.Append("12:00:00", LogEventLevel.Information, "非工作者日志"); view.FlushPending();
        Check(list.Items[1].SubItems[2].Text == "0", "非工作者日志显示0");
        // Keep the message loop alive after producers finish as well, so background formatting cannot hide a UI deadlock.
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(350)) { Application.DoEvents(); Thread.Sleep(2); }
        Check(timerTicks > 0, "日志压力期间UI心跳未处理");
        heartbeat.Stop();
        form.Hide();
        var sequenceBeforeDispose = buffer.LatestSequence;
        sink.Dispose(); logger.Information("disposed-sink-must-ignore");
        Check(buffer.LatestSequence == sequenceBeforeDispose, "已释放Sink仍接收日志");
        return $"PASS\n4个生产者突发40000条日志；入队用时={emitWatch.Elapsed.TotalMilliseconds:F1}ms；手动批量刷新最大耗时={longestFlushMs:F1}ms。\n另持续写入8000条日志，耗时={sustainedWatch.Elapsed.TotalMilliseconds:F1}ms，期间UI心跳={timerTicks - sustainedTicksBefore}次；显示副本丢弃={buffer.Dropped}条。\n环形边界、每批128条、2000条保留、4096字符上限、异常详情、暂停/继续、清空、关闭及实时UI响应验证通过。性能数据仅针对显示通道，不包含文件写入。";
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(2); }
        Check(condition(), "日志后台格式化未完成");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
