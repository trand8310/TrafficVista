using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// 供应道路由：按供应商标识找到对应适配器并校验其配置。
/// 标识与 URL 分开配置，同一家更换 URL 或账号不需要修改路由逻辑；
/// 新增一家供应商时只需新增适配器并在 DI 注册。
/// </summary>
public sealed class ProxyProviderRouter
{
    private readonly Dictionary<string, IProxyVendorAdapter> _adapters;
    private readonly NetOptions _options;

    public ProxyProviderRouter(IEnumerable<IProxyVendorAdapter> adapters, NetOptions options)
    {
        _options = options;
        _adapters = new Dictionary<string, IProxyVendorAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.VendorId))
                throw new InvalidDataException($"适配器 {adapter.GetType().Name} 的 VendorId 不能为空。");
            if (_adapters.TryAdd(adapter.VendorId, adapter)) continue;
            throw new InvalidDataException($"供应商标识“{adapter.VendorId}”注册了多个适配器。");
        }
    }

    /// <summary>已注册且配置可用（启用且 URL 合法）的供应商标识。</summary>
    public IReadOnlyList<string> VendorIds =>
        _adapters.Keys.Where(id => _options.IsVendorUsable(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>解析供应商标识为可用适配器；vendorId 为 null 时使用配置的默认供应商。</summary>
    public IProxyVendorAdapter Resolve(string? vendorId)
    {
        var id = string.IsNullOrWhiteSpace(vendorId) ? _options.DefaultVendor : vendorId;
        if (string.IsNullOrWhiteSpace(id))
            throw new ProxyServiceException("", "route", "未指定供应商，且未配置默认供应商（Net:DefaultVendor）。");
        if (!_adapters.TryGetValue(id, out var adapter))
            throw new ProxyServiceException(id, "route", $"供应商“{id}”没有已注册的适配器。");
        if (!_options.Vendors.TryGetValue(id, out var vendor))
            throw new ProxyServiceException(id, "route", $"供应商“{id}”没有配置（Net:Vendors:{id}）。");
        if (!vendor.Enabled)
            throw new ProxyServiceException(id, "route", $"供应商“{id}”未启用。");
        // 领取 URL 的合法性由 ProxyService 按“调用方传入优先、配置兜底”后的生效值统一校验（route 阶段）。
        return adapter;
    }

    /// <summary>取该供应商已校验过的配置（Resolve 之后调用）。</summary>
    public ProxyVendorOptions GetConfigured(string vendorId) => _options.Vendors[vendorId];
}
