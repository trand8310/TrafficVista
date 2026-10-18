using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// IProxyService 实现：选择供应商 → Router 找适配器 → 用生效领取 URL（调用方传入优先，未传用配置默认）领取代理
/// （每供应商独立并发上限）→ 通过该代理查询 IpInfo（总体并发上限在 ProxyIpInfoProvider）→ 校验有效期与真实性后返回代理 + JObject。
/// 领取与按开关触发的查询分两阶段，分别记录错误；需要查询而未取得有效 IpInfo 即整体失败。
/// 代理可能有瑕疵（连不通、拿不到真实 IP 信息、超时、已过期）：整体按 Net:AcquireRetryCount 重试，
/// 每次重试丢弃瑕疵代理、领取全新代理；调用方取消不参与重试。
/// </summary>
public sealed class ProxyService : IProxyService, IDisposable
{
    private readonly NetOptions _options;
    private readonly ProxyProviderRouter _router;
    private readonly IProxyIpInfoProvider _ipInfo;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vendorGates = new(StringComparer.OrdinalIgnoreCase);
    // All workers and vendors share one acquisition lane. IP detail queries run after release.
    private readonly SemaphoreSlim _acquireGate = new(1, 1);

    public ProxyService(NetOptions options, ProxyProviderRouter router, IProxyIpInfoProvider ipInfo, ILogger logger)
    {
        _options = options;
        _router = router;
        _ipInfo = ipInfo;
        _logger = logger.ForContext<ProxyService>();
    }

    public IReadOnlyList<string> Vendors => _router.VendorIds;

    public Task<ProxyAcquireResult> AcquireAsync(string? vendorId = null, string? apiUrl = null,
        CancellationToken cancellationToken = default)
        => AcquireAsync(new ProxyAcquireRequest(), vendorId, apiUrl, cancellationToken);

    public async Task<ProxyAcquireResult> AcquireAsync(ProxyAcquireRequest request, string? vendorId = null, string? apiUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate(); request = request.Snapshot();
        var adapter = _router.Resolve(vendorId);
        var config = _router.GetConfigured(adapter.VendorId);
        // 领取接口 URL：调用方传入即对全部供应商统一生效（界面“代理接口”）；未传回退该供应商配置的默认值。
        var effectiveUrl = string.IsNullOrWhiteSpace(apiUrl) ? config.ApiUrl : apiUrl.Trim();
        if (!ProxyVendorOptions.IsHttpUrl(effectiveUrl))
            throw new ProxyServiceException(adapter.VendorId, "route",
                $"供应商“{adapter.VendorId}”的领取接口 URL 未配置或不合法（调用方传入与 Net:Vendors 配置均不可用）。");
        var totalAttempts = Math.Clamp(_options.AcquireRetryCount, 0, 10) + 1;
        var retryInterval = _options.AcquireRetryIntervalMilliseconds;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AcquireOnceAsync(adapter, config, effectiveUrl, request.Snapshot(), cancellationToken).ConfigureAwait(false);
            }
            catch (ProxyServiceException ex) when (attempt < totalAttempts)
            {
                // 瑕疵代理（或领取失败/超时/过期）：丢弃，换一个全新代理重试；取消走 OperationCanceledException，不重试。
                _logger.Warning("代理获取第 {Attempt}/{Total} 次尝试失败，Vendor={Vendor}，Phase={Phase}，等待 {RetryIntervalMs} 毫秒后丢弃并换代理重试：{Reason}",
                    attempt, totalAttempts, ex.VendorId, ex.Phase, retryInterval, ex.Message);
                await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProxyAcquireResult> AcquireOnceAsync(
        IProxyVendorAdapter adapter, ProxyVendorOptions config, string apiUrl, ProxyAcquireRequest request, CancellationToken cancellationToken)
    {
        var id = adapter.VendorId;
        var redaction = new ProxyVendorOptions { ApiUrl = apiUrl, Parameters = config.Parameters };
        var gate = _vendorGates.GetOrAdd(id, key =>
            new SemaphoreSlim(Math.Max(1, config.MaxConcurrency)));

        // 阶段一：领取代理。
        ProxyEndpoint endpoint;
        var acquireStarted = Stopwatch.GetTimestamp();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _acquireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                endpoint = await adapter.AcquireProxyAsync(apiUrl, request, cancellationToken).ConfigureAwait(false)
                    ?? throw new ProxyProtocolException($"供应商 {id} 适配器返回了空代理。");
            }
            finally { _acquireGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Information("代理领取被取消，Vendor={Vendor}，Phase=acquire。", id);
            throw;
        }
        catch (Exception ex)
        {
            var acquireMsFailed = (long)Stopwatch.GetElapsedTime(acquireStarted).TotalMilliseconds;
            // URL 中的 secret 等凭证绝不写入日志：异常文本包含凭证值时改为记录脱敏原因。
            var leaks = LeaksSecret(ex, redaction);
            if (leaks)
                _logger.Warning("代理领取失败，Vendor={Vendor}，Phase=acquire，Elapsed={Elapsed}ms，原因={Reason}",
                    id, acquireMsFailed, Scrub(ex.Message, redaction));
            else
                _logger.Warning(ex, "代理领取失败，Vendor={Vendor}，Phase=acquire，Elapsed={Elapsed}ms", id, acquireMsFailed);
            if (ex is ProxyServiceException)
                throw new ProxyServiceException(id, "acquire", Scrub(ex.Message, redaction), leaks ? null : ex);
            throw new ProxyServiceException(id, "acquire", $"供应商 {id} 领取代理失败：{Scrub(ex.Message, redaction)}", leaks ? null : ex);
        }
        finally
        {
            gate.Release();
        }
        var acquireMs = (long)Stopwatch.GetElapsedTime(acquireStarted).TotalMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.IsExpired)
        {
            _logger.Warning("领取到的代理已过期，Vendor={Vendor}，Proxy={Proxy}。", id, endpoint.SafeAddress);
            throw new ProxyServiceException(id, "acquire", "领取到的代理已过期。");
        }
        _logger.Information("代理领取成功，Vendor={Vendor}，Proxy={Proxy}，Phase=acquire，Elapsed={Elapsed}ms",
            id, endpoint.SafeAddress, acquireMs);

