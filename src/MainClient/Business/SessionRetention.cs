using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace TrafficVista.Business;

/// <summary>Only completed, marked runs beneath SessionRoot are eligible. Active leases and reparse points are excluded.</summary>
internal sealed class SessionRetention(BusinessOptions options, ILogger logger)
{
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset _last;

    internal async Task SweepAsync(bool force = false)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!force && DateTimeOffset.UtcNow - _last < TimeSpan.FromHours(1)) return;
            _last = DateTimeOffset.UtcNow;
            await Task.Run(Sweep).ConfigureAwait(false);
        }
        catch (Exception ex) { logger.Warning(ex, "已完成会话清理未完成，将在下次清理周期重试。"); }
        finally { _gate.Release(); }
    }

    private void Sweep()
    {
        var root = Path.GetFullPath(options.SessionRoot, AppContext.BaseDirectory);
        if (!Directory.Exists(root) || IsLink(root)) return;
        var runs = new List<(string Path, DateTime Finished, long Bytes)>();
        foreach (var task in Directory.EnumerateDirectories(root))
        {
            if (IsLink(task) || !long.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            foreach (var run in Directory.EnumerateDirectories(task))
            {
                if (IsLink(run) || !Regex.IsMatch(Path.GetFileName(run), @"^\d{8}-\d{9}-\d+-[a-f0-9]{8}$")) continue;
                var summary = Path.Combine(run, "summary.json");
                var marker = Path.Combine(run, "run.lock");
                if (!File.Exists(summary) || !File.Exists(marker) || ContainsLink(run)) continue;
                try
                {
                    using var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    foreach (var uv in Directory.EnumerateDirectories(run))
                    {
                        if (!int.TryParse(Path.GetFileName(uv), out _) || IsLink(uv)) continue;
                        var cleanupMarker = Path.Combine(uv, "profile-cleanup.pending");
                        var profile = Path.Combine(uv, "chrome-profile");
                        if (File.Exists(cleanupMarker))
                        {
                            try
                            {
                                if (Directory.Exists(profile) && !ContainsLink(profile) && !IsLink(profile))
                                    Directory.Delete(profile, recursive: true);
                                if (!Directory.Exists(profile)) File.Delete(cleanupMarker);
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    runs.Add((run, File.GetLastWriteTimeUtc(summary), Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories)
                        .Sum(file => new FileInfo(file).Length)));
                }
                catch (IOException) { }
            }
        }
        var total = runs.Sum(run => run.Bytes);
        foreach (var run in runs.OrderBy(run => run.Finished))
        {
            if (DateTime.UtcNow - run.Finished < TimeSpan.FromDays(options.SessionRetentionDays)
                && total <= options.SessionMaxMegabytes * 1024L * 1024) continue;
            // Recheck ownership and containment immediately before deleting our completed run.
            if (!Path.GetFullPath(run.Path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || ContainsLink(run.Path)) continue;
            try
            {
                // Keep the lease open during deletion; allow delete, but exclude active FileShare.None owners.
                using var lease = new FileStream(Path.Combine(run.Path, "run.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                Directory.Delete(run.Path, recursive: true);
                total -= run.Bytes;
                logger.Debug("清理已完成任务运行目录：{Run}", run.Path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool ContainsLink(string path)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsLink(item)) return true;
            if (Directory.Exists(item) && ContainsLink(item)) return true;
        }
        return false;
    }
}
