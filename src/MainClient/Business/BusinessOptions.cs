namespace TrafficVista.Business;

public enum TaskRecoveryMode { Discard, Save }

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
    public int StaticCacheMemoryMegabytes { get; init; } = 64;
    public int StaticCacheMaxMemoryEntryMegabytes { get; init; } = 4;
    public int StaticCacheWriteQueueCapacity { get; init; } = 32;
    public int StaticCacheTtlHours { get; init; } = 24;
    /// <summary>旧配置兼容默认值；新界面使用TrafficVista:MaxConcurrency。</summary>
    public int MaxConcurrentTasks { get; init; } = 4;
    public int SessionRetentionDays { get; init; } = 7;
    public int SessionMaxMegabytes { get; init; } = 1024;
    public int MaxExpandedBatchTasks { get; init; } = 10000;
    public int MaxExpandedBatchMegabytes { get; init; } = 16;
    /// <summary>默认只使用内存队列；Save模式保存尚未开始执行的任务供重启恢复。</summary>
    public TaskRecoveryMode TaskRecoveryMode { get; init; } = TaskRecoveryMode.Discard;
    /// <summary>显式开启后才导出任务、设备、运行结果及会话快照。</summary>
    public bool ExportRunData { get; init; } = false;
    /// <summary>Chrome启动后，Context、页面及环境设置共用的准备超时。</summary>
    public int BrowserSetupTimeoutSeconds { get; init; } = 20;
    public int BrowserCloseGraceMilliseconds { get; init; } = 1000;
    public int TaskIntakePauseBrowserQueue { get; init; } = 100;
    public int TaskIntakeResumeBrowserQueue { get; init; } = 50;
    public string ChromeExecutablePath { get; init; } = "File/chrome-win/145.0.7632.110/chrome.exe";

    public void Validate()
    {
        if (!Enum.IsDefined(TaskRecoveryMode)) throw new ArgumentOutOfRangeException(nameof(TaskRecoveryMode));
        if (TaskIntakePauseBrowserQueue < 1 || TaskIntakeResumeBrowserQueue < 0
            || TaskIntakeResumeBrowserQueue >= TaskIntakePauseBrowserQueue)
            throw new ArgumentOutOfRangeException(nameof(TaskIntakeResumeBrowserQueue), "任务领取恢复阈值必须非负且小于暂停阈值。");
        if (BrowserSetupTimeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(BrowserSetupTimeoutSeconds));
        if (BrowserCloseGraceMilliseconds is < 100 or > 10000) throw new ArgumentOutOfRangeException(nameof(BrowserCloseGraceMilliseconds));
        if (string.IsNullOrWhiteSpace(SessionRoot)) throw new InvalidOperationException("业务会话目录不能为空。");
        if (string.IsNullOrWhiteSpace(RecoveryRoot)) throw new InvalidOperationException("任务恢复目录不能为空。");
        if (MaxUvPerTask is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(MaxUvPerTask));
        if (MaxPageViewsPerUv is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(MaxPageViewsPerUv));
        if (MaxDwellSeconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(MaxDwellSeconds));
        if (string.IsNullOrWhiteSpace(StaticCacheRoot)) throw new InvalidOperationException("静态资源缓存目录不能为空。");
        if (StaticCacheMaxMegabytes is < 16 or > 8192) throw new ArgumentOutOfRangeException(nameof(StaticCacheMaxMegabytes));
        if (StaticCacheMemoryMegabytes is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(StaticCacheMemoryMegabytes));
        if (StaticCacheMaxMemoryEntryMegabytes is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(StaticCacheMaxMemoryEntryMegabytes));
        if (StaticCacheWriteQueueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(StaticCacheWriteQueueCapacity));
        if (StaticCacheTtlHours is < 1 or > 8760) throw new ArgumentOutOfRangeException(nameof(StaticCacheTtlHours));
        if (MaxConcurrentTasks is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentTasks));
        if (SessionRetentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(SessionRetentionDays));
        if (SessionMaxMegabytes is < 16 or > 16384) throw new ArgumentOutOfRangeException(nameof(SessionMaxMegabytes));
        if (MaxExpandedBatchTasks is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(MaxExpandedBatchTasks));
        if (MaxExpandedBatchMegabytes is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxExpandedBatchMegabytes));
        if (string.IsNullOrWhiteSpace(ChromeExecutablePath)) throw new ArgumentException("Chrome 路径不能为空。");
    }
}
