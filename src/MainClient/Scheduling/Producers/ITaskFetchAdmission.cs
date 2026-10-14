namespace TrafficVista.Scheduling.Producers;

/// <summary>Local backpressure waits before the remote fetch timeout begins.</summary>
public interface ITaskFetchAdmission
{
    Task WaitToFetchAsync(CancellationToken token);
}
