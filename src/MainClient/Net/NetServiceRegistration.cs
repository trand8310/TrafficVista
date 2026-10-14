using Microsoft.Extensions.DependencyInjection;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Options;
using TrafficVista.Net.Vendors;

namespace TrafficVista.Net;

/// <summary>
/// Net 模块 DI 注册统一入口：选项、请求通道工厂、IpInfo 服务、Router、全部供应商适配器与 IProxyService。
/// 新增一家供应商时增加适配器并在此追加一行注册即可。
/// </summary>
public static class NetServiceRegistration
{
    public static IServiceCollection AddTrafficVistaNet(this IServiceCollection services, NetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return services.AddTrafficVistaNet(_ => options);
    }

    public static IServiceCollection AddTrafficVistaNet(this IServiceCollection services, Func<IServiceProvider, NetOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(optionsFactory);
        services.AddSingleton<ProxyTransportFactory>();
        services.AddSingleton<IProxyIpInfoProvider, ProxyIpInfoProvider>();
        // 供应商适配器；先通过配置（Net:Vendors 与 Net:DefaultVendor）选择供应商，界面接入时再使用下拉框。
        services.AddSingleton<IProxyVendorAdapter, IpzanProxyAdapter>();
        services.AddSingleton<IProxyVendorAdapter, ShenlongProxyAdapter>();
        services.AddSingleton<IProxyVendorAdapter, XingyuProxyAdapter>();
        services.AddSingleton<IProxyVendorAdapter, VendorBProxyAdapter>();
        services.AddSingleton<ProxyProviderRouter>();
        services.AddSingleton<IProxyService, ProxyService>();
        // 白名单处理：接口与实现放这里，容器可取，但任何业务流程都不自动调用，由调用方酌情使用。
        services.AddSingleton<IProxyWhitelistService, ProxyWhitelistService>();
        return services;
    }
}
