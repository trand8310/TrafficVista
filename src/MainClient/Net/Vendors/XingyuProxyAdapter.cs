using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net.Vendors;

/// <summary>
/// xingyu 独立适配器。支持 TXT ip:port；地区、数量和 JSON 协议待供应商规则确认后在本类补齐。
/// </summary>
public sealed class XingyuProxyAdapter : IProxyVendorAdapter
{
    private readonly NetOptions _options;
    private readonly ProxyTransportFactory _transports;

    public XingyuProxyAdapter(NetOptions options, ProxyTransportFactory transports)
    {
        _options = options;
        _transports = transports;
    }

    public string VendorId => "xingyu";

    internal static string BuildRequestUrl(string apiUrl, ProxyVendorOptions vendor, ProxyAcquireRequest request)
    {
        request.Validate();
        var url = ApplyRegions(apiUrl, request);
        url = ApplyQuantity(url, request);
        return ApplyRealIpOption(url, vendor, request);
    }

    private static string ApplyRegions(string apiUrl, ProxyAcquireRequest request)
    {
        // TODO: 读取 request.Task.Payload["address"]，按 xingyu 的地区编码及参数格式处理。
        // 地区样例：北京[0],上海[2],湖南[23]:长沙[267]；不能直接使用 ipzan 的地区码。
        return apiUrl;
    }

    private static string ApplyQuantity(string apiUrl, ProxyAcquireRequest request)
    {
        // TODO: 按 xingyu 协议，用 request.Count（默认 1）覆盖其数量参数。
        // 当前保留原 URL；领取结果仍只消费一个代理，不分发批量返回的剩余代理。
        return apiUrl;
    }

    private static string ApplyRealIpOption(string apiUrl, ProxyVendorOptions vendor, ProxyAcquireRequest request)
    {
        // 仅在配置了已确认的真实 IP 参数名时修改，不能套用 ipzan 的 format/realIp。
        if (string.IsNullOrWhiteSpace(vendor.RealIpParameterName)) return apiUrl;
        var uri = new UriBuilder(apiUrl);
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=')[0].Replace("+", " "))
                .Equals(vendor.RealIpParameterName, StringComparison.OrdinalIgnoreCase)).ToList();
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
        var text = await _transports.GetLimitedAsStringAsync(apiUrl, null,
            TimeSpan.FromSeconds(vendor.RequestTimeoutSeconds), _options.MaxResponseBytes,
            "供应商 xingyu 领取", cancellationToken).ConfigureAwait(false);
        return ParseResponse(text, vendor);
    }

    internal static ProxyEndpoint ParseResponse(string text, ProxyVendorOptions vendor, DateTimeOffset? acquiredAt = null)
    {
        var body = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (body.StartsWith('{') || body.StartsWith('['))
        {
            // TODO: 在本类校验 xingyu 业务状态、代理地址、有效期、真实出口 IP。
            // real_ip 缺失时保持 null；详情或真实 IP 补查交给 ProxyService，避免重复查询。
            throw new ProxyProtocolException("xingyu 的 JSON 响应协议尚未配置，请补齐本适配器的解析规则或使用 TXT 提取接口。");
        }
        foreach (var raw in body.Split('\n'))
        {
            if (!HostPort.TryParse(raw.Trim(), out var host, out var port, out var username, out var password)
                || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown) continue;
            return new ProxyEndpoint
            {
                VendorId = "xingyu", Host = host, Port = port, Username = username, Password = password,
                AcquiredAt = acquiredAt ?? DateTimeOffset.UtcNow,
                Ttl = vendor.DefaultTtlSeconds > 0 ? TimeSpan.FromSeconds(vendor.DefaultTtlSeconds) : null,
                RealIp = null
            };
        }
        // 不把供应商原始响应或凭证写入异常。
        throw new ProxyProtocolException("供应商 xingyu 返回内容中没有有效的 ip:port 行。");
    }
}

