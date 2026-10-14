using TrafficVista.Scheduling.Queues;
using TrafficVista.Scheduling.Persistence;
using TrafficVista.Scheduling.RateLimiting;

namespace TrafficVista.Scheduling;

internal sealed class SchedulerRun<T>
{
    internal SchedulerRun(SchedulerOptions options, ITaskQueue<T> queue, CancellationToken token)
    {
        Options = options;
        ProducerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        WorkerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Queue = queue;
        StartLimiter = options.MaxStartsPerSecond is { } limit ? new SlidingWindowStartLimiter(limit) : null;
    }

    internal readonly SchedulerOptions Options;
    internal readonly ITaskQueue<T> Queue;
    internal readonly SlidingWindowStartLimiter? StartLimiter;
    internal readonly CancellationTokenSource ProducerCancellation;
    internal readonly CancellationTokenSource WorkerCancellation;
    internal readonly TaskCompletionSource<SchedulerSnapshot> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Accessed under the scheduler lifecycle lock. Callback tasks must finish before disposing the CTSs.
    internal Task ProducerCancelTask = Task.CompletedTask;
    internal Task WorkerCancelTask = Task.CompletedTask;
    internal bool ProducerStopRequested, WorkerStopRequested;
    internal long Submitted, Running, Completed, Failed, TimedOut, Canceled, Abandoned, SourceErrors;
    internal string? LastError;
    internal LocalTaskCache<T>? Cache;
    internal long Restored;
    internal long WaitingForRateLimit;
    internal long Preparing, Claimed;

    internal SchedulerSnapshot Snapshot(SchedulerState state) => new()
    {
        State = state, Workers = Options.WorkerCount, Queued = Queue.Count,
        Submitted = Interlocked.Read(ref Submitted), Running = Interlocked.Read(ref Running),
        Completed = Interlocked.Read(ref Completed), Failed = Interlocked.Read(ref Failed),
        TimedOut = Interlocked.Read(ref TimedOut), Canceled = Interlocked.Read(ref Canceled),
        Abandoned = Interlocked.Read(ref Abandoned), SourceErrors = Interlocked.Read(ref SourceErrors),
        LastError = Volatile.Read(ref LastError), Restored = Interlocked.Read(ref Restored),
        Cached = Cache?.Count ?? 0, CacheError = Cache?.Error,
        MaxStartsPerSecond = Options.MaxStartsPerSecond, WaitingForRateLimit = Interlocked.Read(ref WaitingForRateLimit),
        Preparing = Interlocked.Read(ref Preparing), Claimed = Interlocked.Read(ref Claimed)
    };
}
