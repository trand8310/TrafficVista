using TrafficVista.Configuration;
using TrafficVista.Contracts;

namespace TrafficVista.Net.Models;

/// <summary>Per-call task/configuration context. Region text, arrays and unknown fields remain in Task.Payload.</summary>
public sealed record ProxyAcquireRequest
{
    public TaskEnvelope? Task { get; init; }
    public TrafficVistaOptions? Settings { get; init; }
    public int Count { get; init; } = 1;
    // Generic Net calls without UI settings retain the previous full-query behavior.
    public bool ResolveRealIp => Settings?.ResolveRealIp ?? true;
    public bool FetchIpDetails => Settings?.FetchIpDetails ?? true;
    public void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Count);
    public ProxyAcquireRequest Snapshot() => this with
    {
        Task = Task is null ? null : Task with { Payload = (Newtonsoft.Json.Linq.JObject?)Task.Payload?.DeepClone() },
        Settings = Settings is null ? null : Settings with { }
    };
    public override string ToString() => $"TaskId={Task?.Id}, Count={Count}, RealIp={ResolveRealIp}, Details={FetchIpDetails}";
}
