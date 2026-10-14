using System.Net;

namespace TrafficVista.LocalTesting;

/// <summary>本地测试入口不可切换为外部目标。</summary>
public static class LocalEndpointPolicy
{
    public static bool IsLoopback(string value, bool allowWebSocket = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https") && !(allowWebSocket && uri.Scheme is "ws" or "wss")) return false;
        var host = uri.DnsSafeHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address)
            && (IPAddress.IsLoopback(address) || address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
    }

    public static Uri RequireLoopback(string value, string name)
    {
        if (!IsLoopback(value)) throw new InvalidOperationException($"{name} 必须是本机 localhost/127.0.0.1/::1 的 HTTP(S) 地址。");
        return new Uri(value);
    }
}
