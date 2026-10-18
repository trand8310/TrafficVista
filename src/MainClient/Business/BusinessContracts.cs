using Newtonsoft.Json.Linq;
using TrafficVista.Configuration;
using TrafficVista.Contracts;

namespace TrafficVista.Business;

/// <summary>设备接口返回的单台设备指纹；DeviceKey 在发放时分配，保证每次 UV 会话唯一。</summary>
public sealed record BusinessDeviceProfile(string DeviceKey, string Make, string Model, string Name, string OsVersion,
    int ScreenWidth, int ScreenHeight, string UserAgent, int CpuCores, string Vendor, string Renderer, string? Oaid)
{
    public float? DevicePixelRatio { get; init; }
}

/// <summary>
/// 任务网络出口信息。代理模式 Server 为裸 host:port；直连信息的 Server 为 null。
/// IpInfo 是经所选网络通道查询得到的真实出口信息（完整 JSON）。
/// </summary>
public sealed record BusinessProxy(string? Server, JObject? IpInfo = null)
{
    public bool HasProxy => !string.IsNullOrWhiteSpace(Server);
    public string Protocol { get; init; } = "http";
    public string? RealIp { get; init; }
    public string RealIpSource { get; init; } = "none";
    [Newtonsoft.Json.JsonIgnore] public string? Username { get; init; }
    [Newtonsoft.Json.JsonIgnore] public string? Password { get; init; }
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Playwright Proxy.Server 需要带协议前缀。</summary>
    public string? PlaywrightServer => HasProxy ? Protocol + "://" + Server : null;
    public override string ToString() => PlaywrightServer ?? "direct";

    public DateTimeOffset? Deadline(int ipTtl)
    {
        // Direct exit metadata has no rented-proxy lifetime.
        if (!HasProxy) return null;
        var configured = ipTtl > 0 ? AcquiredAt.AddSeconds(ipTtl) : (DateTimeOffset?)null;
        return ExpiresAt is { } vendor && (configured is null || vendor < configured) ? vendor : configured;
    }
}

/// <summary>一次 UV 执行描述：独立 Chrome 进程、Context 和设备；PageViews 为总访问次数。</summary>
public sealed record BusinessUvRun(long TaskId, long UvId, Uri Target, string Directory,
    BusinessDeviceProfile Device, BusinessProxy? Proxy, TrafficVistaOptions Options,
    TimeSpan Dwell, int PageViews, JObject? Jstext)
{
    public int WorkerId { get; init; }
    internal Action? BrowserStartQueued { get; init; }
    public TimeSpan? MaximumDwell { get; init; }
    public TimeSpan NextDwell() => MaximumDwell is { } max
        ? Dwell + TimeSpan.FromSeconds(Random.Shared.NextDouble() * (max - Dwell).TotalSeconds) : Dwell;
}

/// <summary>线程安全；每个 UV 发放一台设备，池空时由接口补拉。</summary>
public interface IBusinessDeviceProvider
{
    ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId, CancellationToken token);
}

/// <summary>每任务取得网络出口信息；代理模式领取新代理，直连按开关查询本机出口，不做跨任务缓存。</summary>
public interface IBusinessProxyProvider
{
    ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1);
}

/// <summary>执行一个 UV 会话；返回前必须完成浏览器关闭和资源清理，必须响应取消。</summary>
public interface IBusinessUvRunner
{
    Task<bool> RunAsync(BusinessUvRun run, CancellationToken token);
}
