using Microsoft.Playwright;

namespace TrafficVista.Business;

/// <summary>页面或浏览器非预期退出时中断当前UV的普通等待；正常清理不算故障。</summary>
internal sealed class BrowserLifetimeGuard : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationToken _requested;
    private readonly CancellationTokenSource _stop;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _watching = true;
    private string? _reason;
    internal CancellationToken Token => _stop.Token;
    internal string? Reason { get { lock (_sync) return _reason; } }

    internal BrowserLifetimeGuard(CancellationToken requested)
    {
        _requested = requested;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(requested);
    }
    internal void Watch(IBrowser browser)
    {
        _browser = browser;
        browser.Disconnected += BrowserDisconnected;
        if (!browser.IsConnected) Signal("浏览器连接已断开，可能被手动关闭或崩溃");
    }
    internal void Watch(IBrowserContext context) { _context = context; context.Close += ContextClosed; }
    internal void Watch(IPage page)
    {
        _page = page;
        page.Close += PageClosed;
        page.Crash += PageCrashed;
        if (page.IsClosed) Signal("业务页面已关闭");
    }
    private void BrowserDisconnected(object? sender, IBrowser browser) => Signal("浏览器连接已断开，可能被手动关闭或崩溃");
    private void ContextClosed(object? sender, IBrowserContext context) => Signal("浏览器会话已关闭");
    private void PageClosed(object? sender, IPage page) => Signal("业务页面已关闭，可能被手动关闭");
    private void PageCrashed(object? sender, IPage page) => Signal("业务页面崩溃");

    private void Signal(string reason)
    {
        lock (_sync)
        {
            if (!_watching || _requested.IsCancellationRequested || _reason is not null) return;
            _reason = reason;
            _stop.Cancel();
        }
    }

    internal void StopWatching()
    {
        lock (_sync)
        {
            _watching = false;
            if (_page is not null) { _page.Close -= PageClosed; _page.Crash -= PageCrashed; }
            if (_context is not null) _context.Close -= ContextClosed;
            if (_browser is not null) _browser.Disconnected -= BrowserDisconnected;
        }
    }
    public void Dispose() { StopWatching(); _stop.Dispose(); }
}

