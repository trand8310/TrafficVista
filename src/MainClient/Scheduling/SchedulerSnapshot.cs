namespace TrafficVista.Scheduling;

public enum SchedulerState { Stopped, Running, Stopping, Faulted, Disposed }
public enum SchedulerStopMode { Drain, Cancel }

/// <summary>Live counters are sampled independently; final counters are stable.</summary>
public sealed record SchedulerSnapshot
{
    public SchedulerState State { get; init; }
    public int Workers { get; init; }
    public int Queued { get; init; }
    public long Submitted { get; init; }
    public long Running { get; init; }
    /// <summary>All finished executions, including failed executions; excludes canceled executions.</summary>
    public long Completed { get; init; }
    public long Failed { get; init; }
    public long Succeeded => Completed - Failed;
    public long TimedOut { get; init; }
    public long Canceled { get; init; }
    public long Abandoned { get; init; }
    public long SourceErrors { get; init; }
    public string? LastError { get; init; }
    public long Restored { get; init; }
    public int Cached { get; init; }
    public string? CacheError { get; init; }
    public int? MaxStartsPerSecond { get; init; }
    public long WaitingForRateLimit { get; init; }
    public long Preparing { get; init; }
    /// <summary>Tasks committed for one-shot consumption, including canceled/failed executions.</summary>
    public long Claimed { get; init; }
}
