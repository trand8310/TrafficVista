using System.Diagnostics;
using System.Text;
using Serilog;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// 请求通道工厂：不同代理使用各自独立的 Handler，绝不修改共享 Handler 的代理设置。
/// 各供应商适配器与 IpInfo 查询共用这里的 HTTP 请求、超时与响应大小限制。
/// </summary>
public sealed class ProxyTransportFactory : IDisposable
{
    private readonly NetOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _direct;

    public ProxyTransportFactory(NetOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _direct = CreateClient(null, Timeout.InfiniteTimeSpan);
    }

    /// <summary>创建一次性 HttpClient；proxy 为 null 表示直连（如供应商领取接口）。</summary>
    public HttpClient CreateClient(ProxyEndpoint? proxy, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
            UseProxy = proxy is not null,
            Proxy = proxy?.CreateWebProxy(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>
    /// GET 并读取有限大小的 UTF-8 正文；校验状态码与大小上限。
    /// 超时抛 ProxyRequestTimeoutException，调用方取消则原样抛出 OperationCanceledException。
    /// </summary>
    public async Task<string> GetLimitedAsStringAsync(
        string url, ProxyEndpoint? viaProxy, TimeSpan timeout, int maxBytes, string what, CancellationToken cancellationToken,
        HttpClient? channel = null)
    {
        var started = Stopwatch.GetTimestamp();
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        var requestToken = requestTimeout.Token;
        try
        {
            using var ownedClient = channel is null && viaProxy is not null ? CreateClient(viaProxy, Timeout.InfiniteTimeSpan) : null;
            var client = channel ?? ownedClient ?? _direct;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProxyProtocolException($"{what}接口返回 HTTP {(int)response.StatusCode}。");
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                throw new ProxyProtocolException($"{what}接口响应声明 {declared} 字节，超过上限 {maxBytes} 字节。");
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[65_536];
            int read;
            while ((read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new ProxyProtocolException($"{what}接口响应超过 {maxBytes} 字节上限。");
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("{What}请求被取消，耗时 {Elapsed}ms", what, ElapsedMs(started));
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时也表现为 OperationCanceledException，但令牌未取消。
            throw new ProxyRequestTimeoutException($"{what}接口请求超时（{timeout.TotalSeconds:0.##} 秒）。");
        }
    }

    private static long ElapsedMs(long started) => (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    public void Dispose() => _direct.Dispose();
}
