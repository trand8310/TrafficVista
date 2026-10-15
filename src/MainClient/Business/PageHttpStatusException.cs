namespace TrafficVista.Business;

/// <summary>Preserves a main-document HTTP status through cleanup and aggregate exceptions.</summary>
internal sealed class PageHttpStatusException(int status, string url)
    : InvalidOperationException($"页面主文档返回 HTTP {status}，URL={url}。")
{
    internal int Status { get; } = status;

    internal static bool Contains644(Exception? error) => error switch
    {
        PageHttpStatusException { Status: 644 } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(Contains644),
        _ => error?.InnerException is { } inner && Contains644(inner)
    };
}
