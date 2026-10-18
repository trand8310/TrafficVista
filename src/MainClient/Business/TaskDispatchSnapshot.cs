namespace TrafficVista.Business;

/// <summary>任务投递进度；UV不设共享执行池。</summary>
public sealed record TaskDispatchSnapshot(int PendingUvs, int ActiveTasks);
