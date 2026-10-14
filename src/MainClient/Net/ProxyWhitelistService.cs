using System.Diagnostics;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// 白名单处理实现：直连配置的 IpInfo 接口（多接口竞速，谁快用谁的）取出口 IP，
/// 再调用供应商白名单接口（供应商 Parameters 的 WhitelistApiUrl，{ip} 占位符替换为出口 IP）。
/// 只登记，不被任何流程自动调用；失败只记日志并返回 false。
/// </summary>
public sealed class ProxyWhitelistService : IProxyWhitelistService
{
    private readonly NetOptions _options;
    private readonly ProxyTransportFactory _transports;
    private readonly ILogger _logger;

    public ProxyWhitelistService(NetOptions options, ProxyTransportFactory transports, ILogger logger)
    {
        _options = options;
        _transports = transports;
        _logger = logger.ForContext<ProxyWhitelistService>();
    }

    public async Task<string?> QueryCurrentEgressIpAsync(CancellationToken cancellationToken = default)
    {
        var urls = _options.IpInfo.EffectiveApiUrls();
        if (urls.Count == 0)
        {
            _logger.Warning("查询出口 IP 失败：Net:IpInfo 的 ApiUrl / ApiUrls 未配置。");
            return null;
        }

        using var race = new CancellationTokenSource();
        var pending = urls.Select(url => EgressOneAsync(url, race.Token, cancellationToken)).ToList();
        string? ip = null;
        string? source = null;
        while (pending.Count > 0 && ip is null)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            var outcome = await done.ConfigureAwait(false);
            if (outcome.Ip is not null) { ip = outcome.Ip; source = outcome.Url; }
        }
        race.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (ip is null)
        {
            _logger.Warning("查询出口 IP 失败：{Count} 个 IpInfo 接口均未返回有效 IP。", urls.Count);
            return null;
        }
        _logger.Information("出口 IP 查询完成，Source={Source}，Ip={Ip}", source, ip);
        return ip;
    }

    private sealed record EgressOutcome(string Url, string? Ip, Exception? Error);

    private async Task<EgressOutcome> EgressOneAsync(string url, CancellationToken race, CancellationToken caller)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(race, caller);
            var text = await _transports.GetLimitedAsStringAsync(
                url, null, TimeSpan.FromSeconds(_options.IpInfo.RequestTimeoutSeconds),
                _options.IpInfo.MaxResponseBytes, "出口 IP", timeout.Token).ConfigureAwait(false);

            // ===== 提取规则区（出口 IP 字段：先 ip 再 query，按各家返回微调，只改这里）=====
            var candidate = ReadIpField(text, "ip") ?? ReadIpField(text, "query");
            // ===== 提取规则区结束 =====
            if (candidate is null)
                return new EgressOutcome(url, null, new ProxyProtocolException("接口返回中未找到有效 IP 字段。"));
            return new EgressOutcome(url, candidate, null);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return new EgressOutcome(url, null, null); // 竞速已结束的普通落败
        }
        catch (Exception ex)
        {
            return new EgressOutcome(url, null, ex);
        }
    }

    private static string? ReadIpField(string text, string field)
    {
        try
        {
            var value = JObject.Parse(text)[field]?.Value<string>();
            return !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value.Trim(), out _) ? value.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> RefreshWhitelistAsync(string vendorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);
        if (!_options.Vendors.TryGetValue(vendorId, out var vendor))
        {
            _logger.Warning("白名单刷新跳过：未配置供应商 {Vendor}。", vendorId);
            return false;
        }
        if (!vendor.Parameters.TryGetValue("WhitelistApiUrl", out var whitelistUrl) ||
            !ProxyVendorOptions.IsHttpUrl(whitelistUrl))
        {
            _logger.Information(
                "白名单刷新跳过：供应商 {Vendor} 未配置 Parameters:WhitelistApiUrl（URL 中用 {ip} 占位出口 IP）。", vendorId);
            return false;
        }

        var ip = await QueryCurrentEgressIpAsync(cancellationToken).ConfigureAwait(false);
        if (ip is null) return false;

        var started = Stopwatch.GetTimestamp();
        try
        {
            var url = whitelistUrl.Replace("{ip}", ip, StringComparison.OrdinalIgnoreCase);
            await _transports.GetLimitedAsStringAsync(
                url, null, TimeSpan.FromSeconds(vendor.RequestTimeoutSeconds),
                _options.MaxResponseBytes, "白名单", cancellationToken).ConfigureAwait(false);

            // ===== 提取规则区（白名单接口成功判定：当前 HTTP 2xx 即成功；若按返回 JSON 的 code 判定，改这里）=====
            _logger.Information("白名单刷新成功，Vendor={Vendor}，Ip={Ip}，Elapsed={Elapsed}ms",
                vendorId, ip, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return true;
            // ===== 提取规则区结束 =====
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warning("白名单刷新超时，Vendor={Vendor}，Ip={Ip}，Elapsed={Elapsed}ms",
                vendorId, ip, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning("白名单刷新失败，Vendor={Vendor}，Ip={Ip}，Elapsed={Elapsed}ms，原因：{Reason}",
                vendorId, ip, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Scrub(ex.Message, vendor));
            return false;
        }
    }

    /// <summary>URL 与参数值（可能含 secret/密钥）绝不出现在日志文本里。</summary>
    private static string Scrub(string message, ProxyVendorOptions vendor)
    {
        foreach (var secret in vendor.SecretValues())
            message = message.Replace(secret, "***", StringComparison.Ordinal);
        foreach (var value in vendor.Parameters.Values
                     .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 4))
            message = message.Replace(value, "***", StringComparison.Ordinal);
        return message;
    }
}
