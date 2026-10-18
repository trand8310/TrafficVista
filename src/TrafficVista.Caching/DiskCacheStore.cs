using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrafficVista.Caching;

internal sealed class DiskCacheStore
{
    // Field names intentionally match TrafficVista's existing v4 cache metadata.
    internal sealed record Metadata(string BodyFile, long Length, string ContentType,
        Dictionary<string, string> Headers, DateTimeOffset StoredAt, int Status);
    internal sealed record Record(string Digest, Metadata Data, string Path, long Bytes);
    internal sealed record Staged(string Key, string Digest, string TemporaryBody, string BodyPath,
        string TemporaryMetadata, string MetadataPath, Metadata Data, long Bytes);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    private const int MaxMetadataBytes = 16384;
    private readonly HybridCacheOptions _options;
    private readonly Action<Exception> _report;
    private readonly object _sync = new();
    private readonly object _bodyFiles = new();
    private readonly Dictionary<string, Record> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _references = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private int _count;
    internal string Root { get; }
    internal DiskCacheStore(HybridCacheOptions options, Action<Exception> report)
    { _options = options; _report = report; Root = System.IO.Path.GetFullPath(options.DirectoryPath); }
    internal (long Bytes, int Count) Size => (Interlocked.Read(ref _bytes), Volatile.Read(ref _count));
    private static string Digest(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal async Task InitializeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        foreach (var path in Directory.EnumerateFiles(Root, "*.meta"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var digest = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!Regex.IsMatch(digest, "^[A-F0-9]{64}$") || new FileInfo(path).Length is < 1 or > MaxMetadataBytes) continue;
                var metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions);
                if (metadata is null || metadata.Headers is null || metadata.Length < 0 || metadata.ContentType is null
                    || metadata.Status is < 100 or > 599 || metadata.BodyFile is null
                    || !(Regex.IsMatch(metadata.BodyFile, "^" + digest + @"\.[a-f0-9]{32}\.body$")
                        || Regex.IsMatch(metadata.BodyFile, @"^js-[A-F0-9]{64}\.body$"))) continue;
                var bodyPath = System.IO.Path.Combine(Root, metadata.BodyFile);
                if (!File.Exists(bodyPath) || new FileInfo(bodyPath).Length != metadata.Length) continue;
                var record = new Record(digest, metadata, bodyPath, metadata.Length + new FileInfo(path).Length);
                lock (_sync) AddRecord(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _report(ex); }
        }
    }

    internal (CacheReadLease Lease, Record Record)? TryGet(string key)
    {
        Record entry;
        lock (_sync)
        {
            if (!_index.TryGetValue(Digest(key), out entry!)
                || _options.Clock.GetUtcNow() - entry.Data.StoredAt >= _options.TimeToLive) return null;
            _readers[entry.Path] = _readers.GetValueOrDefault(entry.Path) + 1;
        }
        try
        {
            if (!File.Exists(entry.Path) || new FileInfo(entry.Path).Length != entry.Data.Length)
            { Release(entry.Path); return null; }
            return (new(ToMetadata(entry.Data), entry.Data.StoredAt, default, entry.Path, () => Release(entry.Path)), entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Release(entry.Path); _report(ex); return null; }
    }
    internal bool IsCurrent(Record record)
    { lock (_sync) return _index.TryGetValue(record.Digest, out var current) && ReferenceEquals(current, record); }
    internal static ResourceMetadata ToMetadata(Metadata data)
        => new(data.ContentType, MemoryCacheStore.CopyHeaders(data.Headers), data.Status)
            { DeduplicateBody = data.BodyFile.StartsWith("js-", StringComparison.Ordinal) };

    internal async Task<Staged> StageAsync(string key, ResourceMetadata metadata, Stream body, CancellationToken token)
    {
        var digest = Digest(key);
        var bodyPath = System.IO.Path.Combine(Root, digest + "." + Guid.NewGuid().ToString("N") + ".body");
        var temporary = bodyPath + ".tmp";
        var metaPath = System.IO.Path.Combine(Root, digest + ".meta");
        var metaTemporary = metaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var storedAt = _options.Clock.GetUtcNow();
        try
        {
            long length;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (metadata.DeduplicateBody)
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    bodyPath = System.IO.Path.Combine(Root, "js-" + Convert.ToHexString(hash.GetHashAndReset()) + ".body");
                }
                else await body.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                length = output.Length;
            }
            var data = new Metadata(System.IO.Path.GetFileName(bodyPath), length, metadata.ContentType,
                MemoryCacheStore.CopyHeaders(metadata.Headers), storedAt, metadata.StatusCode);
            var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            if (json.Length > MaxMetadataBytes) throw new ArgumentException("Resource metadata exceeds 16 KiB.", nameof(metadata));
            await File.WriteAllBytesAsync(metaTemporary, json, token).ConfigureAwait(false);
            return new(key, digest, temporary, bodyPath, metaTemporary, metaPath, data, length + json.Length);
        }
        catch { DeleteFile(temporary); DeleteFile(metaTemporary); throw; }
    }

    // Called by one publisher. Filesystem work never holds the shared index/read-lease lock.
    internal Record? Publish(Staged staged)
    { lock (_bodyFiles) return PublishCore(staged); }
    private Record? PublishCore(Staged staged)
    {
        if (_options.Clock.GetUtcNow() - staged.Data.StoredAt >= _options.TimeToLive) return null;
        var reused = staged.Data.BodyFile.StartsWith("js-", StringComparison.Ordinal) && File.Exists(staged.BodyPath);
        if (reused) DeleteFile(staged.TemporaryBody);
        else File.Move(staged.TemporaryBody, staged.BodyPath);
        try { File.Move(staged.TemporaryMetadata, staged.MetadataPath, true); }
        catch { if (!reused) DeleteFile(staged.BodyPath); throw; }
        var record = new Record(staged.Digest, staged.Data, staged.BodyPath, staged.Bytes);
        Record? old;
        lock (_sync)
        {
            _index.TryGetValue(staged.Digest, out old);
            if (old is not null) RemoveRecord(old);
            AddRecord(record);
        }
        if (old is not null) Retire(old.Path);
        Record[] candidates;
        lock (_sync) candidates = _bytes <= _options.DiskCapacityBytes ? []
            : _index.Values.Where(value => value.Digest != record.Digest).OrderBy(value => value.Data.StoredAt).ToArray();
        // Keep one oversized resource, matching the existing disk cache policy.
        foreach (var candidate in candidates)
        {
            if (Interlocked.Read(ref _bytes) <= _options.DiskCapacityBytes) break;
            try
            {
                File.Delete(System.IO.Path.Combine(Root, candidate.Digest + ".meta"));
                lock (_sync)
                { RemoveRecord(candidate); }
                Retire(candidate.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _report(ex); }
        }
        return record;
    }

    internal void Clear()
    {
        Record[] entries;
        lock (_sync) entries = _index.Values.ToArray();
        // Leave a failed deletion indexed so callers can retry and size accounting stays honest.
        foreach (var entry in entries)
        {
            File.Delete(System.IO.Path.Combine(Root, entry.Digest + ".meta"));
            lock (_sync)
            { RemoveRecord(entry); }
            Retire(entry.Path);
        }
    }
    private void Retire(string path)
    { lock (_bodyFiles) RetireCore(path); }
    private void RetireCore(string path)
    {
        bool delete;
        lock (_sync)
        {
            if (_references.ContainsKey(path)) return;
            delete = !_readers.ContainsKey(path); if (!delete) _retired.Add(path);
        }
        if (delete) DeleteFile(path);
    }
    private void Release(string path)
    { lock (_bodyFiles) ReleaseCore(path); }
    private void ReleaseCore(string path)
    {
        var delete = false;
        lock (_sync)
        {
            if (_readers[path] > 1) _readers[path]--;
            else { _readers.Remove(path); delete = _retired.Remove(path); }
        }
        if (delete) DeleteFile(path);
    }
    internal static void Cleanup(Staged staged)
    { DeleteFile(staged.TemporaryBody); DeleteFile(staged.TemporaryMetadata); }
    // Called under _sync; body storage is counted once across independent URL metadata records.
    private void AddRecord(Record record)
    {
        var references = _references.GetValueOrDefault(record.Path);
        _references[record.Path] = references + 1;
        _retired.Remove(record.Path);
        _index[record.Digest] = record;
        Interlocked.Add(ref _bytes, record.Bytes - (references == 0 ? 0 : record.Data.Length));
        Volatile.Write(ref _count, _index.Count);
    }
    private void RemoveRecord(Record record)
    {
        _index.Remove(record.Digest);
        var references = _references[record.Path];
        if (references == 1) _references.Remove(record.Path); else _references[record.Path] = references - 1;
        Interlocked.Add(ref _bytes, -(record.Bytes - (references == 1 ? 0 : record.Data.Length)));
        Volatile.Write(ref _count, _index.Count);
    }
    private static void DeleteFile(string path)
    { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
