using Microsoft.Playwright;
using Serilog;
using System.Text;

namespace TrafficVista.Business;

internal static class PlaywrightDriversIntegrationTest
{
    internal static async Task VerifyAsync(ILogger logger, StringBuilder report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var created = 0;
        await using var drivers = new BusinessPlaywrightDrivers(logger, async () =>
        { Interlocked.Increment(ref created); return await Playwright.CreateAsync(); });
        drivers.Configure(4, 70);
        Check(drivers.Snapshot.Created == 0 && drivers.DriverIdFor(1) == drivers.DriverIdFor(5)
            && drivers.DriverIdFor(70) == 2, "fixed driver mapping is stable and lazy");
        var leases = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => drivers.AcquireAsync(1, timeout.Token)));
        var other = await drivers.AcquireAsync(2, timeout.Token);
        try
        {
            Check(created == 2 && leases.All(lease => ReferenceEquals(lease.Driver, leases[0].Driver))
                && !ReferenceEquals(other.Driver, leases[0].Driver), "80 simultaneous UV acquisitions initialize one driver per slot");
            Check(!await drivers.ReleaseIdleAsync(), "active browsers retain their drivers");
            try { drivers.Configure(0, 70); throw new InvalidOperationException("expected active configure rejection"); }
            catch (InvalidOperationException ex) when (ex.Message == "业务浏览器仍在使用Playwright驱动。") { }
        }
        finally { foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); } other.Dispose(); }
        Check(drivers.Snapshot.ActiveUvs == 0, "driver leases release exactly once");
        var closing = await BrowserCloseCoordinator.Shared.ReserveAsync(() => { }, TimeSpan.FromSeconds(1));
        try { Check(!await drivers.ReleaseIdleAsync(), "outstanding driver close requests block disposal"); }
        finally { closing.Dispose(); }
        Check(await drivers.ReleaseIdleAsync() && drivers.Snapshot.Created == 0, "idle drivers release after closes drain");

        drivers.Configure(0, 70);
        Check(drivers.Snapshot.Effective == 70 && Enumerable.Range(1, 70).Select(drivers.DriverIdFor).Distinct().Count() == 70,
            "automatic mode follows all 70 workers without eagerly creating drivers");
        using (var first = await drivers.AcquireAsync(1, timeout.Token))
        using (var last = await drivers.AcquireAsync(70, timeout.Token))
            Check(first.Id == 1 && last.Id == 70 && !ReferenceEquals(first.Driver, last.Driver)
                && drivers.Snapshot.Created == 2, "only active worker drivers are created in automatic mode");
        Check(await drivers.ReleaseIdleAsync(), "automatic mode releases idle drivers");
        drivers.Configure(1, 70);
        Check(drivers.DriverIdFor(1) == drivers.DriverIdFor(70), "single-driver compatibility mode");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var delayed = new BusinessPlaywrightDrivers(logger, async () =>
        { entered.SetResult(); await release.Task; return await Playwright.CreateAsync(); });
        using var canceled = new CancellationTokenSource();
        var acquisition = delayed.AcquireAsync(1, canceled.Token);
        await entered.Task.WaitAsync(timeout.Token);
        canceled.Cancel();
        Check(!acquisition.IsCompleted && delayed.Snapshot.ActiveUvs == 1, "creation retains ownership while cancellation is pending");
        release.SetResult();
        try { using var unexpected = await acquisition; throw new InvalidOperationException("expected cancellation"); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Check(delayed.Snapshot.ActiveUvs == 0 && await delayed.ReleaseIdleAsync(), "late-created driver is owned and disposed after cancellation");

        var attempts = 0;
        await using var recovering = new BusinessPlaywrightDrivers(logger, () => ++attempts == 1
            ? Task.FromException<IPlaywright>(new IOException("driver fixture")) : Playwright.CreateAsync());
        try { using var unexpected = await recovering.AcquireAsync(1, timeout.Token); throw new InvalidOperationException("expected creation failure"); }
        catch (IOException) { }
        using (var retry = await recovering.AcquireAsync(1, timeout.Token))
            Check(attempts == 2 && recovering.Snapshot.Created == 1, "failed initialization can retry without poisoning its slot");
        Check(await recovering.ReleaseIdleAsync(), "retry driver cleanup");
        report.AppendLine("PASS Playwright驱动：80并发请求同槽仅创建一次；固定/自动/单驱动映射，70槽按需创建；运行中保护、关闭收尾保护、取消、创建失败重试及释放。");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
