using Microsoft.Playwright;
using Serilog;

namespace TrafficVista;

// DI 单例工厂，统一拥有全部实例；实例关闭时立即从管理集合移除。
public sealed class ChromeInstanceManager : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly BrowserLaunchLimiter _launchLimiter;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, ChromeDemo> _instances = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _disposeTask;
    private bool _closing;
    private readonly string _userRoot;

    public ChromeInstanceManager(IPlaywright playwright, BrowserLaunchLimiter launchLimiter, ILogger logger,
        string? userRoot = null)
    {
        _playwright = playwright;
        _launchLimiter = launchLimiter;
        _logger = logger.ForContext<ChromeInstanceManager>();
        _userRoot = Path.GetFullPath(userRoot ?? ChromeStartup.ProfileRoot);
    }

    public int MaxConcurrentStarts => _launchLimiter.MaxConcurrentStarts;
    public int PeakConcurrentStarts => _launchLimiter.PeakConcurrentStarts;
    public int InstanceCount { get { lock (_sync) return _instances.Count; } }

    public ChromeDemo CreateInstance(int userId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
            var key = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_instances.ContainsKey(key))
                throw new InvalidOperationException($"用户 {userId} 已有实例，必须先停止后才能再次进入。");
            // 手动创建带异步释放的实例，避免 DI 容器长期保留 transient 对象。
            var instance = new ChromeDemo(userId, _userRoot, _playwright, _launchLimiter, _logger, _shutdown.Token);
            instance.Disposed += InstanceDisposed;
            _instances.Add(instance.InstanceId, instance);
            return instance;
        }
    }

    private void InstanceDisposed(object? sender, EventArgs e)
    {
        var instance = (ChromeDemo)sender!;
        lock (_sync)
        {
            _instances.Remove(instance.InstanceId);
            instance.Disposed -= InstanceDisposed;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closing = true;
            if (_disposeTask is null)
                _logger.Information("应用退出，关闭 {InstanceCount} 个浏览器实例", _instances.Count);
            _disposeTask ??= DisposeInstancesAsync(_instances.Values.ToArray());
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeInstancesAsync(ChromeDemo[] instances)
    {
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(instances.Select(instance => instance.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
