using System.Runtime.CompilerServices;

namespace TrafficVista;

internal static class ExceptionHandlingTest
{
    public static string Run(GlobalExceptionHandling exceptionHandling)
    {
        var uiExceptionsBefore = exceptionHandling.HandledUiExceptions;
        using var taskHandled = new ManualResetEventSlim();
        var taskObserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> taskObserver = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(ex => ex.Message == "TrafficVista exception test: Task"))
            {
                taskObserved = e.Observed;
                taskHandled.Set();
            }
        };
        TaskScheduler.UnobservedTaskException += taskObserver;
        try
        {
            // 真实消息循环中的异常：隐藏测试窗体，不弹对话框。
            using var form = new Form { ShowInTaskbar = false, Opacity = 0 };
            form.Shown += (_, _) =>
            {
                form.BeginInvoke(new Action(() => throw new InvalidOperationException("TrafficVista exception test: UI")));
                form.BeginInvoke(new Action(form.Close));
            };
            Application.Run(form);
            if (exceptionHandling.HandledUiExceptions != uiExceptionsBefore + 1)
                throw new InvalidOperationException("UI 异常没有进入全局处理器。");

            var weakTask = CreateUnobservedTask();
            for (var attempt = 0; attempt < 10 && !taskHandled.IsSet; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                taskHandled.Wait(100);
            }
            GC.KeepAlive(weakTask);
            if (!taskHandled.IsSet || !taskObserved)
                throw new InvalidOperationException("未观察的 Task 异常没有被全局记录并设置为已观察。");
            return "PASS\n真实 UI 消息循环异常已处理。\n未观察的 Task 异常已处理，SetObserved 已生效。";
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= taskObserver;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedTask()
    {
        var task = Task.Run(() => throw new InvalidOperationException("TrafficVista exception test: Task"));
        if (!SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)))
            throw new TimeoutException("异常测试任务没有完成。");
        // IsCompleted 不读取 Exception，保留“未观察”状态供 GC 触发事件。
        return new WeakReference(task);
    }
}
