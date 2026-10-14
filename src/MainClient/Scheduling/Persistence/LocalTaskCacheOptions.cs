using System.IO;

namespace TrafficVista.Scheduling.Persistence;

/// <summary>Null SchedulerOptions.LocalCache disables persistence; LoadOnStart only controls replay.</summary>
public sealed record LocalTaskCacheOptions
{
    public required string FilePath { get; init; }
    public bool LoadOnStart { get; init; }
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Only use with globally unique delivery IDs. Compacts claims after an empty snapshot is durable.</summary>
    public bool CompactClaimsWhenEmpty { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !Path.IsPathFullyQualified(FilePath))
            throw new ArgumentException("缓存文件必须使用绝对路径。", nameof(FilePath));
        if (CheckpointInterval < TimeSpan.FromMilliseconds(100) || CheckpointInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(CheckpointInterval));
    }
}
