namespace TrafficVista.Net.Contracts;

/// <summary>
/// 代理供应商 IP 白名单处理：查询本机当前出口 IP，并调用供应商白名单接口登记。
/// 该服务不被任何业务流程自动调用——位置留在这里，需要时由调用方从容器取出酌情调用。
/// </summary>
public interface IProxyWhitelistService
{
    /// <summary>
    /// 直连（不经代理）请求配置的 IpInfo 接口，取本机当前出口 IP；取不到返回 null。
    /// 与代理竞速查询共用同一批接口，谁快用谁的。
    /// </summary>
    Task<string?> QueryCurrentEgressIpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询出口 IP 并登记到指定供应商的白名单。
    /// 白名单接口未配置（供应商 Parameters 无 WhitelistApiUrl）时跳过并返回 false；失败返回 false，不抛出。
    /// </summary>
    Task<bool> RefreshWhitelistAsync(string vendorId, CancellationToken cancellationToken = default);
}
