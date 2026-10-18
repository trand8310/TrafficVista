namespace TrafficVista.Configuration;

public enum JavaScriptCacheMode { Disabled = 1, Url = 2, UrlAndContent = 3 }

/// <summary>与 MainForm 的输入项一一对应；间隔单位毫秒，延时/超时/IP 时长单位秒。</summary>
public sealed record TrafficVistaOptions
{
    public const string SectionName = "TrafficVista";
    public string TaskApiUrl { get; init; } = "";
    public string ProxyIpUrl { get; init; } = "";
    public string DevApiUrl { get; init; } = "http://117.21.200.18:9000/api/getdev.php";
    public string TaskName { get; init; } = "";
    public int TaskFetchInterval { get; init; } = 1000;
    public int UvExecutionInterval { get; init; } = 1000;
    /// <summary>任务并发数，即长期工作者数量；每个工作者串行处理任务。</summary>
    public int MaxConcurrency { get; init; } = 1;
    public bool TaskIntakeBackpressureEnabled { get; init; } = true;
    /// <summary>0 disables periodic graceful restart; positive values are minutes from run start.</summary>
    public int AutoRestartMinutes { get; init; }
    /// <summary>0 follows task workers; positive values cap lazily created independent Playwright drivers.</summary>
    public int PlaywrightDriverCount { get; init; } = 1;
    public decimal PageLoadingDelay { get; init; }
    public int PageLoadingTimeout { get; init; } = 1;
    public int Multiple { get; init; } = 1;
    public int IpTtl { get; init; }
    public bool IsHiddenMode { get; init; }
    public bool IsProxyMode { get; init; }
    public bool ResolveRealIp { get; init; } = true;
    public bool FetchIpDetails { get; init; }
    public bool IsCacheMode { get; init; }
    public JavaScriptCacheMode JavaScriptCacheMode { get; init; } = JavaScriptCacheMode.UrlAndContent;
    public string CacheExcludedUrlPrefixes { get; init; } = "";
    public bool VerboseLog { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(JavaScriptCacheMode)) throw new ArgumentOutOfRangeException(nameof(JavaScriptCacheMode));
        _ = new ResourceCachePolicy(this);
        Range(TaskFetchInterval, 1, 60000, nameof(TaskFetchInterval));
        Range(UvExecutionInterval, 0, 60000, nameof(UvExecutionInterval));
        Range(MaxConcurrency, 1, 1024, nameof(MaxConcurrency));
        Range(AutoRestartMinutes, 0, 10080, nameof(AutoRestartMinutes));
        Range(PlaywrightDriverCount, 0, 1024, nameof(PlaywrightDriverCount));
        Range(PageLoadingTimeout, 1, 3600, nameof(PageLoadingTimeout));
        Range(Multiple, 1, 1000, nameof(Multiple));
        Range(IpTtl, 0, 1800, nameof(IpTtl));
        if (PageLoadingDelay is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(PageLoadingDelay), "首页加载延时必须在 0～86400 秒之间。");
    }

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, $"{name} 必须在 {min}～{max} 之间。");
    }
}
