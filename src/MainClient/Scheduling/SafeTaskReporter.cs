using TrafficVista.Contracts;

namespace TrafficVista.Scheduling;

// Reporting is best effort. A UI/logger exception must not kill workers or misclassify a business result.
internal sealed class SafeTaskReporter(ITaskReporter inner) : ITaskReporter
{
    public void Log(string message) { try { inner.Log(message); } catch (Exception) { } }
    public void Started(TaskEnvelope task) { try { inner.Started(task); } catch (Exception) { } }
    public void Completed(TaskEnvelope task, TaskExecutionResult result) { try { inner.Completed(task, result); } catch (Exception) { } }
}
