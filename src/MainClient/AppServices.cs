using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Serilog;
using TrafficVista.Scheduling;
using TrafficVista.Configuration;
using Microsoft.Extensions.Options;
using TrafficVista.Business;
using TrafficVista.Net;
using TrafficVista.Scheduling.Consumers;

namespace TrafficVista;

internal static class AppServices
{
    public static ServiceProvider Build(ILogger logger, string? userRoot = null)
    {
        var services = new ServiceCollection();
        // 全局 Logger 由 Program 最后 CloseAndFlush，容器不拥有这个外部实例。
        services.AddSingleton(logger);
        // 同时支持业务服务注入 Microsoft.Extensions.Logging.ILogger<T>。
        services.AddLogging(builder => builder.AddSerilog(logger, dispose: false));
        services.AddOptions();
        services.AddSingleton(_ => new TrafficVistaSettings(AppContext.BaseDirectory));
        services.AddSingleton<IOptions<TrafficVistaOptions>>(provider => provider.GetRequiredService<TrafficVistaSettings>());
        // 在 Main 的 UI 消息循环启动前解析；只有这里创建并由容器释放 IPlaywright。
        services.AddSingleton<IPlaywright>(_ => Playwright.CreateAsync().GetAwaiter().GetResult());
        services.AddSingleton(provider => new BrowserLaunchLimiter(
            provider.GetRequiredService<TrafficVistaSettings>().MaxConcurrentBrowserStarts,
            provider.GetRequiredService<TrafficVistaSettings>().BrowserStartIntervalMilliseconds));
        services.AddSingleton(provider => new ChromeInstanceManager(
            provider.GetRequiredService<IPlaywright>(), provider.GetRequiredService<BrowserLaunchLimiter>(),
            logger, userRoot));
        services.AddTransient<MainForm>();
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<Scheduling.Producers.HttpTaskSource>();
        services.AddSingleton<Contracts.ITaskSource>(provider => provider.GetRequiredService<Scheduling.Producers.HttpTaskSource>());
        services.AddSingleton<TaskSchedulingService>();
        // Net 模块：多供应商代理领取 + IpInfo 查询统一入口；配置来自 TrafficVistaSettings 的 Net 节（含用户覆盖）。
        services.AddTrafficVistaNet(provider => provider.GetRequiredService<TrafficVistaSettings>().Net);
        // 业务浏览器消费者链路；本地测试消费者（LocalUv*）代码保留但不再接线。
        services.AddSingleton(provider => provider.GetRequiredService<TrafficVistaSettings>().Business);
        services.AddSingleton<IBusinessDeviceProvider, HttpDeviceFingerprintProvider>();
        // 业务代理已切换到 Net 模块（供应道路由 + 瑕疵重试 + 真实出口 IP 校验）；旧 HttpProxyProvider 类保留但不再接线。
        services.AddSingleton<IBusinessProxyProvider, NetProxyProvider>();
        services.AddSingleton<SharedStaticResourceCache>();
        services.AddSingleton<BusinessMetrics>();
        services.AddSingleton<IBusinessUvRunner, BusinessUvBrowserRunner>();
        services.AddSingleton<BrowserTaskConsumer>();
        services.AddSingleton<ITaskConsumer<Contracts.TaskEnvelope>>(provider =>
            provider.GetRequiredService<BrowserTaskConsumer>());
        services.AddSingleton<ITaskSchedulerService>(provider => provider.GetRequiredService<TaskSchedulingService>());
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
