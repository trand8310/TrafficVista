namespace TrafficVista.Business;

/// <summary>业务消费者固定配置，从 appsettings.json 的 Business 节读取，界面不修改。</summary>
public sealed record BusinessOptions
{
    public string SessionRoot { get; init; } = "business/tasks";
    public string RecoveryRoot { get; init; } = "business/recovery";
    public int MaxUvPerTask { get; init; } = 10000;
    public int MaxPageViewsPerUv { get; init; } = 1000;
    public int MaxDwellSeconds { get; init; } = 86400;
    public string StaticCacheRoot { get; init; } = "business/static-cache";
    public int StaticCacheMaxMegabytes { get; init; } = 512;
    public int StaticCacheTtlHours { get; init; } = 24;
    /// <summary>旧配置兼容默认值；新界面使用TrafficVista:TaskWorkerCount。</summary>
    public int MaxConcurrentTasks { get; init; } = 4;
    public int SessionRetentionDays { get; init; } = 7;
    public int SessionMaxMegabytes { get; init; } = 1024;
    public int MaxExpandedBatchTasks { get; init; } = 10000;
    public int MaxExpandedBatchMegabytes { get; init; } = 16;
    public bool PersistPendingTasks { get; init; } = true;
    public string ChromeExecutablePath { get; init; } = "File/chrome-win/145.0.7632.110/chrome.exe";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionRoot)) throw new InvalidOperationException("业务会话目录不能为空。");
        if (string.IsNullOrWhiteSpace(RecoveryRoot)) throw new InvalidOperationException("任务恢复目录不能为空。");
        if (MaxUvPerTask is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(MaxUvPerTask));
        if (MaxPageViewsPerUv is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(MaxPageViewsPerUv));
        if (MaxDwellSeconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(MaxDwellSeconds));
        if (string.IsNullOrWhiteSpace(StaticCacheRoot)) throw new InvalidOperationException("静态资源缓存目录不能为空。");
        if (StaticCacheMaxMegabytes is < 16 or > 8192) throw new ArgumentOutOfRangeException(nameof(StaticCacheMaxMegabytes));
        if (StaticCacheTtlHours is < 1 or > 8760) throw new ArgumentOutOfRangeException(nameof(StaticCacheTtlHours));
        if (MaxConcurrentTasks is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentTasks));
        if (SessionRetentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(SessionRetentionDays));
        if (SessionMaxMegabytes is < 16 or > 16384) throw new ArgumentOutOfRangeException(nameof(SessionMaxMegabytes));
        if (MaxExpandedBatchTasks is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(MaxExpandedBatchTasks));
        if (MaxExpandedBatchMegabytes is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxExpandedBatchMegabytes));
        if (string.IsNullOrWhiteSpace(ChromeExecutablePath)) throw new ArgumentException("Chrome 路径不能为空。");
    }
}
