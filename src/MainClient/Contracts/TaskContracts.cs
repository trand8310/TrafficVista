using Newtonsoft.Json.Linq;

namespace TrafficVista.Contracts;

public sealed record TaskEnvelope(long Id, string Name, string Url, DateTime CreatedAt)
{
    public JObject? Payload { get; init; }
    // The server ID may repeat; local durable claims identify a single delivery/copy instead.
    public string DeliveryId { get; init; } = Guid.NewGuid().ToString("N");
}
public sealed record ProxyInfo(string Address, string Protocol, bool IsHealthy, string? Region = null);
public sealed record DeviceInfo(string DeviceId, string UserAgent, string Platform, int CpuCores, int MemoryGb);
public sealed record TaskExecutionResult(bool Success, bool TriggeredClick, string Message);

public sealed class TaskContext
{
    public required TaskEnvelope Task { get; init; }
    public required ProxyInfo Proxy { get; init; }
    public required DeviceInfo Device { get; init; }
    public required ITaskReporter Reporter { get; init; }
}

public interface ITaskSource
{
    Task<IReadOnlyList<TaskEnvelope>> FetchAsync(CancellationToken token);
}

public interface IProxyProvider
{
    Task<ProxyInfo> AcquireAsync(CancellationToken token);
}

public interface IDeviceProvider
{
    Task<DeviceInfo> AcquireAsync(CancellationToken token);
}

public interface ITaskHandler
{
    string Name { get; }
    Task<TaskExecutionResult> ExecuteAsync(TaskContext context, CancellationToken token);
}

public interface ITaskReporter
{
    void Log(string message);
    void Started(TaskEnvelope task);
    void Completed(TaskEnvelope task, TaskExecutionResult result);
}
