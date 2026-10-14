using System.Net;
using Newtonsoft.Json.Linq;

namespace TrafficVista.Net;

internal static class IpInfoValidation
{
    internal static bool HasRealIp(JObject info) => GetRealIp(info) is not null;
    internal static string? GetRealIp(JObject? info) => new[] { "ip", "query", "origin" }
        .Select(field => Parse(info?[field]?.Type == JTokenType.String ? info[field]!.Value<string>() : null))
        .FirstOrDefault(ip => ip is not null);
    internal static string? Parse(string? value) => IPAddress.TryParse(value?.Trim(), out var address) ? address.ToString() : null;
}
