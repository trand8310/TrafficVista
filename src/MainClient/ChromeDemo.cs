using Microsoft.Playwright;
using Serilog;

namespace TrafficVista;

// Patchright 包沿用 Microsoft.Playwright 命名空间。
public sealed class ChromeDemo : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly BrowserLaunchLimiter _launchLimiter;
    private readonly ILogger _logger;
    private readonly CancellationToken _shutdownToken;
    // 同一个实例的启动、业务操作和关闭按顺序执行，不锁住其他实例。
    private readonly SemaphoreSlim _operations = new(1, 1);
    private IBrowserContext? _context;
    private IBrowser? _browser;
    private readonly FileStream _directoryLock;
    private bool _ready;
    private volatile bool _isRunning;
    private bool _disposed;

    internal ChromeDemo(int userId, string userRoot, IPlaywright playwright, BrowserLaunchLimiter launchLimiter, ILogger logger,
        CancellationToken shutdownToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        UserDataDirectory = Path.Combine(userRoot, InstanceId);
        Directory.CreateDirectory(UserDataDirectory);
        try
        {
            _directoryLock = new FileStream(Path.Combine(UserDataDirectory, "session.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"用户 {userId} 的目录正在被其他实例使用。", ex);
        }
        _playwright = playwright;
        _launchLimiter = launchLimiter;
        _logger = logger.ForContext<ChromeDemo>().ForContext("InstanceId", InstanceId);
        _shutdownToken = shutdownToken;
    }

    public event EventHandler? Disposed;
    public int UserId { get; }
    public string InstanceId => UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string UserDataDirectory { get; }
    public string StorageStatePath => Path.Combine(UserDataDirectory, "storage-state.json");
    // Page 供初始化/诊断使用，业务操作统一走 ExecuteAsync，防止同页操作交错。
    public IPage? Page { get; private set; }
    internal IPlaywright SharedPlaywright => _playwright;
    public bool IsRunning => _isRunning;

    public async Task RunAsync(bool headless = false, CancellationToken cancellationToken = default,
        string? startUrl = null, Func<IPage, Task>? initializePage = null)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = linkedCancellation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_context is not null)
                throw new InvalidOperationException("实例已启动；新浏览器请通过管理器创建新实例。");

            await _launchLimiter.ExecuteAsync(async () =>
            {
                var launchOptions = ChromeStartup.CreateLaunchOptions(headless, UserDataDirectory);
                if (!File.Exists(launchOptions.ExecutablePath))
                    throw new FileNotFoundException("找不到指定的 Chrome，请检查浏览器目录。", launchOptions.ExecutablePath);

                try
                {
                    _logger.Information("启动 H5 触屏实例，用户目录：{UserDataDirectory}，Headless={Headless}",
                        UserDataDirectory, headless);
                    Directory.CreateDirectory(UserDataDirectory);
                    _browser = await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
                    _context = await _browser.NewContextAsync(
                        ChromeStartup.CreateContextOptions(StorageStatePath)).ConfigureAwait(false);
                    _context.Close += (_, _) => _isRunning = false;
                    _isRunning = true;

                    var page = _context.Pages.FirstOrDefault()
                        ?? await _context.NewPageAsync().ConfigureAwait(false);
                    Page = page;
                    page.SetDefaultTimeout(10_000);
                    page.SetDefaultNavigationTimeout(30_000);

                    // 实例专属 Cookie、初始化脚本等在访问网址之前配置。
                    // await page.Context.AddCookiesAsync(...);
                    // await page.AddInitScriptAsync("window.myConfig = {};");
                    if (initializePage is not null)
                        await initializePage(page).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = startUrl ?? ChromeStartup.StartUrl;
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded
                    }).ConfigureAwait(false);
                    if (response is null || !response.Ok)
                        throw new InvalidOperationException($"打开页面失败，HTTP 状态：{response?.Status.ToString() ?? "无响应"}");
                    cancellationToken.ThrowIfCancellationRequested();
                    _ready = true;
                    _logger.Information("实例就绪，网址：{Url}", url);
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        _logger.Information("实例启动已取消");
                    else
                        _logger.Error(ex, "实例启动失败");
                    try
                    {
                        await CloseContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception closeException)
                    {
                        throw new AggregateException("启动失败且浏览器清理失败。", ex, closeException);
                    }
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task ExecuteAsync(Func<IPage, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = linkedCancellation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning || Page is null || Page.IsClosed)
                throw new InvalidOperationException("实例未运行或页面已关闭。");
            cancellationToken.ThrowIfCancellationRequested();
            // H5 示例：await instance.ExecuteAsync(page => ChromeTouch.TapAsync(page, page.GetByText("按钮")));
            await operation(Page).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("实例业务操作已取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "实例业务操作失败");
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task CloseContextAsync()
    {
        var errors = new List<Exception>();
        try
        {
            if (_context is not null && _ready && _isRunning)
            {
                try
                {
                    // 先写临时文件，成功后原子替换，失败保留上一次会话状态。
                    var temporaryPath = StorageStatePath + ".tmp";
                    await _context.StorageStateAsync(new() { Path = temporaryPath, IndexedDB = true }).ConfigureAwait(false);
                    File.Move(temporaryPath, StorageStatePath, overwrite: true);
                }
                catch (Exception ex) { errors.Add(ex); _logger.Error(ex, "保存用户会话失败"); }
            }
            if (_context is not null)
            {
                try { await _context.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
                _logger.Information("实例已关闭");
            }
            if (_browser is not null)
            {
                try { await _browser.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
        }
        finally
        {
            _isRunning = false;
            Page = null;
            _context = null;
            _browser = null;
            _ready = false;
        }
        if (errors.Count > 0)
            throw new AggregateException("保存或关闭用户会话失败。", errors);
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await CloseContextAsync().ConfigureAwait(false);
            }
            finally
            {
                // 这里只关闭本实例；共享 IPlaywright 由 DI 容器在应用退出时释放。
                _directoryLock.Dispose();
                Disposed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _operations.Release();
        }
    }
}
