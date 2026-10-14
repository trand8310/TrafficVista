namespace TrafficVista.Scheduling;

/// <summary>Immutable settings, captured separately for each run.</summary>
public sealed record SchedulerOptions
{
    public int WorkerCount { get; init; } = 32;
    public int QueueCapacity { get; init; } = 512;
    /// <summary>Shared task-start limit in a rolling one-second window; null disables rate limiting.</summary>
    public int? MaxStartsPerSecond { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan EmptyPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan SourceRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxSourceRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConsecutiveSourceErrors { get; init; } = 10;
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool CompleteWhenSourceEmpty { get; init; }
    public Persistence.LocalTaskCacheOptions? LocalCache { get; init; }

    public void Validate()
    {
        LocalCache?.Validate();
        if (WorkerCount is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(WorkerCount));
        if (QueueCapacity is < 1 or > 2_048_000) throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (MaxStartsPerSecond is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxStartsPerSecond));
        if (MaxConsecutiveSourceErrors < 1) throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveSourceErrors));
        CheckDuration(PollInterval, nameof(PollInterval), allowZero: true);
        CheckDuration(EmptyPollInterval, nameof(EmptyPollInterval));
        CheckDuration(SourceRetryDelay, nameof(SourceRetryDelay));
        CheckDuration(MaxSourceRetryDelay, nameof(MaxSourceRetryDelay));
        if (MaxSourceRetryDelay < SourceRetryDelay) throw new ArgumentOutOfRangeException(nameof(MaxSourceRetryDelay));
        CheckDuration(FetchTimeout, nameof(FetchTimeout), allowInfinite: true);
        CheckDuration(ExecutionTimeout, nameof(ExecutionTimeout), allowInfinite: true);
        CheckDuration(StopTimeout, nameof(StopTimeout));
    }

    private static void CheckDuration(TimeSpan value, string name, bool allowZero = false, bool allowInfinite = false)
    {
        if (allowInfinite && value == Timeout.InfiniteTimeSpan) return;
        if (value < TimeSpan.Zero || (!allowZero && value == TimeSpan.Zero) || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name);
    }
}
