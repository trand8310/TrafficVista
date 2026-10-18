using Microsoft.Playwright;
using Serilog;
using System.Diagnostics;

namespace TrafficVista.Business;

/// <summary>首次导航允许慢资源/超时下已有内容的页面继续，不吞掉主文档网络错误或任务取消。</summary>
internal static class InitialPageNavigation
{
    internal sealed record Result(int? Status, IReadOnlyList<string> Warnings);

    internal static async Task<Result> OpenAsync(IPage page, string url, ILogger logger,
        long taskId, long uvId, CancellationToken token, int timeoutSeconds = 30)
    {
        IResponse? document = null;
        string? networkFailure = null;
        var warnings = new List<string>();
        EventHandler<IResponse> responseHandler = (_, response) =>
        {
            if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
            { document = response; networkFailure = null; }
        };
        EventHandler<IRequest> failedHandler = (_, request) =>
        {
            if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                networkFailure = request.Failure;
        };
        page.Response += responseHandler;
        page.RequestFailed += failedHandler;
        try
        {
            var timedOut = false;
            var started = Stopwatch.GetTimestamp();
            var budget = TimeSpan.FromSeconds(timeoutSeconds);
            try
            {
                document = await ObserveCancellation(page.GotoAsync(url,
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeoutSeconds * 1000 }), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                token.ThrowIfCancellationRequested();
                timedOut = true;
            }
            token.ThrowIfCancellationRequested();
            if (document?.Status == 644) throw new PageHttpStatusException(644, document.Url);
            if (!string.IsNullOrWhiteSpace(networkFailure))
                throw new InvalidOperationException("首次页面主文档网络请求失败：" + networkFailure);
            if (document?.Status == 407)
                throw new PageHttpStatusException(407, document.Url);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = budget - Stopwatch.GetElapsedTime(started);
                // Use the remaining navigation budget for the control-channel round trip. A busy
                // driver is not a failed URL merely because it takes more than two seconds to reply.
                // Preserve the existing final two-second check for an already committed slow document.
                var inspectionBudget = remaining > TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
                var state = await PageContentInspector.InspectAsync(page, inspectionBudget, token).ConfigureAwait(false);
                if (document?.Status == 644) throw new PageHttpStatusException(644, document.Url);
                if (document?.Status == 407) throw new PageHttpStatusException(407, document.Url);
                if (!string.IsNullOrWhiteSpace(networkFailure))
                    throw new InvalidOperationException("首次页面主文档网络请求失败：" + networkFailure);
                if (document?.Status == 407 || (!state.Transient && state.IsError))
                    throw new InvalidOperationException($"首次页面为代理或网络错误页面，URL={state.Url}，HTTP={document?.Status}。");
                if (!state.Transient && state.HasContent && document is not null) break;
                remaining = budget - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw new InvalidOperationException($"首次页面在{timeoutSeconds}秒内没有可见内容，URL={state.Url}，HTTP={document?.Status}。");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)), token).ConfigureAwait(false);
            }
            if (timedOut) warnings.Add("首次导航超时，但已有可见内容，按已打开继续；代理或资源可能较慢。");
            foreach (var warning in warnings)
                logger.Warning("页面打开提示，TaskId={TaskId}，UvId={UvId}，提示={Warning}", taskId, uvId, warning);
            return new(document.Status, warnings);
        }
        finally
        {
            page.Response -= responseHandler;
            page.RequestFailed -= failedHandler;
        }
    }

    internal static bool IsNetworkErrorPage(string text)
    {
        // 仅短错误正文识别，避免正文文章提及错误码时被误判；不把所有非2xx当作代理失败。
        if (text.Length > 1500) return false;
        var markers = new[] { "net::ERR_", "ERR_PROXY_CONNECTION_FAILED", "ERR_TUNNEL_CONNECTION_FAILED",
            "ERR_CONNECTION_REFUSED", "ERR_CONNECTION_RESET", "ERR_NAME_NOT_RESOLVED", "ERR_TIMED_OUT",
            "Proxy Authentication Required", "Proxy connection failed", "Tunnel connection failed",
            "代理失效", "代理无效", "代理认证失败", "隧道连接失败", "无法访问此网站", "无法访问该网站",
            "This site can’t be reached", "This site can't be reached" };
        return markers.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase))
            || text.StartsWith("Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("502 Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("504 Gateway", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Gateway Timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T> ObserveCancellation<T>(Task<T> task, CancellationToken token)
    {
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch { _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
    }
}

