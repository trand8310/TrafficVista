using TrafficVista.Net.Models;

namespace TrafficVista.Net.Contracts;

/// <summary>
/// 每家代理供应商一个适配器，只负责自己的协议：
/// 如何构造领取请求、如何提交认证、如何判断成败、如何转成统一 ProxyEndpoint。
/// 失败时抛出描述具体原因的异常（ProxyProtocolException 等），由 ProxyService 汇总为 ProxyServiceException。
/// </summary>
public interface IProxyVendorAdapter
{
    /// <summary>供应商标识；与 URL 分开配置，换 URL/账号不改路由。</summary>
    string VendorId { get; }

    /// <summary>
    /// 领取一个代理并统一为连接信息；apiUrl 为本次生效的领取接口 URL（调用方传入优先，未传时用配置默认值），实现必须使用它；必须尊重取消令牌。
    /// </summary>
    Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, CancellationToken cancellationToken);
    Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, ProxyAcquireRequest request, CancellationToken cancellationToken)
        => AcquireProxyAsync(apiUrl, cancellationToken);
}
