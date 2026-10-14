using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net.Vendors;

/// <summary>
/// 占位供应商 B：JSON 应答协议，实际接入时换成对应供应商名称与真实字段规则。
/// 解析规则（占位）：code 非 0 视为失败；代理取 proxy 字段“ip:port”或 ip + port 两字段；
/// 可选 username/password/protocol；可选 expire（秒）作为有效期，从领取时刻开始计。
/// 各家解析代码留在各自类中，HTTP 请求、超时和大小限制共用 ProxyTransportFactory。
/// </summary>
public sealed class VendorBProxyAdapter : IProxyVendorAdapter
{
    private readonly NetOptions _options;
    private readonly ProxyTransportFactory _transports;

    public VendorBProxyAdapter(NetOptions options, ProxyTransportFactory transports)
    {
        _options = options;
        _transports = transports;
    }

    public string VendorId => "vendor-b";

    /// <summary>本供应商的请求规则入口；完整任务、配置及数量均由本次请求提供。</summary>
    internal static string BuildRequestUrl(string apiUrl, ProxyVendorOptions vendor, ProxyAcquireRequest request)
    {
        request.Validate();
        var url = ApplyRegions(apiUrl, request);
        url = ApplyQuantity(url, request);
        return ApplyRealIpOption(url, vendor, request);
    }

    private static string ApplyRegions(string apiUrl, ProxyAcquireRequest request)
    {
        // TODO: 在本适配器内按供应商协议解析 request.Task.Payload 中的地区。
        // 保留多地区，例如 北京[0],上海[2],湖南[23]:长沙[267]；任务编号不能直接当供应商地区码。
        return apiUrl;
    }

    private static string ApplyQuantity(string apiUrl, ProxyAcquireRequest request)
    {
        // TODO: 按本供应商规则，用 request.Count（默认1）替换 URL 中的数量字段。
        // 数量字段修改与批量响应解析尚未启用。
        return apiUrl;
    }

    private static string ApplyRealIpOption(string apiUrl, ProxyVendorOptions vendor, ProxyAcquireRequest request)
    {
        // 本供应商确认了参数名和取值才修改 URL；不同供应商可在各自适配器中使用不同规则。
        if (string.IsNullOrWhiteSpace(vendor.RealIpParameterName)) return apiUrl;
        var uri = new UriBuilder(apiUrl);
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.Equals(Uri.UnescapeDataString(part.Split('=')[0].Replace("+", " ")),
                vendor.RealIpParameterName, StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add(Uri.EscapeDataString(vendor.RealIpParameterName) + "=" + Uri.EscapeDataString(
            request.ResolveRealIp ? vendor.RealIpParameterOnValue : vendor.RealIpParameterOffValue));
        uri.Query = string.Join("&", parts);
        return uri.Uri.AbsoluteUri;
    }


    public Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, CancellationToken cancellationToken)
        => AcquireProxyAsync(apiUrl, new ProxyAcquireRequest(), cancellationToken);

    public async Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, ProxyAcquireRequest request, CancellationToken cancellationToken)
    {
        var vendor = _options.Vendors[VendorId];
        apiUrl = BuildRequestUrl(apiUrl, vendor, request);
        var text = await _transports.GetLimitedAsStringAsync(
            apiUrl, null, TimeSpan.FromSeconds(vendor.RequestTimeoutSeconds),
            _options.MaxResponseBytes, $"供应商 {VendorId} 领取", cancellationToken).ConfigureAwait(false);

        JObject root;
        try
        {
            root = JObject.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ProxyProtocolException($"供应商 {VendorId} 返回的正文不是有效 JSON。", ex);
        }

        var code = root["code"];
        if (code is { Type: not JTokenType.Null } && code.ToString() != "0")
            throw new ProxyProtocolException(
                $"供应商 {VendorId} 接口返回失败：code={code.ToString()}，msg={root["msg"]?.ToString() ?? "无"}。");

        var host = "";
        var port = 0;
        if (root["proxy"] is { Type: JTokenType.String } proxy && proxy.Value<string>() is { } proxyText)
        {
            if (!HostPort.TryParse(proxyText.Trim(), out host, out port, out _, out _))
                throw new ProxyProtocolException($"供应商 {VendorId} 返回的 proxy 字段不是合法 ip:port：{proxyText}。");
        }
        else if (root["ip"] is { Type: JTokenType.String } ip && ip.Value<string>() is { } ipText
                 && root["port"] is { } portToken && int.TryParse(portToken.ToString(), out port))
        {
            host = ipText.Trim();
            if (host.Length == 0 || host.Any(char.IsWhiteSpace) || port is < 1 or > 65535)
                throw new ProxyProtocolException($"供应商 {VendorId} 返回的 ip/port 不合法：{ipText}:{portToken}。");
        }
        else
        {
            throw new ProxyProtocolException($"供应商 {VendorId} 返回内容中缺少 proxy（或 ip+port）字段。");
        }

        var credentialUser = root["username"]?.Value<string>();
        var credentialPassword = root["password"]?.Value<string>();
        var expireSeconds = root["expire"] is { Type: not JTokenType.Null } expireToken
                            && int.TryParse(expireToken.ToString(), out var expire) ? expire : (int?)null;
        var ttlSeconds = expireSeconds ?? (vendor.DefaultTtlSeconds > 0 ? vendor.DefaultTtlSeconds : (int?)null);

        return new ProxyEndpoint
        {
            VendorId = VendorId,
            Host = host,
            Port = port,
            Protocol = root["protocol"]?.Value<string>() is { Length: > 0 } protocol ? protocol : "http",
            RealIp = string.IsNullOrWhiteSpace(vendor.RealIpJsonPath) ? null
                : root.SelectToken(vendor.RealIpJsonPath) is { Type: JTokenType.String } realIp
                    ? IpInfoValidation.Parse(realIp.Value<string>()) : null,
            Username = string.IsNullOrEmpty(credentialUser) ? null : credentialUser,
            Password = string.IsNullOrEmpty(credentialPassword) ? null : credentialPassword,
            AcquiredAt = DateTimeOffset.UtcNow,
            Ttl = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) : null
        };
    }
}
