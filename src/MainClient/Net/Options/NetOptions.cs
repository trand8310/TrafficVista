namespace TrafficVista.Net.Options;

/// <summary>Net 模块总配置：供应商默认配置、IpInfo 接口与请求限制。节名 Net。</summary>
public sealed class NetOptions
{
    public const string SectionName = "Net";

    /// <summary>调用方未指定供应商时使用的默认供应商标识；用户文件可覆盖。</summary>
    public string DefaultVendor { get; set; } = "";

    /// <summary>各供应商配置，键为供应商标识。</summary>
    public Dictionary<string, ProxyVendorOptions> Vendors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IpInfo 查询接口配置。</summary>
    public IpInfoOptions IpInfo { get; set; } = new();

    /// <summary>TCP 连接建立超时（秒），所有请求通道共用。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>领取接口单次响应正文大小上限（字节）。</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>IpInfo 查询的总体并发上限（跨供应商共用）。</summary>
    public int IpInfoMaxConcurrency { get; set; } = 8;

    /// <summary>
    /// 代理获取失败后的重试次数（每次重试丢弃瑕疵代理、领取全新代理）；
    /// 触发条件：领取失败、经代理拿不到真实可用的 IP 信息、超时、代理已过期。0 表示不重试。
    /// </summary>
    public int AcquireRetryCount { get; set; } = 5;

    /// <summary>代理获取失败后、下一次重试前等待的毫秒数；0 表示立即重试。</summary>
    public int AcquireRetryIntervalMilliseconds { get; set; } = 1000;

    public void Validate()
    {
        if (ConnectTimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException($"Net:ConnectTimeoutSeconds 必须在 1～120 秒之间（当前 {ConnectTimeoutSeconds}）。");
        if (AcquireRetryCount is < 0 or > 10)
            throw new InvalidDataException($"Net:AcquireRetryCount 必须在 0～10 之间（当前 {AcquireRetryCount}）。");
        if (AcquireRetryIntervalMilliseconds is < 0 or > 600_000)
            throw new InvalidDataException($"Net:AcquireRetryIntervalMilliseconds 必须在 0～600000 毫秒之间（当前 {AcquireRetryIntervalMilliseconds}）。");
        if (MaxResponseBytes is < 1_024 or > 16_777_216)
            throw new InvalidDataException($"Net:MaxResponseBytes 必须在 1024～16777216 字节之间（当前 {MaxResponseBytes}）。");
        if (IpInfoMaxConcurrency is < 1 or > 64)
            throw new InvalidDataException($"Net:IpInfoMaxConcurrency 必须在 1～64 之间（当前 {IpInfoMaxConcurrency}）。");
        foreach (var (vendorId, vendor) in Vendors)
            vendor.Validate(vendorId);
        // IpInfo 接口在启用查询前可以全部留空；一旦填写必须合法（全空时查询阶段报明确错误）。
        if (IpInfo.EffectiveApiUrls().Count > 0)
            IpInfo.Validate();
    }

    /// <summary>该供应商是否配置为可用（存在、启用且 URL 合法）。</summary>
    public bool IsVendorUsable(string vendorId) =>
        Vendors.TryGetValue(vendorId, out var vendor) && vendor.Enabled && ProxyVendorOptions.IsHttpUrl(vendor.ApiUrl);
}
