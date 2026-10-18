namespace TrafficVista.Scheduling;

public sealed record ProducerStatusSnapshot(string Stage, double StageSeconds, double? NextFetchInSeconds);
