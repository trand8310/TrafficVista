using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Options;
using TrafficVista.Net.Models;
using TrafficVista.Contracts;
using TrafficVista.Net;

namespace TrafficVista.Business;

/// <summary>
/// 业务链路网络出口来源：代理模式经 Net 模块 IProxyService 领取——供应道路由、瑕疵代理换新重试（Net:AcquireRetryCount）、
/// 每供应商并发上限，以及按界面开关查询真实 IP/详情。代理属于单个任务：每次消费无条件领取全新代理，
/// 本任务全部 UV 共享，不跨任务缓存。领取失败（含重试次数用尽）抛 ProxyServiceException，
/// 由消费者记 Error 日志后丢弃任务、接下一个任务。
/// 领取接口 URL 一律使用界面配置的“代理接口”（ProxyIpUrl）并传给所有供应商；供应商配置只提供提取规则与参数。
/// 直连模式按真实IP/详情开关查询本机出口，携带元数据但不创建代理路由或租赁TTL。
/// </summary>
public sealed class NetProxyProvider(IProxyService proxies, ILogger logger, IProxyIpInfoProvider ipInfo) : IBusinessProxyProvider
{
    public async ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1)
    {
        token.ThrowIfCancellationRequested();
        if (!options.IsProxyMode)
        {
            if (!options.ResolveRealIp && !options.FetchIpDetails) return null;
            // A single direct race supplies both the exit IP and requested environment details.
            try
            {
                var info = await ipInfo.QueryDirectAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var realIp = IpInfoValidation.GetRealIp(info)
                    ?? throw new ProxyProtocolException("直连IP信息未包含有效出口IP。");
                logger.Information("直连出口信息就绪，TaskId={TaskId}，IpInfoQueried=true", task.Id);
                return new BusinessProxy(null, info) { RealIp = realIp, RealIpSource = "direct-ipinfo" };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.Warning(ex, "直连IP信息查询失败，TaskId={TaskId}，未领取代理", task.Id);
                throw;
            }
        }
        // 所有供应商统一用界面“代理接口”URL 领取：没配好就直接失败（任务按丢弃策略处理），不回退配置默认。
        var apiUrl = options.ProxyIpUrl?.Trim() ?? "";
        if (!ProxyVendorOptions.IsHttpUrl(apiUrl))
            throw new InvalidOperationException(
                "代理模式必须先配置代理接口 URL（ProxyIpUrl，绝对 http/https 地址）；URL 可能含凭证，不写入日志。");
        // 供应商由 Net:DefaultVendor（后续界面下拉框）选择；领取 URL 由调用方决定，两者互不影响。
        var request = new ProxyAcquireRequest { Task = task, Settings = options, Count = count };
        var result = await proxies.AcquireAsync(request, apiUrl: apiUrl, cancellationToken: token).ConfigureAwait(false);
        logger.Information("业务代理就绪，TaskId={TaskId}，Vendor={Vendor}，Proxy={Proxy}，RealIpSource={RealIpSource}，IpInfoQueried={IpInfoQueried}",
            task.Id, result.Proxy.VendorId, result.Proxy.SafeAddress, result.RealIpSource, result.IpInfoQueried);
        // BusinessProxy 保存裸 host:port；PlaywrightServer 在创建浏览器时添加协议。IpInfo 完整 JSON 随任务快照留档。
        return new BusinessProxy($"{result.Proxy.Host}:{result.Proxy.Port}", result.IpInfo)
        {
            Protocol = result.Proxy.Protocol, Username = result.Proxy.Username, Password = result.Proxy.Password,
            RealIp = result.RealIp, RealIpSource = result.RealIpSource,
            AcquiredAt = result.Proxy.AcquiredAt, ExpiresAt = result.Proxy.ExpiresAt
        };
    }
}
