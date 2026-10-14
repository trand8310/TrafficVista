namespace TrafficVista.Configuration;

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
    public int MaxConcurrency { get; init; } = 1;
    /// <summary>任务工作协程数量，与UV/浏览器并发上限分别配置。</summary>
    public int TaskWorkerCount { get; init; } = 4;
    public decimal PageLoadingDelay { get; init; }
    public int PageLoadingTimeout { get; init; } = 1;
    public int Multiple { get; init; } = 1;
    public int IpTtl { get; init; }
    public bool IsHiddenMode { get; init; }
    public bool IsProxyMode { get; init; }
    public bool ResolveRealIp { get; init; } = true;
    public bool FetchIpDetails { get; init; }
    public bool IsCacheMode { get; init; }
    public bool VerboseLog { get; init; }

    public void Validate()
    {
        Range(TaskFetchInterval, 1, 60000, nameof(TaskFetchInterval));
        Range(UvExecutionInterval, 0, 60000, nameof(UvExecutionInterval));
        Range(MaxConcurrency, 1, 1024, nameof(MaxConcurrency));
        Range(TaskWorkerCount, 1, 1024, nameof(TaskWorkerCount));
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
