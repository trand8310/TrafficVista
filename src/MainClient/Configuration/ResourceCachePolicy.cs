namespace TrafficVista.Configuration;

/// <summary>Per-UV immutable cache policy; JS exclusions bypass cache reads and writes, never network access.</summary>
internal sealed class ResourceCachePolicy
{
    private readonly JavaScriptCacheMode _mode;
    private readonly Uri[] _excluded;
    internal ResourceCachePolicy(TrafficVistaOptions options)
    {
        _mode = options.JavaScriptCacheMode;
        _excluded = (options.CacheExcludedUrlPrefixes ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(prefix => Uri.TryCreate(prefix, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
                ? uri : throw new InvalidDataException("JS缓存排除前缀每行填写一个完整HTTP/HTTPS URL，不含账号或片段：" + prefix)).Distinct().ToArray();
    }
    internal bool Bypass(string resourceType, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        var isJavaScript = resourceType == "script" || uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        if (!isJavaScript) return false;
        if (_mode == JavaScriptCacheMode.Disabled) return true;
        return _excluded.Any(prefix => uri.Scheme == prefix.Scheme && uri.Host.Equals(prefix.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == prefix.Port && uri.PathAndQuery.StartsWith(prefix.PathAndQuery, StringComparison.Ordinal));
    }
    internal bool DeduplicateJavaScript => _mode == JavaScriptCacheMode.UrlAndContent;
}
