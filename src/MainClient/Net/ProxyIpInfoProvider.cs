using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Net.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;

namespace TrafficVista.Net;

/// <summary>
/// IpInfo 查询独立实现：通过给定代理或本机直连访问配置的接口并解析为 JObject。
/// 多接口并行请求；先返回包含有效出口 IP 的 JSON 者胜出，取消并等待其他分支清理。
/// 代理供应商更换不影响这里；另有跨供应商共用的总体并发上限（按查询次数计）。
/// 成功时记录胜出的来源与查询时间，表示该次请求观察到的信息。
/// </summary>
public sealed class ProxyIpInfoProvider : IProxyIpInfoProvider, IDisposable
{
    private readonly NetOptions _options;
    private readonly ProxyTransportFactory _transports;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;

    public ProxyIpInfoProvider(NetOptions options, ProxyTransportFactory transports, ILogger logger)
    {
        _options = options;
        _transports = transports;
        _logger = logger.ForContext<ProxyIpInfoProvider>();
        _gate = new SemaphoreSlim(Math.Max(1, options.IpInfoMaxConcurrency));
    }

    public Task<JObject> QueryAsync(ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return QueryCoreAsync(proxy, cancellationToken);
    }

    public Task<JObject> QueryDirectAsync(CancellationToken cancellationToken) => QueryCoreAsync(null, cancellationToken);

    private async Task<JObject> QueryCoreAsync(ProxyEndpoint? proxy, CancellationToken cancellationToken)
    {
        var urls = _options.IpInfo.EffectiveApiUrls();
        if (urls.Count == 0)
            throw new ProxyProtocolException("Net:IpInfo 的 ApiUrl / ApiUrls 未配置，无法查询 IpInfo。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var race = new CancellationTokenSource();
            using var channel = _transports.CreateClient(proxy, Timeout.InfiniteTimeSpan);
            var pending = urls.Select(url => RaceOneAsync(url, proxy, channel, race.Token, cancellationToken)).ToList();
            JObject? winner = null;
            string? winnerSource = null;
            DateTimeOffset winnerQueriedAt = default;
            Exception? firstError = null;
            while (pending.Count > 0 && winner is null)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var outcome = await done.ConfigureAwait(false); // RaceOneAsync 不抛（调用方取消除外）
                if (outcome?.Info is not null)
                {
                    winner = outcome.Info;
                    winnerSource = outcome.Url;
                    winnerQueriedAt = outcome.QueriedAt;
                }
                else if (outcome?.Error is not null)
                    firstError ??= outcome.Error;
            }
            race.Cancel(); // 淘汰尚未返回的接口
            // 所有分支清理完才释放总体并发许可，不留后台请求。
            await Task.WhenAll(pending).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (winner is null)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                firstError ??= new ProxyProtocolException("IpInfo 全部接口均未成功。");
                _logger.Warning(
                    "IpInfo 全部 {Count} 个接口均未成功，Vendor={Vendor}，Proxy={Proxy}，Elapsed={Elapsed}ms，首个原因：{Reason}",
                    urls.Count, proxy?.VendorId ?? "direct", proxy?.SafeAddress ?? "direct",
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, firstError.Message);
                throw firstError;
            }

            _logger.Information(
                "IpInfo 查询完成（多接口竞速最快），Vendor={Vendor}，Proxy={Proxy}，Source={Source}，QueriedAt={QueriedAt:O}，Elapsed={Elapsed}ms",
                proxy?.VendorId ?? "direct", proxy?.SafeAddress ?? "direct", winnerSource, winnerQueriedAt,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return winner;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record RaceOutcome(string Url, JObject? Info, Exception? Error, DateTimeOffset QueriedAt);

    /// <summary>单个接口的竞速分支：成功返回解析结果，失败返回异常供汇总，不向外抛出。</summary>
    private async Task<RaceOutcome?> RaceOneAsync(string url, ProxyEndpoint? proxy, HttpClient channel, CancellationToken race, CancellationToken caller)
    {
        var queriedAt = DateTimeOffset.Now;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(race, caller);
            var text = await _transports.GetLimitedAsStringAsync(
                url, proxy, TimeSpan.FromSeconds(_options.IpInfo.RequestTimeoutSeconds),
                _options.IpInfo.MaxResponseBytes, "IpInfo", timeout.Token, channel).ConfigureAwait(false);
            try
            {
                var info = JObject.Parse(text);
                if (!IpInfoValidation.HasRealIp(info))
                    throw new ProxyProtocolException("IpInfo JSON 未包含可解析的真实出口 IP。");
                return new RaceOutcome(url, info, null, queriedAt);
            }
            catch (JsonException ex)
            {
                throw new ProxyProtocolException("IpInfo 接口返回的正文不是有效 JSON。", ex);
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // 竞速已结束（本接口输了）：正常现象，只记调试级。
            _logger.Debug("IpInfo 接口竞速未胜出，已取消，Proxy={Proxy}，Source={Source}，Elapsed={Elapsed}ms",
                proxy?.SafeAddress ?? "direct", url, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new RaceOutcome(url, null, null, queriedAt);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpInfo 接口失败，Proxy={Proxy}，Source={Source}，Elapsed={Elapsed}ms，原因：{Reason}",
                proxy?.SafeAddress ?? "direct", url, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            return new RaceOutcome(url, null, ex, queriedAt);
        }
    }
    public void Dispose() => _gate.Dispose();
}
