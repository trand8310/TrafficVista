namespace TrafficVista.Scheduling.Persistence;

/// <summary>One-use restart handoff, separate from ordinary recovery. Directory moves include the claim journal.</summary>
internal static class RestartQueueFiles
{
    internal static LocalTaskCacheOptions Create(string root, string? resumeToken = null)
    {
        Directory.CreateDirectory(root);
        var active = Path.Combine(root, Guid.NewGuid().ToString("N") + ".active");
        if (resumeToken is not null)
        {
            if (!Guid.TryParseExact(resumeToken, "N", out var token))
                throw new InvalidDataException("定时重启恢复标识无效。");
            var ready = Path.Combine(root, token.ToString("N") + ".ready");
            if (!Directory.Exists(ready))
                throw new InvalidDataException("定时重启暂存队列不存在或已经加载，禁止重复恢复。");
            // Atomic one-use claim: a second process or stale command can no longer find the ready directory.
            Directory.Move(ready, active);
        }
        return new LocalTaskCacheOptions
        {
            FilePath = Path.Combine(active, "queue.json"), LoadOnStart = resumeToken is not null,
            CompactClaimsWhenEmpty = true, RestartOnly = true, DeleteWhenEmpty = true
        };
    }

    internal static string Publish(LocalTaskCacheOptions options)
    {
        if (!options.RestartOnly) throw new InvalidOperationException("当前运行没有独立的定时重启暂存队列。");
        var active = Path.GetDirectoryName(options.FilePath)!;
        var name = Path.GetFileName(active);
        if (!name.EndsWith(".active", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[..^7], "N", out var token))
            throw new InvalidOperationException("定时重启工作目录无效。");
        // Called only after scheduler completion closed its snapshot, journal and exclusive lease.
        Directory.Move(active, Path.Combine(Path.GetDirectoryName(active)!, token.ToString("N") + ".ready"));
        return token.ToString("N");
    }
}
