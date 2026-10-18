namespace TrafficVista.Contracts;

/// <summary>Assign a runtime worker to a consumed copy without modifying the queued/persisted delivery.</summary>
public interface IWorkerAssignedTask<T>
{
    T AssignWorker(int workerId);
}
