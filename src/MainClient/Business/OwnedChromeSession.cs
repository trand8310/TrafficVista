using System.Diagnostics;
using Microsoft.Playwright;

namespace TrafficVista.Business;

/// <summary>Owns exactly one Chrome process tree, its CDP connection and its temporary profile.</summary>
internal sealed class OwnedChromeSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ChromeProcessJob _job;
    private readonly string _profile;
    private readonly CancellationTokenRegistration _cancel;
    internal IBrowser? Browser { get; private set; }
    internal bool ProfileCleanupDeferred { get; private set; }
    internal ulong PeakMemoryBytes => _job.PeakMemoryBytes;

    private OwnedChromeSession(ChromeProcessJob job, string profile, CancellationToken token)
    {
        _job = job; _process = job.Process; _profile = profile;
        // Only this session's process tree is terminated; other Chrome instances are never enumerated/killed.
        _cancel = token.Register(Kill);
    }

    internal static async Task<OwnedChromeSession> StartAsync(IPlaywright playwright,
        BrowserTypeLaunchOptions options, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var profile = Path.GetFullPath(Path.Combine(directory, "chrome-profile"));
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(options.ExecutablePath!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in options.Args ?? []) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        info.ArgumentList.Add("--remote-debugging-port=0");
        info.ArgumentList.Add("--user-data-dir=" + profile);
        // CDP creates the only business window via NewContext/NewPage; do not open a default-profile blank window.
        info.ArgumentList.Add("--no-startup-window");
        if (options.Headless == true) info.ArgumentList.Add("--headless=new");
        if (options.Proxy is { } proxy) info.ArgumentList.Add("--proxy-server=" + proxy.Server);
        var job = ChromeProcessJob.Start(info);
        var process = job.Process;
        var session = new OwnedChromeSession(job, profile, token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            string[] lines = [];
            while (lines.Length < 2)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"Chrome 启动提前退出，ExitCode={process.ExitCode}。");
                try { if (File.Exists(portFile)) lines = await File.ReadAllLinesAsync(portFile, timeout.Token).ConfigureAwait(false); }
                catch (IOException) { }
                if (lines.Length < 2) await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            if (!int.TryParse(lines[0], out var port) || port is < 1 or > 65535
                || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                throw new InvalidDataException("Chrome 调试端点无效。");
            var connecting = playwright.Chromium.ConnectOverCDPAsync(
                $"ws://127.0.0.1:{port}{lines[1]}", new() { Timeout = 10000 });
            try { session.Browser = await connecting.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch
            {
                _ = connecting.ContinueWith(async finished =>
                {
                    if (finished.IsCompletedSuccessfully)
                    { try { await finished.Result.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } }
                    else _ = finished.Exception;
                }, TaskScheduler.Default).Unwrap();
                throw;
            }
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void Kill()
    {
        try { _job.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Browser is not null)
            {
                Task? close = null;
                try { close = Browser.CloseAsync(); await close.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception)
                {
                    if (close is not null) _ = close.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    // The owned process is the final cleanup authority.
                }
            }
        }
        finally
        {
            Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                while (_job.ActiveProcesses > 0) await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            finally { _job.Dispose(); }
            // This directory is created here, beneath this UV's run; no user profile is ever reused.
            if (Directory.Exists(_profile))
            {
                if ((File.GetAttributes(_profile) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("临时 Chrome profile 被替换为链接，保留供人工检查。");
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try { if (Directory.Exists(_profile)) Directory.Delete(_profile, recursive: true); break; }
                    catch (IOException) when (attempt < 9) { await Task.Delay(100).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) when (attempt < 9) { await Task.Delay(100).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Antivirus/indexers can retain handles after every Chrome process has exited.
                        // A marked, completed run is retried by retention; this is not a navigation failure.
                        ProfileCleanupDeferred = true;
                        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_profile)!, "profile-cleanup.pending"), ex.Message);
                    }
                }
            }
        }
    }
}
