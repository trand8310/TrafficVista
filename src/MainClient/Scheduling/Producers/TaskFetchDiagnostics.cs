using System.Diagnostics;

namespace TrafficVista.Scheduling.Producers;

public sealed record TaskFetchSnapshot
{
    public long Requests { get; init; }
    public long Responses { get; init; }
    public long EmptyResponses { get; init; }
    public long NonEmptyResponses => Responses - EmptyResponses;
    public long FailedRequests { get; init; }
    public long InterruptedRequests { get; init; }
    public long ReceivedTasks { get; init; }
    public int? LastTaskCount { get; init; }
    public int? LastHttpStatus { get; init; }
    public double? LastDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double? SecondsSinceLastRequest { get; init; }
    public double? SecondsSinceLastResponse { get; init; }
    public bool InFlight { get; init; }
    public string Stage { get; init; } = "未请求";
    public double StageSeconds { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Counts original HTTP tasks before Multiple expansion. Reset only between runs.</summary>
internal sealed class TaskFetchDiagnostics
{
    private readonly object _sync = new();
    private TaskFetchSnapshot _value = new();
    private long _requestAt, _responseAt, _stageAt;

    internal void Reset()
    {
        lock (_sync)
        {
            _value = new();
            _requestAt = _responseAt = _stageAt = 0;
        }
    }

    internal long Begin()
    {
        lock (_sync)
        {
            _requestAt = _stageAt = Stopwatch.GetTimestamp();
            _value = _value with { Requests = _value.Requests + 1, InFlight = true, Stage = "等待响应头" };
            return _value.Requests;
        }
    }

    internal void SetStage(string stage)
    {
        lock (_sync) { _value = _value with { Stage = stage }; _stageAt = Stopwatch.GetTimestamp(); }
    }

    internal void Finish(int? count, int? status, double durationMs, string? error, bool interrupted)
    {
        lock (_sync)
        {
            if (count.HasValue) _responseAt = Stopwatch.GetTimestamp();
            _value = _value with
            {
                InFlight = false,
                Responses = _value.Responses + (count.HasValue ? 1 : 0),
                EmptyResponses = _value.EmptyResponses + (count == 0 ? 1 : 0),
                FailedRequests = _value.FailedRequests + (!count.HasValue && !interrupted ? 1 : 0),
                InterruptedRequests = _value.InterruptedRequests + (interrupted ? 1 : 0),
                ReceivedTasks = _value.ReceivedTasks + (count ?? 0), LastTaskCount = count,
                LastHttpStatus = status, LastDurationMs = durationMs,
                MaxDurationMs = Math.Max(_value.MaxDurationMs, durationMs), LastError = error,
                Stage = count.HasValue ? (count == 0 ? "空响应" : "有任务") : (interrupted ? "取消或超时" : "失败")
            };
        }
    }

    internal TaskFetchSnapshot Snapshot
    {
        get
        {
            lock (_sync) return _value with
            {
                SecondsSinceLastRequest = _requestAt == 0 ? null : Stopwatch.GetElapsedTime(_requestAt).TotalSeconds,
                SecondsSinceLastResponse = _responseAt == 0 ? null : Stopwatch.GetElapsedTime(_responseAt).TotalSeconds,
                StageSeconds = !_value.InFlight ? 0 : Stopwatch.GetElapsedTime(_stageAt).TotalSeconds
            };
        }
    }
}
