
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Producers;

namespace TrafficVista.Scheduling;

/// <summary>Project adapter: acquires task resources and invokes the existing business handler.</summary>
public sealed class HighThroughputScheduler : ITaskSchedulerService
{
    private readonly AsyncTaskScheduler<TaskEnvelope> _engine;

    public HighThroughputScheduler(ITaskSource source, ITaskHandler handler,
        IProxyProvider proxyProvider, IDeviceProvider deviceProvider, ITaskReporter reporter)
    {
        _engine = new AsyncTaskScheduler<TaskEnvelope>(new TaskSourceProducer(source),
            new TaskHandlerConsumer(handler, proxyProvider, deviceProvider, reporter),
            cacheKeySelector: task => task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool IsRunning => _engine.IsRunning;
    public SchedulerSnapshot Snapshot => _engine.Snapshot;
    public Task<SchedulerSnapshot> Completion => _engine.Completion;
    public Task StartAsync(int workerCount, int queueCapacity, CancellationToken token = default) =>
        StartAsync(new SchedulerOptions { WorkerCount = workerCount, QueueCapacity = queueCapacity }, token);
    public Task StartAsync(SchedulerOptions options, CancellationToken token = default) => _engine.StartAsync(options, token);
    public Task StopAsync(SchedulerStopMode mode = SchedulerStopMode.Cancel, CancellationToken token = default) => _engine.StopAsync(mode, token);
    public ValueTask DisposeAsync() => _engine.DisposeAsync();

}
