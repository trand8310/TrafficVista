using Microsoft.Playwright;

namespace TrafficVista.Business;

/// <summary>有界检查真实页面内容；不依赖body本身的布局尺寸。</summary>
internal static class PageContentInspector
{
    internal sealed class State
    {
        public string Url { get; set; } = "";
        public string Text { get; set; } = "";
        public bool HasContent { get; set; }
        public bool BrowserError { get; set; }
        public string[] FrameUrls { get; set; } = [];
        public bool Transient { get; set; }
        internal bool IsError => BrowserError || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || InitialPageNavigation.IsNetworkErrorPage(Text);
    }

    private const string Script = """
        () => {
            const body = document.body;
            const visible = el => {
                const box = el.getBoundingClientRect();
                if (box.width <= 0 || box.height <= 0) return false;
                for (let p = el; p; p = p.parentElement) {
                    const s = getComputedStyle(p);
                    if (s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse' || s.opacity === '0') return false;
                }
                return true;
            };
            let content = false;
            if (body) {
                const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
                let node, count = 0;
                while ((node = walker.nextNode()) && count++ < 1000) {
                    const parent = node.parentElement;
                    if (node.textContent.trim() && parent && !parent.closest('script,style,noscript,template') && visible(parent)) {
                        content = true; break;
                    }
                }
                if (!content) content = [...body.querySelectorAll('img,svg,canvas,video,input,textarea,select')].slice(0, 500)
                    .some(el => visible(el) && (el.tagName !== 'IMG' || (el.complete && el.naturalWidth > 0)));
            }
            return { Url: location.href, Text: (body?.innerText || '').trim().slice(0, 2000),
                HasContent: content,
                FrameUrls: body ? [...body.querySelectorAll('iframe,frame')].filter(visible).slice(0, 8).map(el => el.src) : [],
                BrowserError: location.href.startsWith('chrome-error:') || location.href.startsWith('chrome://')
                    || !!document.querySelector('#main-frame-error #error-code') };
        }
        """;

    internal static async Task<T> AwaitInspectionAsync<T>(Task<T> operation, TimeSpan timeout, CancellationToken token)
    {
        try { return await operation.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("页面内容检查调用超时，浏览器控制通道未及时响应；不能据此判断页面为空。", ex);
        }
        finally
        {
            _ = operation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    internal static async Task<State> InspectAsync(IPage page, TimeSpan timeout, CancellationToken token)
    {
        async Task<State> InspectFramesAsync()
        {
            var state = await page.MainFrame.EvaluateAsync<State>(Script).ConfigureAwait(false);
            if (state.IsError || state.HasContent) return state;
            foreach (var frame in page.Frames.Where(f => f != page.MainFrame && state.FrameUrls.Contains(f.Url)).Take(8))
            {
                var child = await frame.EvaluateAsync<State>(Script).ConfigureAwait(false);
                if (!child.IsError && child.HasContent) { state.HasContent = true; break; }
            }
            return state;
        }
        try { return await AwaitInspectionAsync(InspectFramesAsync(), timeout, token).ConfigureAwait(false); }
        catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Frame was detached", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase))
        {
            // 导航/重定向替换了上下文，交给有界轮询再次检查。
            return new State { Url = page.Url, Transient = true };
        }
    }

    internal static async Task EnsureFinalContentAsync(IPage page, Func<int?> status, Func<string?> failure, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = TimeSpan.FromSeconds(3) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (status() == 644) throw new PageHttpStatusException(644, page.Url);
            if (status() == 407) throw new PageHttpStatusException(407, page.Url);
            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException($"浏览结束后3秒内未检测到可见页面内容，URL={page.Url}，HTTP={status()}。");
            var state = await InspectAsync(page, remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            if (!state.Transient)
            {
                if (status() == 407 || state.IsError)
                    throw new InvalidOperationException($"浏览结束时为代理或网络错误页面，URL={state.Url}，HTTP={status()}。");
                if (!string.IsNullOrWhiteSpace(failure()))
                    throw new InvalidOperationException("浏览结束时主文档网络请求失败：" + failure());
                if (state.HasContent && status() is not null) return;
            }
            remaining = TimeSpan.FromSeconds(3) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)), token).ConfigureAwait(false);
        }
    }
}
