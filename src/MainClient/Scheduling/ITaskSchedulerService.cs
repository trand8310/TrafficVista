namespace TrafficVista.Scheduling;

public interface ITaskSchedulerService : IAsyncDisposable
{
    bool IsRunning { get; }
    SchedulerSnapshot Snapshot { get; }
    Task<SchedulerSnapshot> Completion { get; }
    Task StartAsync(SchedulerOptions options, CancellationToken token = default);
    Task StopAsync(SchedulerStopMode mode = SchedulerStopMode.Cancel, CancellationToken token = default);
}
