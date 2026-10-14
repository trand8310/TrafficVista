using Newtonsoft.Json.Linq;

namespace TrafficVista.Net.Models;

/// <summary>
/// 一次领取的统一结果；IpInfoQueried=false 时 IpInfo 为空 JObject。
/// IpInfo 不建立字段模型：IpInfo 接口返回什么 JSON 就完整保留什么，
/// 未知字段、嵌套对象与原始值全部留在 JObject 中。
/// </summary>
public sealed record ProxyAcquireResult(ProxyEndpoint Proxy, JObject IpInfo)
{
    public string? RealIp { get; init; }
    public string RealIpSource { get; init; } = "none";
    public bool IpInfoQueried { get; init; }
    public override string ToString() => $"{Proxy}，IpInfo 顶层字段 {IpInfo.Count} 个";
}
