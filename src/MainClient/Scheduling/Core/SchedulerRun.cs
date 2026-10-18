using TrafficVista.Scheduling.Queues;
using TrafficVista.Scheduling.Persistence;
using TrafficVista.Scheduling.RateLimiting;
using System.Diagnostics;

namespace TrafficVista.Scheduling;

internal sealed class SchedulerRun<T>
{
    internal SchedulerRun(SchedulerOptions options, ITaskQueue<T> queue, CancellationToken token)
    {
        Options = options;
        ProducerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        WorkerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        DequeueCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        FetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Queue = queue;
        StartLimiter = options.MaxStartsPerSecond is { } limit ? new SlidingWindowStartLimiter(limit) : null;
    }

    internal readonly SchedulerOptions Options;
    internal readonly ITaskQueue<T> Queue;
    internal readonly SlidingWindowStartLimiter? StartLimiter;
    internal readonly CancellationTokenSource ProducerCancellation;
    internal readonly CancellationTokenSource WorkerCancellation;
    internal readonly CancellationTokenSource DequeueCancellation;
    internal readonly CancellationTokenSource FetchCancellation;
    internal Task DequeueCancelTask = Task.CompletedTask, FetchCancelTask = Task.CompletedTask;
    internal bool DequeueStopRequested, FetchStopRequested;
    internal bool SuspendRequested;
    internal readonly TaskCompletionSource<SchedulerSnapshot> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Accessed under the scheduler lifecycle lock. Callback tasks must finish before disposing the CTSs.
    internal Task ProducerCancelTask = Task.CompletedTask;
    internal Task WorkerCancelTask = Task.CompletedTask;
    internal bool ProducerStopRequested, WorkerStopRequested;
    internal long Submitted, Running, Completed, Failed, TimedOut, Canceled, Abandoned, SourceErrors;
    internal string? LastError;
    internal Exception? ConsumerError;
    internal LocalTaskCache<T>? Cache;
    internal long Restored;
    internal long WaitingForRateLimit;
    internal long Preparing, Claimed;
    internal long SourceTimeouts;
    private readonly object _producerSync = new();
    private string _producerStage = "等待启动";
    private long _producerStageAt = Stopwatch.GetTimestamp();
    private TimeSpan? _producerWait;

    internal void SetProducerStage(string stage, TimeSpan? wait = null)
    {
        lock (_producerSync)
        {
            _producerStage = stage;
            _producerWait = wait;
            _producerStageAt = Stopwatch.GetTimestamp();
        }
    }

    internal ProducerStatusSnapshot ProducerSnapshot()
    {
        lock (_producerSync)
        {
            var elapsed = Stopwatch.GetElapsedTime(_producerStageAt).TotalSeconds;
            return new(_producerStage, elapsed,
                _producerWait is { } delay ? Math.Max(0, delay.TotalSeconds - elapsed) : null);
        }
    }

    internal SchedulerSnapshot Snapshot(SchedulerState state) => new()
    {
        State = state, Workers = Options.WorkerCount, Queued = Queue.Count,
        Submitted = Interlocked.Read(ref Submitted), Running = Interlocked.Read(ref Running),
        Completed = Interlocked.Read(ref Completed), Failed = Interlocked.Read(ref Failed),
        TimedOut = Interlocked.Read(ref TimedOut), Canceled = Interlocked.Read(ref Canceled),
        Abandoned = Interlocked.Read(ref Abandoned), SourceErrors = Interlocked.Read(ref SourceErrors),
        SourceTimeouts = Interlocked.Read(ref SourceTimeouts), Producer = ProducerSnapshot(),
        LastError = Volatile.Read(ref LastError), Restored = Interlocked.Read(ref Restored),
        Cached = Cache?.Count ?? 0, CacheError = Cache?.Error,
        MaxStartsPerSecond = Options.MaxStartsPerSecond, WaitingForRateLimit = Interlocked.Read(ref WaitingForRateLimit),
        Preparing = Interlocked.Read(ref Preparing), Claimed = Interlocked.Read(ref Claimed)
    };
}
