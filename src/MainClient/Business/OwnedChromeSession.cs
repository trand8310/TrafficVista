using Microsoft.Playwright;
using System.Diagnostics;

namespace TrafficVista.Business;

/// <summary>One directly launched browser per UV; Playwright owns its fresh temporary profile.</summary>
internal sealed class OwnedChromeSession : IAsyncDisposable
{
    private ChromeProcessJob? _job;
    private CancellationTokenRegistration _cancel;
    internal IBrowser Browser { get; }
    private readonly Func<string, IDisposable>? _trackCleanup;
    private readonly Action<string, Exception?>? _cleanupNotice;
    private readonly TimeSpan _closeGrace;
    private readonly object _disposeSync = new();
    private Task? _dispose;
    private Task _startupDetach = Task.CompletedTask;
    internal int? ProcessId => _job?.Process.Id;
    // Playwright may still be removing its temporary profile after Chrome exits.
    internal bool ProfileCleanupDeferred { get; private set; }
    internal bool StartupCdpDetachDeferred { get; private set; }
    internal ulong PeakMemoryBytes => _job?.PeakMemoryBytes ?? 0;

    private OwnedChromeSession(IBrowser browser, Func<string, IDisposable>? trackCleanup, Action<string, Exception?>? cleanupNotice, TimeSpan closeGrace)
    { Browser = browser; _trackCleanup = trackCleanup; _cleanupNotice = cleanupNotice; _closeGrace = closeGrace; }

