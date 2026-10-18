using Serilog.Core;
using Serilog.Events;
using TrafficVista.Scheduling;

namespace TrafficVista.UiLogging;

/// <summary>Capture at emission time, before the UI sink moves formatting to its background reader.</summary>
internal sealed class WorkerIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        => logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("WorkerId", WorkerExecutionContext.WorkerId));
}
