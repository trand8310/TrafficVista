namespace TrafficVista.Net.Options;

/// <summary>单个代理供应商的配置；供应商标识即配置键，与 URL 分开，换 URL 或账号不改路由逻辑。</summary>
public sealed class ProxyVendorOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>领取接口 URL（绝对 http/https）；认证参数可拼在 URL 或放在 Parameters。</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>供应商私有的账号、密钥等扩展参数；具体含义由各适配器解释，值不写入日志。</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Confirmed supplier query field for returning exit IP; empty means no URL change.</summary>
    public string RealIpParameterName { get; set; } = "";
    public string RealIpParameterOnValue { get; set; } = "1";
    public string RealIpParameterOffValue { get; set; } = "0";
    /// <summary>Confirmed exit-IP JSON path, separate from proxy host. Used by JSON adapters.</summary>
    public string RealIpJsonPath { get; set; } = "";

    /// <summary>该供应商独立的请求并发上限。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>领取请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>接口未返回有效期时的兜底 TTL（秒）；0 表示不限时。</summary>
    public int DefaultTtlSeconds { get; set; } = 300;

    public void Validate(string vendorId)
    {
        if (string.IsNullOrWhiteSpace(vendorId))
            throw new InvalidDataException("Net:Vendors 的配置键（供应商标识）不能为空。");
        if (!Enabled) return;
        if (!IsHttpUrl(ApiUrl))
            throw new InvalidDataException($"Net:Vendors:{vendorId}:ApiUrl 必须是绝对 http/https URL。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        if (MaxConcurrency > 64)
            throw new InvalidDataException($"Net:Vendors:{vendorId}:MaxConcurrency 不能超过 64（当前 {MaxConcurrency}）。");
        if (RequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidDataException($"Net:Vendors:{vendorId}:RequestTimeoutSeconds 必须在 1～300 秒之间（当前 {RequestTimeoutSeconds}）。");
        if (DefaultTtlSeconds is < 0 or > 86_400)
            throw new InvalidDataException($"Net:Vendors:{vendorId}:DefaultTtlSeconds 必须在 0～86400 秒之间（当前 {DefaultTtlSeconds}）。");
    }

    internal static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static readonly string[] SecretParameterNames =
        { "secret", "password", "passwd", "key", "token", "appkey", "appsecret" };

    /// <summary>
    /// ApiUrl 查询串中凭证参数的值（如 ipzan 的 secret=），供日志与异常文本脱敏，永不输出这些值本身。
    /// </summary>
    public IReadOnlyList<string> SecretValues()
    {
        var values = new List<string>();
        foreach (var parameter in Parameters)
            if (SecretParameterNames.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parameter.Value)) values.Add(parameter.Value);
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var uri)) return values;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!SecretParameterNames.Contains(Uri.UnescapeDataString(pair[..eq]), StringComparer.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace("+", " "));
            if (value.Length > 0 && !values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        return values;
    }
}
