using Serilog;
using System.Diagnostics;

namespace TrafficVista;

internal sealed class GlobalExceptionHandling : IDisposable
{
    private readonly ILogger _logger;
    private readonly bool _showDialogs;
    private int _showingDialog;
    private int _handledUiExceptions;

    internal int HandledUiExceptions => Volatile.Read(ref _handledUiExceptions);

    public GlobalExceptionHandling(ILogger logger, bool showDialogs)
    {
        _logger = logger.ForContext<GlobalExceptionHandling>();
        _showDialogs = showDialogs;
        // 必须在第一个窗口创建之前设置。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        try
        {
            _logger.Error(e.Exception, "未处理的 UI 线程异常");
            Interlocked.Increment(ref _handledUiExceptions);
            if (_showDialogs && Interlocked.Exchange(ref _showingDialog, 1) == 0)
            {
                try
                {
                    MessageBox.Show($"操作发生异常：{e.Exception.Message}\n\n详细日志：{AppLogging.LogDirectory}",
                        "TrafficVista", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Volatile.Write(ref _showingDialog, 0);
                }
            }
        }
        catch (Exception handlerException)
        {
            // 异常处理器自身不能再向消息循环抛异常。
            Trace.WriteLine(handlerException);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception exception)
                _logger.Fatal(exception, "未处理的进程级异常，IsTerminating={IsTerminating}", e.IsTerminating);
            else
                _logger.Fatal("未处理的进程级异常：{ExceptionObject}，IsTerminating={IsTerminating}",
                    e.ExceptionObject, e.IsTerminating);
        }
        catch (Exception handlerException)
        {
            Trace.WriteLine(handlerException);
        }
        finally
        {
            // 此事件无法阻止 CLR 终止进程；不要在这里等待浏览器异步清理。
            try { Log.CloseAndFlush(); }
            catch (Exception flushException) { Trace.WriteLine(flushException); }
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _logger.Error(e.Exception, "未观察的 Task 异常");
        }
        catch (Exception handlerException)
        {
            Trace.WriteLine(handlerException);
        }
        finally
        {
            // 兜底处理；正常业务仍需 await Task 并在业务边界处理异常。
            e.SetObserved();
        }
    }

    public void Dispose()
    {
        Application.ThreadException -= OnThreadException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}
