using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace TrafficVista.UiLogging;

/// <summary>Producers never format UI text or invoke UI controls. Overflow only drops the UI copy.</summary>
internal sealed class LiveLogSink : ILogEventSink, IDisposable
{
    internal const int PendingCapacity = 512;
    internal const int MaxTextLength = 4096;
    private readonly LiveLogBuffer _buffer;
    private readonly Channel<LogEvent> _pending;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _disposed;

    internal LiveLogSink(LiveLogBuffer buffer)
    {
        _buffer = buffer;
        _pending = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(PendingCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false },
            _ => _buffer.MarkDropped());
        _worker = Task.Run(ProcessAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_pending.Writer.TryWrite(logEvent)) _buffer.MarkDropped();
    }

    private async Task ProcessAsync()
    {
        var formatter = new MessageTemplateTextFormatter("{Message:lj}{NewLine}{Exception}", CultureInfo.InvariantCulture);
        try
        {
            await foreach (var entry in _pending.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    using var text = new LimitedWriter(MaxTextLength);
                    try { formatter.Format(entry, text); }
                    catch (TextLimitReached) { }
                    _buffer.Append(entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), entry.Level, text.ToString().TrimEnd());
                }
                catch (Exception) { _buffer.MarkDropped(); } // Sink errors cannot recurse into logging or stop the application.
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pending.Writer.TryComplete();
        _stop.Cancel();
        // Do not block the UI on a custom property's slow rendering during application shutdown.
        if (_worker.IsCompleted) _stop.Dispose();
        else _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    private sealed class TextLimitReached : Exception;
    private sealed class LimitedWriter(int maximum) : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (_text.Length < maximum) { _text.Append(value); return; }
            _text[maximum - 1] = '…';
            throw new TextLimitReached();
        }
        public override void Write(string? value) { if (value is not null) Write(value.AsSpan()); }
        public override void Write(ReadOnlySpan<char> value)
        {
            var remaining = maximum - _text.Length;
            if (value.Length <= remaining) { _text.Append(value); return; }
            if (remaining > 0) _text.Append(value[..remaining]);
            _text[maximum - 1] = '…';
            throw new TextLimitReached();
        }
        public override string ToString() => _text.ToString();
    }
}
