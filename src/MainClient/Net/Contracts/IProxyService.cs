using TrafficVista.Net.Models;

namespace TrafficVista.Net.Contracts;

/// <summary>
/// 业务统一调用入口：调用方提供供应商标识（null 表示用配置的默认供应商），
/// 一次调用得到代理及真实IP/详情；查询由本次请求的配置开关决定。
/// 需要查询时，未取得有效 IpInfo 即整体失败；未查询时 IpInfo 为空 JObject。
/// </summary>
public interface IProxyService
{
    /// <summary>当前注册且启用、配置可用的供应商标识（供后续界面下拉框使用）。</summary>
    IReadOnlyList<string> Vendors { get; }

    /// <summary>
    /// 领取代理并通过它查询 IpInfo；失败抛 ProxyServiceException（含 VendorId 与 Phase）。
    /// apiUrl 为本次领取接口 URL：传入时对所有供应商生效（供应商配置只提供提取规则与参数），
    /// 未传时用该供应商配置的 ApiUrl；两者都不可用则报路由阶段失败。
    /// </summary>
    Task<ProxyAcquireResult> AcquireAsync(string? vendorId = null, string? apiUrl = null, CancellationToken cancellationToken = default);
    /// <summary>传入完整任务、配置快照和默认为1的数量；地区/数量URL规则留在供应商扩展点。</summary>
    Task<ProxyAcquireResult> AcquireAsync(ProxyAcquireRequest request, string? vendorId = null, string? apiUrl = null,
        CancellationToken cancellationToken = default);
}
