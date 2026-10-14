namespace TrafficVista.LocalTesting;

public sealed record LocalTestingOptions
{
    public string SessionRoot { get; init; } = "145_s1/tasks";
    public string? ProxyServer { get; init; }
    public int MaxUvPerTask { get; init; } = 1000;
    public int MaxDwellSeconds { get; init; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionRoot)) throw new InvalidOperationException("本地会话目录不能为空。");
        if (MaxUvPerTask is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(MaxUvPerTask));
        if (MaxDwellSeconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(MaxDwellSeconds));
        if (!string.IsNullOrWhiteSpace(ProxyServer)) LocalEndpointPolicy.RequireLoopback(ProxyServer, "本地代理");
    }
}
