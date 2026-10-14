using TrafficVista.Configuration;

namespace TrafficVista.LocalTesting;

// 每个 UV 都有新对象及唯一设备描述 ID，使用标准移动视口模拟，不注入硬件指纹。
public sealed record LocalDeviceProfile(string DeviceId, string Name, int Width, int Height, float Scale, string UserAgent);
public sealed record LocalTaskProxy(string Server);
public sealed record UvBrowserRun(long TaskId, long UvId, Uri Target, string Directory,
    LocalDeviceProfile Device, LocalTaskProxy? Proxy, TrafficVistaOptions Options, TimeSpan Dwell);

public interface IUvDeviceProvider
{
    ValueTask<LocalDeviceProfile> AcquireAsync(long taskId, long uvId, CancellationToken token);
}
public interface ILocalTaskProxyProvider
{
    ValueTask<LocalTaskProxy?> AcquireAsync(TrafficVistaOptions options, CancellationToken token);
}
public interface IUvBrowserRunner
{
    Task<bool> RunAsync(UvBrowserRun run, CancellationToken token);
}

public sealed class LocalDeviceProfileProvider : IUvDeviceProvider
{
    private long _next;
    public ValueTask<LocalDeviceProfile> AcquireAsync(long taskId, long uvId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var number = Interlocked.Increment(ref _next);
        var (width, height, scale) = (number % 3) switch
        {
            0 => (412, 829, 2.625f), 1 => (390, 844, 3f), _ => (360, 800, 2.5f)
        };
        return ValueTask.FromResult(new LocalDeviceProfile($"{taskId}:{uvId}", $"LocalMobile-{number}",
            width, height, scale, ChromeStartup.MobileUserAgent));
    }
}

public sealed class LocalTaskProxyProvider(LocalTestingOptions settings) : ILocalTaskProxyProvider
{
    public ValueTask<LocalTaskProxy?> AcquireAsync(TrafficVistaOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.IsProxyMode) return ValueTask.FromResult<LocalTaskProxy?>(null);
        if (string.IsNullOrWhiteSpace(settings.ProxyServer))
            throw new InvalidOperationException("本地代理模式请先配置 appsettings.json 的 LocalTesting:ProxyServer。");
        LocalEndpointPolicy.RequireLoopback(settings.ProxyServer, "本地代理");
        return ValueTask.FromResult<LocalTaskProxy?>(new(settings.ProxyServer));
    }
}
