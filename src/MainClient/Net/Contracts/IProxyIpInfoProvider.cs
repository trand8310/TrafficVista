using Newtonsoft.Json.Linq;
using TrafficVista.Net.Models;

namespace TrafficVista.Net.Contracts;

/// <summary>
/// 负责通过指定代理或本机直连访问 IpInfo 接口；代理供应商更换不影响本实现。
/// 正文解析为 JObject，完整保留未知字段、嵌套对象和原始值。
/// </summary>
public interface IProxyIpInfoProvider
{
    /// <summary>通过 proxy 指向的代理通道查询配置的 IpInfo 接口；失败抛出异常（由调用方分阶段记录）。</summary>
    Task<JObject> QueryAsync(ProxyEndpoint proxy, CancellationToken cancellationToken);
    /// <summary>通过本机直连通道查询同一组接口；不领取代理、不使用系统HTTP代理。</summary>
    Task<JObject> QueryDirectAsync(CancellationToken cancellationToken);
}
