using Microsoft.Playwright;

namespace TrafficVista.Business;

/// <summary>One directly launched browser per UV; Playwright owns its fresh temporary profile.</summary>
internal sealed class OwnedChromeSession : IAsyncDisposable
{
    private ChromeProcessJob? _job;
    private CancellationTokenRegistration _cancel;
    internal IBrowser Browser { get; }
    // Kept for the result schema; Playwright now owns temporary-profile cleanup.
    internal bool ProfileCleanupDeferred => false;
    internal ulong PeakMemoryBytes => _job?.PeakMemoryBytes ?? 0;

    private OwnedChromeSession(IBrowser browser) => Browser = browser;

    internal static async Task<OwnedChromeSession> StartAsync(IPlaywright playwright,
        BrowserTypeLaunchOptions options, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        options.Timeout ??= 30_000;
        options.ArtifactsDir ??= directory;
        // Launch has no cancellation API. Retain the launch slot until the bounded launch
        // finishes, establish process ownership, then honor cancellation and clean up.
        var browser = await playwright.Chromium.LaunchAsync(options).ConfigureAwait(false);
        var session = new OwnedChromeSession(browser);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cdp = await browser.NewBrowserCDPSessionAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var info = (await cdp.SendAsync("SystemInfo.getProcessInfo").WaitAsync(timeout.Token).ConfigureAwait(false))!.Value;
                var root = info.GetProperty("processInfo").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "browser");
                session._job = ChromeProcessJob.Attach(root.GetProperty("id").GetInt32());
                session._job.AttachExistingProcesses();
            }
            finally { await cdp.DetachAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            session._cancel = token.Register(session.Kill);
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal void Abort() => Kill();

    private void Kill()
    {
        try { _job?.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.DisposeAsync().ConfigureAwait(false);
        Task? close = null;
        try
        {
            close = Browser.CloseAsync();
            await close.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (close is not null) _ = close.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        finally
        {
            Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (_job is not null) await _job.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                while (_job is not null && _job.ActiveProcesses > 0) await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            finally { _job?.Dispose(); }
        }
    }
}
