using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrafficVista.Serialization;

namespace TrafficVista.Scheduling.Persistence;

/// <summary>
/// Exclusive pending-task snapshot plus append-only consumed-ID journal. Claims are durable before execution.
/// </summary>
internal sealed class LocalTaskCache<T> : IAsyncDisposable
{
    private readonly LocalTaskCacheOptions _options;
    private readonly Func<T, string> _keySelector;
    private readonly ConcurrentDictionary<string, CacheEntry> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _checkpointStop = new();
    private Task _checkpoint = Task.CompletedTask;
    private ConsumedTaskJournal? _claims;
    private long _revision, _savedRevision;
    private string? _error;

    // Store an immutable JSON copy of each producer item, so consumer mutations cannot change cached input.
    public sealed record CacheEntry(string Key, JToken Payload);
    public sealed record CacheDocument(int Version, string TaskType, CacheEntry[] Tasks);

    private LocalTaskCache(LocalTaskCacheOptions options, Func<T, string> keySelector, FileStream lease)
    { _options = options; _keySelector = keySelector; _lease = lease; }

    internal int Count => _pending.Count;
    internal string? Error => Volatile.Read(ref _error);

    internal static async Task<LocalTaskCache<T>> OpenAsync(LocalTaskCacheOptions options, Func<T, string> keySelector)
    {
        var directory = Path.GetDirectoryName(options.FilePath)!;
        Directory.CreateDirectory(directory);
        // Keep the lock file after close; deleting it would race another process taking the same lock.
        var lease = new FileStream(options.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var cache = new LocalTaskCache<T>(options, keySelector, lease);
        try
        {
            if (File.Exists(options.FilePath))
            {
                await using var stream = new FileStream(options.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
                var document = JsonData.Deserialize<CacheDocument>(await reader.ReadToEndAsync().ConfigureAwait(false));
                if (document is null || document.Version != 2 || document.TaskType != typeof(T).FullName || document.Tasks is null)
                    throw new InvalidDataException("本地任务缓存格式或任务类型不匹配。旧版缓存无法区分已领取任务，需人工核查后迁移，禁止自动重放。");
                foreach (var entry in document.Tasks)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Key) || !cache._pending.TryAdd(entry.Key, entry))
                        throw new InvalidDataException("本地任务缓存包含空任务标识或重复标识。");
                    var item = RestoreItem(entry);
                    if (cache.GetKey(item) != entry.Key) throw new InvalidDataException("缓存任务标识与内容不匹配。");
                }
            }
            cache._claims = await ConsumedTaskJournal.OpenAsync(options.FilePath + ".consumed.jsonl",
                typeof(T).FullName!, File.Exists(options.FilePath)).ConfigureAwait(false);
            foreach (var key in cache._pending.Keys)
                if (cache._claims.Contains(key)) cache.Acknowledge(key);
            cache._checkpoint = cache.CheckpointLoopAsync();
            return cache;
        }
        catch
        {
            if (cache._claims is not null) await cache._claims.DisposeAsync().ConfigureAwait(false);
            cache._lease.Dispose();
            cache._writer.Dispose();
            cache._checkpointStop.Dispose();
            throw;
        }
    }

    internal string GetKey(T item)
    {
        var key = _keySelector(item);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("缓存任务标识不能为空。");
        return key;
    }

    internal IEnumerable<T> ReplayItems()
    {
        // Called before the producer starts. Iterate a fixed set; checkpoints/acknowledgments may run concurrently.
        foreach (var entry in _pending.Values.ToArray())
            yield return RestoreItem(entry);
    }

    private static T RestoreItem(CacheEntry entry)
    {
        if (entry.Payload is null) throw new InvalidDataException("缓存任务不能为 null。");
        return entry.Payload.ToObject<T>() ?? throw new InvalidDataException("缓存任务不能为 null。");
    }

    internal async Task<IReadOnlyList<T>> StageBatchAsync(IReadOnlyList<T> batch)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            var accepted = new List<T>(batch.Count);
            foreach (var item in batch)
            {
                var key = GetKey(item);
                // Even when replay is disabled, previously cached IDs remain reserved; do not implicitly replay them.
                if (_claims!.Contains(key) || _pending.ContainsKey(key)) continue;
                var entry = new CacheEntry(key, item is null ? JValue.CreateNull() : JToken.FromObject(item));
                if (_pending.TryAdd(key, entry))
                {
                    Interlocked.Increment(ref _revision);
                    accepted.Add(item);
                }
            }
            // Never pass the run's canceled token here: already fetched items must be saved before exiting.
            await WriteSnapshotAsync().ConfigureAwait(false);
            return accepted;
        }
        catch (Exception ex) { Volatile.Write(ref _error, ex.Message); throw; }
        finally { _writer.Release(); }
    }

    private void Acknowledge(string key)
    {
        if (_pending.TryRemove(key, out _)) Interlocked.Increment(ref _revision);
    }

    internal async Task<bool> ClaimAsync(string key)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            var claimed = await _claims!.ClaimAsync(key).ConfigureAwait(false);
            Acknowledge(key);
            return claimed;
        }
        catch (Exception ex) { Volatile.Write(ref _error, ex.Message); throw; }
        finally { _writer.Release(); }
    }

    internal async Task FlushAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteSnapshotAsync().ConfigureAwait(false);
            if (_options.CompactClaimsWhenEmpty && _pending.IsEmpty)
                await _claims!.CompactAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Volatile.Write(ref _error, ex.Message); throw; }
        finally { _writer.Release(); }
    }

    private async Task WriteSnapshotAsync()
    {
        var revision = Interlocked.Read(ref _revision);
        if (revision == _savedRevision) { Volatile.Write(ref _error, null); return; }
        var document = new CacheDocument(2, typeof(T).FullName!, _pending.Values.ToArray());
        var temporary = _options.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document));
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement: an interrupted write leaves the previous complete document intact.
            File.Move(temporary, _options.FilePath, overwrite: true);
            _savedRevision = revision;
            Volatile.Write(ref _error, null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task CheckpointLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_checkpointStop.Token).ConfigureAwait(false))
            {
                try { await FlushAsync().ConfigureAwait(false); }
                catch (Exception ex) { Volatile.Write(ref _error, ex.Message); }
            }
        }
        catch (OperationCanceledException) when (_checkpointStop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _checkpointStop.Cancel();
            await _checkpoint.ConfigureAwait(false);
            if (_claims is not null) await _claims.DisposeAsync().ConfigureAwait(false);
            if (_options.DeleteWhenEmpty && _pending.IsEmpty)
            {
                File.Delete(_options.FilePath);
                File.Delete(_options.FilePath + ".consumed.jsonl");
            }
        }
        finally
        {
            _lease.Dispose();
            _checkpointStop.Dispose();
            _writer.Dispose();
        }
    }
}
