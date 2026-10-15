using System.Text.RegularExpressions;

namespace TrafficVista.Business;

/// <summary>Only emitted for browser main-document proxy transport failures.</summary>
internal sealed class ProxyUnavailableException(string code, Exception cause)
    : InvalidOperationException($"当前代理通道不可用：{code}。", cause)
{
    internal string Code { get; } = code;
    internal static readonly string[] BrowserCodes =
    [
        "ERR_PROXY_CONNECTION_FAILED", "ERR_TUNNEL_CONNECTION_FAILED",
        "ERR_PROXY_AUTH_UNSUPPORTED", "ERR_PROXY_AUTH_REQUESTED",
        "ERR_PROXY_AUTH_REQUESTED_WITH_NO_CONNECTION", "ERR_PROXY_CERTIFICATE_INVALID",
        "ERR_SOCKS_CONNECTION_FAILED", "ERR_SOCKS_CONNECTION_HOST_UNREACHABLE",
        "ERR_PROXY_UNABLE_TO_CONNECT_TO_DESTINATION", "ERR_NO_SUPPORTED_PROXIES",
        "ERR_PROXY_HTTP_1_1_REQUIRED"
    ];

    internal static string? BrowserCode(string? failure)
    {
        var code = Regex.Match(failure ?? "", @"\bERR_[A-Z0-9_]+\b").Value;
        return BrowserCodes.Contains(code, StringComparer.Ordinal) ? code : null;
    }

    internal static string? FindCode(Exception? error)
    {
        if (error is ProxyUnavailableException proxy) return proxy.Code;
        if (error is PageHttpStatusException { Status: 407 }) return "HTTP407";
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(FindCode).FirstOrDefault(code => code is not null);
        return error?.InnerException is { } inner ? FindCode(inner) : null;
    }
}
