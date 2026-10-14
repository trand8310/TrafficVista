namespace TrafficVista.Net.Models;

/// <summary>
/// IProxyService 对外的失败异常：区分供应商与阶段（route/acquire/ipinfo/ttl），
/// 领取代理与查询 IpInfo 的错误分别报告。
/// </summary>
public sealed class ProxyServiceException : Exception
{
    public ProxyServiceException(string vendorId, string phase, string message, Exception? inner = null)
        : base(message, inner)
    {
        VendorId = vendorId;
        Phase = phase;
    }

    /// <summary>供应商标识；路由阶段可能为空。</summary>
    public string VendorId { get; }

    /// <summary>失败阶段：route（路由）、acquire（领取）、ipinfo（查询）、ttl（有效期）。</summary>
    public string Phase { get; }
}
