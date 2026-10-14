using System.Net;

namespace TrafficVista.Net.Models;

/// <summary>
/// 统一后的代理连接信息：地址、协议、可选认证与有效期。
/// 有效期从领取时刻（AcquiredAt）开始计算；IpInfo 查询耗时计入有效期。
/// 认证信息不参与 ToString，避免写入日志。
/// </summary>
public sealed record ProxyEndpoint
{
    /// <summary>领取该代理的供应商标识。</summary>
    public required string VendorId { get; init; }

    /// <summary>代理主机（IPv4 或域名）。</summary>
    public required string Host { get; init; }

    /// <summary>代理端口。</summary>
    public required int Port { get; init; }

    /// <summary>代理协议，默认 http。</summary>
    public string Protocol { get; init; } = "http";
    /// <summary>Explicit supplier-reported exit IP; Host is not treated as the exit IP implicitly.</summary>
    public string? RealIp { get; init; }

    /// <summary>可选认证用户名。</summary>
    public string? Username { get; init; }

    /// <summary>可选认证密码；不写入日志。</summary>
    public string? Password { get; init; }

    /// <summary>领取时刻；有效期从此刻开始计算。</summary>
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>有效期；null 表示接口未提供且配置未兜底，视为不限时。</summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>过期时刻 = 领取时刻 + 有效期；null 表示不限时。</summary>
    public DateTimeOffset? ExpiresAt => Ttl is null ? null : AcquiredAt + Ttl.Value;

    /// <summary>当前是否已过有效期。</summary>
    public bool IsExpired => ExpiresAt is { } expires && DateTimeOffset.UtcNow >= expires;

    /// <summary>不含认证信息的通道地址，可安全写日志。</summary>
    public string SafeAddress => $"{Protocol}://{Host}:{Port}";

    /// <summary>基于本代理创建独立的 HTTP 通道 Handler 配置（本地地址不绕过代理）。</summary>
    public WebProxy CreateWebProxy()
    {
        var proxy = new WebProxy(SafeAddress) { BypassProxyOnLocal = false };
        if (!string.IsNullOrEmpty(Username))
            proxy.Credentials = new NetworkCredential(Username, Password);
        return proxy;
    }

    // 记录默认 ToString 会输出全部字段；这里屏蔽密码。
    public override string ToString() => string.IsNullOrEmpty(Username)
        ? $"{SafeAddress}，Vendor={VendorId}，过期={ExpiresAt?.ToString("O") ?? "不限时"}"
        : $"{SafeAddress}（用户 {Username}），Vendor={VendorId}，过期={ExpiresAt?.ToString("O") ?? "不限时"}";
}
