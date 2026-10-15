using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net.Vendors;

/// <summary>ipzan 请求及响应规则：地区映射、num、format/realIp、TXT 和 data.list JSON。</summary>
public sealed class IpzanProxyAdapter : IProxyVendorAdapter
{
    private readonly NetOptions _options;
    private readonly ProxyTransportFactory _transports;
    private static readonly Lazy<Region[]> Regions = new(LoadRegions);
    internal sealed record Region(string Name, string Code);

    public IpzanProxyAdapter(NetOptions options, ProxyTransportFactory transports)
    {
        _options = options;
        _transports = transports;
    }

    public string VendorId => "ipzan";

    internal static string BuildRequestUrl(string apiUrl, ProxyVendorOptions vendor, ProxyAcquireRequest request,
        IReadOnlyList<Region>? regionMap = null)
    {
        request.Validate();
        var url = ApplyRegions(apiUrl, request, regionMap);
        url = SetParameter(url, "num", request.Count.ToString(CultureInfo.InvariantCulture));
        url = SetParameter(url, "format", request.ResolveRealIp ? "json" : "txt");
        // Explicitly overwrite old realIp values when the UI option is disabled.
        return SetParameter(url, "realIp", request.ResolveRealIp ? "1" : "0");
    }

    private static string ApplyRegions(string apiUrl, ProxyAcquireRequest request, IReadOnlyList<Region>? regionMap)
    {
        var address = request.Task?.Payload?["address"];
        if (address is null || address.Type == JTokenType.Null) return apiUrl;
        var entries = address switch
        {
            JValue { Type: JTokenType.String } value => new[] { value.Value<string>() ?? "" },
            JArray array when array.All(value => value.Type == JTokenType.String) => array.Values<string>().Select(value => value ?? "").ToArray(),
            _ => throw new ProxyProtocolException("ipzan 任务 address 必须是地区字符串或字符串数组。")
        };
        var requested = entries.SelectMany(value => value.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (requested.Length == 0) return apiUrl;
        if (requested.Any(value => value.Equals("全部", StringComparison.Ordinal) || value.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return apiUrl;// SetParameter(apiUrl, "area", null);
        var map = regionMap ?? Regions.Value;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in requested)
        {
            var parts = item.Replace('：', ':').Split(':');
            if (parts.Length is < 1 or > 2) continue;
            var province = RegionName(parts[0]);
            if (province.Length == 0) continue;
            var provinces = map.Where(region => Matches(region.Name, province)).ToArray();
            var candidates = provinces.AsEnumerable();
            var target = province;
            if (parts.Length == 2)
            {
                target = RegionName(parts[1]);
                if (target.Length == 0) continue;
                // Administrative region codes share their first two digits with the province.
                var prefixes = provinces.Select(region => region.Code[..2]).Distinct().ToArray();
                candidates = map.Where(region => prefixes.Contains(region.Code[..2]) && Matches(region.Name, target));
            }
            var selected = candidates.OrderByDescending(region => RegionName(region.Name) == target)
                .ThenByDescending(region => region.Code.EndsWith("00", StringComparison.Ordinal))
                .ThenByDescending(region => region.Code, StringComparer.Ordinal).FirstOrDefault();
            if (selected is not null) codes.Add(selected.Code);
        }
        if (codes.Count == 0)
            throw new ProxyProtocolException("ipzan 无法映射任务指定地区；检查应用目录 data/net/ipzan/regions.json，未发送非定向请求。");
        return SetParameter(apiUrl, "area", codes.ElementAt(Random.Shared.Next(codes.Count)));
    }

    private static bool Matches(string name, string target) =>
        RegionName(name).Equals(target, StringComparison.Ordinal) || name.StartsWith(target, StringComparison.Ordinal);

    private static string RegionName(string value)
    {
        var name = value.Split('[')[0].Trim();
        foreach (var suffix in new[] { "特别行政区", "自治区", "自治州", "省", "市" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    private static Region[] LoadRegions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "net", "ipzan", "regions.json");
        try
        {
            if (!File.Exists(path)) throw new InvalidDataException("地区文件不存在。");
            var rows = JArray.Parse(File.ReadAllText(path, Encoding.UTF8));
            var regions = rows.OfType<JObject>().Select(row => new Region(row["name"]?.ToString().Trim() ?? "",
                row["code"]?.ToString().Trim() ?? "")).ToArray();
            if (regions.Length == 0 || regions.Length != rows.Count || regions.Any(region => region.Name.Length == 0
                || region.Code.Length != 6 || region.Code.Any(c => c is < '0' or > '9')))
                throw new InvalidDataException("地区列表需含非空name和六位code。");
            return regions;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new ProxyProtocolException("ipzan 地区映射文件加载失败：" + path, ex);
        }
    }

    // Change one exact query key, preserving all other encoded values and any URI fragment.
    private static string SetParameter(string apiUrl, string name, string? value)
    {
        var uri = new UriBuilder(apiUrl);
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=')[0].Replace("+", " ")).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (value is not null) parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        uri.Query = string.Join("&", parts);
        return uri.Uri.AbsoluteUri;
    }

    public Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, CancellationToken cancellationToken)
        => AcquireProxyAsync(apiUrl, new ProxyAcquireRequest(), cancellationToken);

    public async Task<ProxyEndpoint> AcquireProxyAsync(string apiUrl, ProxyAcquireRequest request, CancellationToken cancellationToken)
    {
        var vendor = _options.Vendors[VendorId];
        apiUrl = BuildRequestUrl(apiUrl, vendor, request);
        var text = await _transports.GetLimitedAsStringAsync(apiUrl, null, TimeSpan.FromSeconds(vendor.RequestTimeoutSeconds),
            _options.MaxResponseBytes, "供应商 ipzan 领取", cancellationToken).ConfigureAwait(false);
        return ParseResponse(text, vendor);
    }

    /// <summary>Recognize the actual body format; keep TXT compatibility even if the API returns TXT for a JSON request.</summary>
    internal static ProxyEndpoint ParseResponse(string text, ProxyVendorOptions vendor, DateTimeOffset? acquiredAt = null)
    {
        var acquired = acquiredAt ?? DateTimeOffset.UtcNow;
        var body = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (body.StartsWith('{') || body.StartsWith('['))
        {
            JObject root;
            try { root = JObject.Parse(body); }
            catch (JsonException ex) { throw new ProxyProtocolException("ipzan 返回的 JSON 无效。", ex); }
            if (root["code"]?.ToString() != "0" || (root["status"] is { Type: not JTokenType.Null } status && status.ToString() != "200"))
                throw new ProxyProtocolException("ipzan 接口返回业务失败；code=" + root["code"]?.ToString());
            if (root["data"] is not JObject data || data["list"] is not JArray list || list.Count == 0)
                throw new ProxyProtocolException("ipzan JSON 缺少非空 data.list。");
            foreach (var item in list.OfType<JObject>())
            {
                var host = item["ip"]?.Type == JTokenType.String ? IpInfoValidation.Parse(item["ip"]!.Value<string>()) : null;
                if (host is null || !int.TryParse(item["port"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    || port is < 1 or > 65535) continue;
                TimeSpan? ttl = vendor.DefaultTtlSeconds > 0 ? TimeSpan.FromSeconds(vendor.DefaultTtlSeconds) : null;
                if (item["expired"] is { Type: not JTokenType.Null } expired)
                {
                    if (!long.TryParse(expired.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)) continue;
                    try { ttl = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) - acquired; }
                    catch (ArgumentOutOfRangeException) { continue; }
                    if (ttl <= TimeSpan.Zero) continue;
                }
                return new ProxyEndpoint
                {
                    VendorId = "ipzan", Host = host, Port = port, AcquiredAt = acquired, Ttl = ttl,
                    RealIp = item["real_ip"]?.Type == JTokenType.String ? IpInfoValidation.Parse(item["real_ip"]!.Value<string>()) : null
                };
            }
            throw new ProxyProtocolException("ipzan data.list 中没有地址、端口和有效期均可用的代理。");
        }
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!HostPort.TryParse(line, out var host, out var port, out var username, out var password)) continue;
            return new ProxyEndpoint
            {
                VendorId = "ipzan", Host = host, Port = port, Username = username, Password = password,
                AcquiredAt = acquired, Ttl = vendor.DefaultTtlSeconds > 0 ? TimeSpan.FromSeconds(vendor.DefaultTtlSeconds) : null
            };
        }
        // Do not include vendor response bodies, which may contain credentials, in protocol errors.
        throw new ProxyProtocolException("供应商 ipzan 返回内容中没有有效的 ip:port 行。");
    }
}
/// <summary>ip:port / user:pass@ip:port 行解析，供文本类供应商适配器共用。</summary>
internal static class HostPort
{
    public static bool TryParse(string line, out string host, out int port, out string? username, out string? password)
    {
        host = "";
        port = 0;
        username = null;
        password = null;
        var hostPart = line;
        var at = line.LastIndexOf('@');
        if (at >= 0)
        {
            var credential = line[..at];
            hostPart = line[(at + 1)..];
            var credentialColon = credential.IndexOf(':');
            username = credentialColon < 0 ? credential : credential[..credentialColon];
            password = credentialColon < 0 ? null : credential[(credentialColon + 1)..];
            if (username.Length == 0) return false;
        }
        var colon = hostPart.LastIndexOf(':');
        if (colon <= 0 || colon == hostPart.Length - 1) return false;
        var candidateHost = hostPart[..colon];
        if (candidateHost.Length == 0 || candidateHost.Any(char.IsWhiteSpace)) return false;
        if (!int.TryParse(hostPart[(colon + 1)..], out var candidatePort) || candidatePort is < 1 or > 65535) return false;
        host = candidateHost;
        port = candidatePort;
        return true;
    }
}
