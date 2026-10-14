namespace TrafficVista.Net.Options;

/// <summary>
/// IpInfo 查询配置：可配置多个接口，每次查询同时请求全部生效接口，谁先返回有效 JSON 谁胜出，其余取消。
/// </summary>
public sealed class IpInfoOptions
{
    /// <summary>单个接口 URL（兼容旧键）；与 ApiUrls 一起参与竞速。</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>接口 URL 列表；全部生效接口并行请求，最快返回有效 JSON 的接口作为结果。</summary>
    public List<string> ApiUrls { get; set; } = new();

    /// <summary>单个接口的请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>响应正文大小上限（字节）。</summary>
    public int MaxResponseBytes { get; set; } = 262_144;

    /// <summary>全部生效接口 URL（ApiUrl 与 ApiUrls 合并去重，仅保留合法 http/https）。</summary>
    public IReadOnlyList<string> EffectiveApiUrls()
    {
        var urls = new List<string>();
        foreach (var raw in ApiUrls.Append(ApiUrl))
        {
            var url = (raw ?? "").Trim();
            if (ProxyVendorOptions.IsHttpUrl(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }
        return urls;
    }

    public void Validate()
    {
        if (EffectiveApiUrls().Count == 0)
            throw new InvalidDataException("Net:IpInfo 的 ApiUrl / ApiUrls 至少要有一个绝对 http/https URL。");
        if (RequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidDataException($"Net:IpInfo:RequestTimeoutSeconds 必须在 1～300 秒之间（当前 {RequestTimeoutSeconds}）。");
        if (MaxResponseBytes is < 1_024 or > 16_777_216)
            throw new InvalidDataException($"Net:IpInfo:MaxResponseBytes 必须在 1024～16777216 字节之间（当前 {MaxResponseBytes}）。");
    }
}