    internal static async Task<OwnedChromeSession> StartAsync(IPlaywright playwright,
        BrowserTypeLaunchOptions options, string directory, CancellationToken token, Func<string, IDisposable>? trackPhase = null,
        Func<string, IDisposable>? trackCleanup = null, Action<string, Exception?>? cleanupNotice = null,
        Action<TimeSpan>? processAttachComplete = null, TimeSpan? closeGrace = null, Action? launchStarting = null,
        Func<string, IDisposable>? trackAuxiliary = null)
    {
        token.ThrowIfCancellationRequested();
        options.Timeout ??= 30_000;
        options.ArtifactsDir ??= directory;
        // Launch has no cancellation API. Retain the launch slot until the bounded launch
        // finishes, establish process ownership, then honor cancellation and clean up.
        var browser = await MeasureAsync("playwrightLaunch", () =>
        {
            launchStarting?.Invoke();
            return playwright.Chromium.LaunchAsync(options);
        }, trackPhase).ConfigureAwait(false);
        var session = new OwnedChromeSession(browser, trackCleanup, cleanupNotice, closeGrace ?? TimeSpan.FromSeconds(1));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cdp = await MeasureAsync("launchCdpConnect", () => browser.NewBrowserCDPSessionAsync().WaitAsync(timeout.Token), trackPhase).ConfigureAwait(false);
            try
            {
                var info = (await MeasureAsync("launchProcessQuery", () => cdp.SendAsync("SystemInfo.getProcessInfo").WaitAsync(timeout.Token), trackPhase).ConfigureAwait(false))!.Value;
                var attachStarted = Stopwatch.GetTimestamp();
                try
                {
                    await MeasureAsync("launchMainProcessAttach", () =>
                    {
                        var root = info.GetProperty("processInfo").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "browser");
                        session._job = ChromeProcessJob.Attach(root.GetProperty("id").GetInt32());
                        return Task.FromResult(true);
                    }, trackPhase).ConfigureAwait(false);
                    session._job!.AttachExistingProcesses(trackPhase);
                }
                finally { processAttachComplete?.Invoke(Stopwatch.GetElapsedTime(attachStarted)); }
            }
            finally
            {
                if (session._job is null)
                {
                    // Failed ownership is still a failed launch; do not publish an unowned browser.
                    await CompleteStartupDetachAsync(cdp.DetachAsync(), TimeSpan.FromSeconds(2), () => false).ConfigureAwait(false);
                }
                else
                {
                    // This session only queried process ownership. It does not configure the page.
                    // All native processes have been bound before diagnostic teardown overlaps page setup.
                    session._startupDetach = session.DetachDiagnosticAsync(cdp, trackAuxiliary);
                }
            }
            session._cancel = token.Register(session.Kill);
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action, Func<string, IDisposable>? track)
    {
        using var timing = track?.Invoke(phase);
        try { return await action().ConfigureAwait(false); }
        catch (Exception ex) { ex.Data["BrowserStartupPhase"] ??= phase; throw; }
    }

    internal void Abort() => Kill();

    private async Task DetachDiagnosticAsync(ICDPSession cdp, Func<string, IDisposable>? track)
    {
        using var timing = track?.Invoke("launchCdpDetach");
        await ObserveDiagnosticDetachAsync(cdp.DetachAsync, TimeSpan.FromSeconds(2), (deferred, error) =>
        {
            StartupCdpDetachDeferred = deferred;
            if (error is not null)
                Notice("启动诊断CDP解绑未完成；进程归属已建立，页面准备继续，诊断会话随独立浏览器关闭释放", error);
        }).ConfigureAwait(false);
    }

    internal static async Task ObserveDiagnosticDetachAsync(Func<Task> detach, TimeSpan timeout,
        Action<bool, Exception?> completed)
    {
        Task? pending = null;
        try
        {
            pending = detach();
            await pending.WaitAsync(timeout).ConfigureAwait(false);
            completed(false, null);
        }
        catch (Exception ex) { completed(pending is { IsCompleted: false }, ex); }
        finally
        {
            if (pending?.IsFaulted == true) _ = pending.Exception;
            else if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static async Task<bool> CompleteStartupDetachAsync(Task detach, TimeSpan timeout, Func<bool> browserHealthy)
    {
        try { await detach.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) when (!detach.IsFaulted && !detach.IsCanceled && browserHealthy())
        { return detach.IsCompletedSuccessfully; }
        finally
        {
            // The browser owns this diagnostic session; observe late failure even if startup itself fails.
            if (detach.IsFaulted) _ = detach.Exception;
            else if (!detach.IsCompleted)
                _ = detach.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void Kill()
    {
        try { _job?.Kill(); }
        catch (InvalidOperationException ex) { Notice("终止进程树失败", ex); }
        catch (System.ComponentModel.Win32Exception ex) { Notice("终止进程树失败", ex); }
    }

    private void Notice(string message, Exception? error = null)
    { try { _cleanupNotice?.Invoke(message, error); } catch { } }
    private string DescribeProcesses()
    {
        try { return $"PID={ProcessId}，MainExited={_job?.Process.HasExited}，BrowserConnected={Browser.IsConnected}，ActiveProcesses={_job?.ActiveProcesses}"; }
        catch (Exception ex) { return "进程状态查询失败：" + ex.Message; }
    }

    public ValueTask DisposeAsync()
    { lock (_disposeSync) return new(_dispose ??= DisposeCoreAsync()); }

    internal static async Task<string> WaitForCloseGraceAsync(Task close, Task? mainExit, TimeSpan grace)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(grace, timer.Token);
        try
        {
            var winner = await Task.WhenAny(mainExit is null ? new[] { close, delay } : new[] { close, mainExit, delay }).ConfigureAwait(false);
            if (winner == close) { await close.ConfigureAwait(false); return "正常关闭完成"; }
            if (winner == mainExit) { await mainExit!.ConfigureAwait(false); return "主进程已退出，提前收尾"; }
            return "正常关闭宽限已到";
        }
        finally { timer.Cancel(); }
    }

    private async Task DisposeCoreAsync()
    {
        Notice("开始关闭浏览器，" + DescribeProcesses());
        using (_trackCleanup?.Invoke("cleanupCancelRegistration"))
            await _cancel.DisposeAsync().ConfigureAwait(false);
        Task? close = null;
        BrowserCloseCoordinator.Lease? lease = null;
        try
        {
            using (_trackCleanup?.Invoke("cleanupCloseQueue"))
                lease = await BrowserCloseCoordinator.Shared.ReserveAsync(() =>
                {
                    Notice("Playwright收尾请求达到64个，先终止本UV进程再等待空位，" + DescribeProcesses());
                    Kill();
                }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            using (_trackCleanup?.Invoke("cleanupBrowserClose"))
            {
                close = Browser.CloseAsync();
                using var monitor = new CancellationTokenSource();
                var mainExit = _job?.Process.WaitForExitAsync(monitor.Token);
                try
                {
                    var reason = await WaitForCloseGraceAsync(close, mainExit, _job is null ? TimeSpan.FromSeconds(3) : _closeGrace).ConfigureAwait(false);
                    Notice($"关闭宽限结束，Reason={reason}，GraceMs={(_job is null ? 3000 : _closeGrace.TotalMilliseconds):F0}，" + DescribeProcesses());
                }
                finally { monitor.Cancel(); }
            }
        }
        catch (Exception ex)
        {
            Notice("Browser.Close或收尾准入失败，继续终止进程树，" + DescribeProcesses(), ex);
            if (lease is null) throw;
        }
        finally
        {
            try
            {
                using (_trackCleanup?.Invoke("cleanupTerminate")) Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using (_trackCleanup?.Invoke("cleanupMainProcessExit"))
                    if (_job is not null) await _job.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                using (_trackCleanup?.Invoke("cleanupChildProcessExit"))
                    while (_job is not null && _job.ActiveProcesses > 0) await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                Notice((_job is null ? "未绑定进程树，已请求关闭，" : "进程退出确认完成，") + DescribeProcesses());
            }
            catch (Exception ex) { Notice("等待进程退出失败（主进程与子进程共用5秒期限），" + DescribeProcesses(), ex); throw; }
            finally
            {
                try
                {
                    // The bounded diagnostic observer must finish before timing data and driver leases are released.
                    try { await _startupDetach.ConfigureAwait(false); }
                    finally { using (_trackCleanup?.Invoke("cleanupJobDispose")) _job?.Dispose(); }
                }
                finally
                {
                    if (lease is not null)
                    {
                        if (close is not null && !close.IsCompleted)
                        {
                            ProfileCleanupDeferred = true;
                            Notice("Playwright剩余关闭任务转交有界收尾跟踪");
                            BrowserCloseCoordinator.Shared.Defer(close, lease, error => Notice("Playwright后台关闭任务结束", error));
                        }
                        else
                        {
                            if (close?.IsFaulted == true) _ = close.Exception;
                            lease.Dispose();
                        }
                    }
                }
            }
        }
    }
}
