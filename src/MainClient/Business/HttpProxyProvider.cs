using System.Net;
using System.Text;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;

namespace TrafficVista.Business;

/// <summary>
/// 获取任务代理 IP（ProxyIpUrl）。代理属于单个任务：每个任务在消费开始时无条件重新调用一次接口，
/// 本任务全部 UV 共享结果，不与其他任务共享，也不做任何跨任务缓存。
/// 协议：纯文本，取第一行 ip:port（可带 http:// 前缀）。
/// IpTtl 不是这里的缓存窗口，而是任务侧对该 IP 的使用期限（由消费者在全部 UV 执行期间计时）。
/// </summary>
public sealed class HttpProxyProvider(HttpClient client, ILogger logger) : IBusinessProxyProvider, IDisposable
{
    private readonly SemaphoreSlim _fetch = new(1, 1);
    private const int MaxBytes = 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public async ValueTask<BusinessProxy?> AcquireAsync(TaskEnvelope task, TrafficVistaOptions options, CancellationToken token, int count = 1)
    {
        token.ThrowIfCancellationRequested();
        if (!options.IsProxyMode) return null;
        if (!Uri.TryCreate(options.ProxyIpUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("代理模式请先配置完整的代理接口 HTTP/HTTPS 地址。");
        // 串行化对代理接口的调用，避免多任务同时启动时瞬时并发打爆接口；每个任务仍然各拿各的 IP。
        await _fetch.WaitAsync(token).ConfigureAwait(false);
        try { return new BusinessProxy(await FetchAsync(uri, token).ConfigureAwait(false)); }
        finally { _fetch.Release(); }
    }

    private async Task<string> FetchAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(FetchTimeout);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await BusinessHttp.ReadCappedAsync(response, MaxBytes, "代理接口", timeout.Token).ConfigureAwait(false);
        var server = ParseProxy(Encoding.UTF8.GetString(bytes));
        logger.Debug("代理 IP 获取完成，Server={Server}", server);
        return server;
    }

    public static string ParseProxy(string text)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            var scheme = line.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) line = line[(scheme + 3)..];
            var path = line.IndexOf('/');
            if (path >= 0) line = line[..path];
            var colon = line.LastIndexOf(':');
            if (colon <= 0) continue;
            var host = line[..colon].Trim().Trim('[', ']');
            if (!IPAddress.TryParse(host, out _)
                || !int.TryParse(line[(colon + 1)..], out var port) || port is < 1 or > 65535) continue;
            return $"{host}:{port}";
        }
        throw new InvalidDataException("代理接口没有返回有效的 ip:port。");
    }

    public void Dispose() => _fetch.Dispose();
}