        var supplierIp = IpInfoValidation.Parse(endpoint.RealIp);
        // 适配器缺少 real_ip 时保持 null；在同一入口决定是否查询详情，避免分别补IP和查详情。
        var needsIpInfo = request.FetchIpDetails || (request.ResolveRealIp && supplierIp is null);
        if (!needsIpInfo)
        {
            _logger.Information("代理获取完成，Vendor={Vendor}，TaskId={TaskId}，Count={Count}，RealIpSource={Source}，无需查询IpInfo",
                id, request.Task?.Id, request.Count, supplierIp is null ? "none" : "vendor");
            return new(endpoint, new JObject()) { RealIp = supplierIp, RealIpSource = supplierIp is null ? "none" : "vendor" };
        }
        // 每个已领取代理最多进入一次详情竞速，同时取得真实IP与完整详情；全失败则重新领取新代理。
        JObject info;
        var ipInfoStarted = Stopwatch.GetTimestamp();
        try
        {
            info = await _ipInfo.QueryAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Information("IpInfo 查询被取消，Vendor={Vendor}，Phase=ipinfo。", id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IpInfo 查询失败，Vendor={Vendor}，Proxy={Proxy}，Phase=ipinfo，Elapsed={Elapsed}ms",
                id, endpoint.SafeAddress, (long)Stopwatch.GetElapsedTime(ipInfoStarted).TotalMilliseconds);
            if (ex is ProxyServiceException serviceException) throw;
            throw new ProxyServiceException(id, "ipinfo", $"通过代理 {endpoint.SafeAddress} 查询 IpInfo 失败：{ex.Message}", ex);
        }

        // 瑕疵判定：拿到有效 JSON 还不够，必须含真实出口 IP，否则视为代理不可用，交给外层重试换新代理。
        // ===== 提取规则区（真实 IP 字段清单按各家 IpInfo 接口增改，只改这里）=====
        if (!IpInfoValidation.HasRealIp(info))
        {
            _logger.Warning("代理未返回真实可用的 IP 信息（可能有瑕疵），Vendor={Vendor}，Proxy={Proxy}，Phase=ipinfo。",
                id, endpoint.SafeAddress);
            throw new ProxyServiceException(id, "ipinfo",
                $"代理 {endpoint.SafeAddress} 未返回真实可用的 IP 信息（可能有瑕疵），丢弃。");
        }
        // ===== 提取规则区结束 =====

        if (endpoint.IsExpired)
        {
            _logger.Warning("IpInfo 查询结束时代理已超过有效期，Vendor={Vendor}，Proxy={Proxy}，Ttl={Ttl}。",
                id, endpoint.SafeAddress, endpoint.Ttl);
            throw new ProxyServiceException(id, "ttl",
                $"代理有效期从领取时开始计算（{endpoint.Ttl}），IpInfo 查询结束时已过期。");
        }

        _logger.Information(
            "代理获取完成，Vendor={Vendor}，Proxy={Proxy}，AcquireElapsed={AcquireMs}ms，IpInfoElapsed={IpInfoMs}ms，IpInfo 顶层字段 {Fields} 个",
            id, endpoint.SafeAddress, acquireMs,
            (long)Stopwatch.GetElapsedTime(ipInfoStarted).TotalMilliseconds, info.Count);
        return new ProxyAcquireResult(endpoint, info)
        { RealIp = IpInfoValidation.GetRealIp(info), RealIpSource = "ipinfo", IpInfoQueried = true };
    }

    /// <summary>异常文本（含内部异常）是否携带供应商 URL 中的凭证值。</summary>
    private static bool LeaksSecret(Exception ex, Options.ProxyVendorOptions vendor)
    {
        var secrets = vendor.SecretValues();
        if (secrets.Count == 0) return false;
        var text = ex.ToString();
        return secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>把消息中的凭证值替换为 ***，保证日志与对外异常永不输出 secret。</summary>
    private static string Scrub(string message, Options.ProxyVendorOptions vendor)
    {
        foreach (var secret in vendor.SecretValues())
            message = message.Replace(secret, "***", StringComparison.Ordinal);
        return message;
    }
    public void Dispose() { foreach (var gate in _vendorGates.Values) gate.Dispose(); _acquireGate.Dispose(); }
}
