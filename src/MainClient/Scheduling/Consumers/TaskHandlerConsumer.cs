
using TrafficVista.Contracts;

namespace TrafficVista.Scheduling.Consumers;

/// <summary>Acquires project resource descriptions and invokes the business handler.</summary>
public sealed class TaskHandlerConsumer : ITaskConsumer<TaskEnvelope>
{
    private readonly ITaskHandler _handler;
    private readonly IProxyProvider _proxyProvider;
    private readonly IDeviceProvider _deviceProvider;
    private readonly SafeTaskReporter _reporter;

    public TaskHandlerConsumer(ITaskHandler handler, IProxyProvider proxyProvider,
        IDeviceProvider deviceProvider, ITaskReporter reporter)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _proxyProvider = proxyProvider ?? throw new ArgumentNullException(nameof(proxyProvider));
        _deviceProvider = deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));
        _reporter = new SafeTaskReporter(reporter ?? throw new ArgumentNullException(nameof(reporter)));
    }

    public async ValueTask<bool> ConsumeAsync(TaskEnvelope task, CancellationToken token)
    {
        _reporter.Started(task);
        try
        {
            var context = new TaskContext
            {
                Task = task,
                Proxy = await _proxyProvider.AcquireAsync(token).ConfigureAwait(false),
                Device = await _deviceProvider.AcquireAsync(token).ConfigureAwait(false),
                Reporter = _reporter
            };
            var result = await _handler.ExecuteAsync(context, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _reporter.Completed(task, result);
            return result.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _reporter.Completed(task, new(false, false, ex.Message));
            throw;
        }
    }
}
