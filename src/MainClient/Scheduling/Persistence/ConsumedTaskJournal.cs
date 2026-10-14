using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using TrafficVista.Serialization;
using System.Threading.Channels;

namespace TrafficVista.Scheduling.Persistence;

/// <summary>Append-only consumed IDs, not execution progress. Group commit precedes every dispatch.</summary>
internal sealed class ConsumedTaskJournal : IAsyncDisposable
{
    internal sealed record Record(int Version, string TaskType, string[] Keys);
    private sealed record Claim(string Key, TaskCompletionSource<bool> Completion);
    private readonly ConcurrentDictionary<string, byte> _consumed = new(StringComparer.Ordinal);
    private readonly Channel<Claim> _requests = Channel.CreateUnbounded<Claim>(new() { SingleReader = true });
    private FileStream _stream;
    private readonly SemaphoreSlim _io = new(1);
    private readonly string _path;
    private readonly string _taskType;
    private Task _writer = Task.CompletedTask;

    private ConsumedTaskJournal(FileStream stream, string taskType) { _stream = stream; _path = stream.Name; _taskType = taskType; }
    internal bool Contains(string key) => _consumed.ContainsKey(key);

    internal static async Task<ConsumedTaskJournal> OpenAsync(string path, string taskType, bool required)
    {
        if (required && !File.Exists(path)) throw new InvalidDataException("消费日志缺失，禁止恢复任务以免重复派发。");
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.Asynchronous);
        var journal = new ConsumedTaskJournal(stream, taskType);
        try
        {
            if (stream.Length == 0)
            {
                if (required) throw new InvalidDataException("消费日志为空，禁止恢复任务以免重复派发。");
                var header = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Record(1, taskType, [])) + "\n");
                await stream.WriteAsync(header).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n') throw new InvalidDataException("消费日志尾部不完整，需人工核查；不会自动重放。");
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 65536, leaveOpen: true);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var record = JsonData.Deserialize<Record>(line);
                    if (record is null || record.Version != 1 || record.TaskType != taskType || record.Keys is null ||
                        record.Keys.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidDataException("消费日志格式或任务类型不匹配。");
                    foreach (var key in record.Keys) journal._consumed.TryAdd(key, 0);
                }
            }
            stream.Position = stream.Length;
            journal._writer = Task.Run(journal.WriteLoopAsync);
            return journal;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async Task<bool> ClaimAsync(string key)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(new(key, completion)).ConfigureAwait(false);
        // No cancellation here: once submitted, the durable outcome must be known before releasing ownership.
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<Claim>(256);
        try
        {
            while (await _requests.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 256 && _requests.Reader.TryRead(out var request)) batch.Add(request);
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    var keys = batch.Select(x => x.Key).Where(x => !Contains(x)).Distinct(StringComparer.Ordinal).ToArray();
                    if (keys.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Record(1, _taskType, keys)) + "\n");
                        await _stream.WriteAsync(bytes).ConfigureAwait(false);
                        await _stream.FlushAsync().ConfigureAwait(false);
                        _stream.Flush(flushToDisk: true);
                    }
                    foreach (var request in batch)
                        request.Completion.TrySetResult(_consumed.TryAdd(request.Key, 0));
                }
                finally { _io.Release(); }
            }
        }
        catch (Exception ex)
        {
            // Uncertain/torn writes are never retried in this journal session. No failed claimant may dispatch.
            _requests.Writer.TryComplete(ex);
            foreach (var request in batch) request.Completion.TrySetException(ex);
            while (_requests.Reader.TryRead(out var request)) request.Completion.TrySetException(ex);
        }
    }

    internal async Task CompactAsync()
    {
        // LocalTaskCache holds its writer gate and has durably saved an empty pending snapshot.
        await _io.WaitAsync().ConfigureAwait(false);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (_stream.Length < 1024 * 1024) return;
            await using (var replacement = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await replacement.WriteAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Record(1, _taskType, [])) + "\n"));
                await replacement.FlushAsync(); replacement.Flush(true);
            }
            // The inbox lease and _io exclude writers. Reopen the original after a failed atomic replacement.
            await _stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Move(temp, _path, true);
                _consumed.Clear();
            }
            finally
            {
                _stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                    65536, FileOptions.Asynchronous);
                _stream.Position = _stream.Length;
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            finally { _io.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _io.Dispose();
    }
}
