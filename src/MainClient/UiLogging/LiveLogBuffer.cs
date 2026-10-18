using Serilog.Events;

namespace TrafficVista.UiLogging;

internal sealed record LiveLogEntry(long Sequence, string Time, LogEventLevel Level, string Text, int WorkerId);

/// <summary>Bounded, formatted history shared by the sink and independent UI readers.</summary>
internal sealed class LiveLogBuffer(int capacity = 2000)
{
    private readonly object _sync = new();
    private readonly LiveLogEntry?[] _entries = new LiveLogEntry[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private long _sequence;
    private long _dropped;
    public int Capacity => _entries.Length;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long LatestSequence { get { lock (_sync) return _sequence; } }
    internal void MarkDropped() => Interlocked.Increment(ref _dropped);
    internal void Append(string time, LogEventLevel level, string text, int workerId = 0)
    {
        lock (_sync)
        {
            var id = ++_sequence;
            _entries[(int)((id - 1) % Capacity)] = new(id, time, level, text, workerId);
        }
    }
    internal LiveLogEntry[] ReadAfter(long cursor, int limit, out long skipped)
    {
        lock (_sync)
        {
            var oldest = Math.Max(1, _sequence - Capacity + 1);
            skipped = Math.Max(0, oldest - cursor - 1);
            var first = Math.Max(oldest, cursor + 1);
            var count = (int)Math.Clamp(_sequence - first + 1, 0, limit);
            var result = new LiveLogEntry[count];
            for (var i = 0; i < count; i++) result[i] = _entries[(int)((first + i - 1) % Capacity)]!;
            return result;
        }
    }
}
