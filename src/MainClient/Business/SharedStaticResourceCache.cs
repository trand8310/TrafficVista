using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using TrafficVista.Serialization;

namespace TrafficVista.Business;

/// <summary>Immutable body files, atomically published metadata, and leases protecting active cache hits.</summary>
public sealed class SharedStaticResourceCache : IDisposable
{
    public sealed record Entry(byte[] Body, string ContentType, Dictionary<string, string> Headers, int Status = 200);
    public sealed class FileEntry : IDisposable
    {
        private Action? _release;
        public string Path { get; }
        public string ContentType { get; }
        public Dictionary<string, string> Headers { get; }
        public int Status { get; }
        internal FileEntry(string path, string type, Dictionary<string, string> headers, int status, Action release)
        { Path = path; ContentType = type; Headers = headers; Status = status; _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private sealed record Metadata(string BodyFile, long Length, string ContentType, Dictionary<string, string> Headers,
        DateTimeOffset StoredAt, int Status);
    private sealed record Indexed(string BodyPath, long Bytes, DateTime Written);
    private const int MaxMetadataBytes = 16384;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly SemaphoreSlim _captures = new(2, 2);
    private readonly Dictionary<string, Indexed> _index = new();
    private readonly object _leaseSync = new();
    private readonly Dictionary<string, int> _readers = new();
    private readonly HashSet<string> _pendingDelete = new();
    private readonly long _maxBytes;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private bool _indexed;
    public string Root { get; }
    internal bool TryBeginCapture() => _captures.Wait(0);
    internal void EndCapture() => _captures.Release();

    public SharedStaticResourceCache(BusinessOptions options, ILogger logger, TimeProvider? clock = null)
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetFullPath(options.StaticCacheRoot, AppContext.BaseDirectory), "v4");
        _maxBytes = options.StaticCacheMaxMegabytes * 1024L * 1024;
        _ttl = TimeSpan.FromHours(options.StaticCacheTtlHours);
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    // Ordinary body filenames are SHA256(full URL); ranges have separate keys.
    // partition is retained for callers but deliberately does not affect reuse.
    public static string RequestKey(string url, IReadOnlyDictionary<string, string> headers, string partition)
        => headers.TryGetValue("range", out var range) && !string.IsNullOrEmpty(range)
            ? JsonConvert.SerializeObject(new[] { url, range, headers.GetValueOrDefault("if-range", "") })
            : url;
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private Metadata? ReadMetadata(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is < 1 or > MaxMetadataBytes) return null;
        var metadata = JsonData.Deserialize<Metadata>(File.ReadAllText(path));
        if (metadata is null || metadata.Length < 1 || metadata.Status is not (200 or 206) || string.IsNullOrEmpty(metadata.BodyFile)
            || !System.Text.RegularExpressions.Regex.IsMatch(metadata.BodyFile, @"^[A-F0-9]{64}\.[a-f0-9]{32}\.body$")) return null;
        return metadata;
    }

    public async Task<FileEntry?> TryGetFileAsync(string key, CancellationToken token = default)
    {
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var metadata = ReadMetadata(PathFor(key));
            if (metadata is null || metadata.StoredAt + _ttl <= _clock.GetUtcNow()) return null;
            var path = System.IO.Path.Combine(Root, metadata.BodyFile);
            if (!File.Exists(path) || new FileInfo(path).Length != metadata.Length) return null;
            var headers = new Dictionary<string, string>(metadata.Headers);
            var residentAge = Math.Max(0, (long)(_clock.GetUtcNow() - metadata.StoredAt).TotalSeconds);
            headers["age"] = residentAge.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_leaseSync) _readers[path] = _readers.GetValueOrDefault(path) + 1;
            return new(path, metadata.ContentType, headers, metadata.Status, () => Release(path));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存读取失败"); return null; }
        finally { _writer.Release(); }
    }

    // Compatibility helper for diagnostics/tests; the browser path uses a leased disk file.
    public async Task<Entry?> TryGetAsync(string key, CancellationToken token = default)
    {
        using var entry = await TryGetFileAsync(key, token).ConfigureAwait(false);
        if (entry is null) return null;
        return new(await File.ReadAllBytesAsync(entry.Path, token).ConfigureAwait(false), entry.ContentType, entry.Headers, entry.Status);
    }

    public Task StoreAsync(string key, string contentType, Dictionary<string, string> headers, byte[] body,
        CancellationToken token = default, int status = 200)
        => StoreBytesAsync(key, contentType, headers, body, token, status);
    private async Task StoreBytesAsync(string key, string type, Dictionary<string, string> headers, byte[] body,
        CancellationToken token, int status)
    {
        using var stream = new MemoryStream(body, writable: false);
        await StoreStreamAsync(key, type, headers, stream, token, status).ConfigureAwait(false);
    }

    public async Task StoreStreamAsync(string key, string contentType, Dictionary<string, string> headers, Stream body,
        CancellationToken token = default, int status = 200)
    {
        if (status is not (200 or 206)
            || contentType.Split(';', 2)[0].Trim().Equals("image/gif", StringComparison.OrdinalIgnoreCase)) return;
        var storedAt = _clock.GetUtcNow();
        var expiresAt = storedAt + _ttl;
        Directory.CreateDirectory(Root);
        var path = PathFor(key);
        var bodyPath = System.IO.Path.Combine(Root, Digest(key) + "." + Guid.NewGuid().ToString("N") + ".body");
        var temp = bodyPath + ".tmp";
        var metadataTemp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var published = false;
        try
        {
            long length;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var prefix = new byte[6];
                var count = 0;
                while (count < prefix.Length)
                {
                    var read = await body.ReadAsync(prefix.AsMemory(count), token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0 || (count == 6 && (prefix.AsSpan().SequenceEqual("GIF87a"u8) || prefix.AsSpan().SequenceEqual("GIF89a"u8)))) return;
                await stream.WriteAsync(prefix.AsMemory(0, count), token).ConfigureAwait(false);
                await body.CopyToAsync(stream, 65536, token).ConfigureAwait(false);
                length = stream.Length;
            }
            var cachedHeaders = new Dictionary<string, string>(headers);
            cachedHeaders.Remove("set-cookie"); // Reusing a body must not copy response cookies into another UV.
            var metadata = JsonConvert.SerializeObject(new Metadata(System.IO.Path.GetFileName(bodyPath), length, contentType,
                cachedHeaders, storedAt, status));
            if (Encoding.UTF8.GetByteCount(metadata) > MaxMetadataBytes) return;
            await File.WriteAllTextAsync(metadataTemp, metadata, token).ConfigureAwait(false);
            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (_clock.GetUtcNow() >= expiresAt) return;
                EnsureIndex();
                _index.TryGetValue(path, out var old);
                File.Move(temp, bodyPath);
                File.Move(metadataTemp, path, true);
                published = true;
                _index[path] = new(bodyPath, length + Encoding.UTF8.GetByteCount(metadata), DateTime.UtcNow);
                if (old is not null) DeleteBody(old.BodyPath);
                var total = _index.Values.Sum(x => x.Bytes);
                // The total budget is soft for a single oversized entry: never reject a file because of its size.
                foreach (var oldest in _index.Where(x => x.Key != path).OrderBy(x => x.Value.Written).ToArray())
                {
                    if (total <= _maxBytes) break;
                    try { File.Delete(oldest.Key); DeleteBody(oldest.Value.BodyPath); total -= oldest.Value.Bytes; _index.Remove(oldest.Key); }
                    catch (IOException) { }
                }
            }
            finally { _writer.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存写入失败"); }
        finally
        {
            DeleteFile(temp); DeleteFile(metadataTemp);
            if (!published) DeleteFile(bodyPath);
        }
    }

    private void EnsureIndex()
    {
        if (_indexed) return;
        foreach (var path in Directory.EnumerateFiles(Root, "*.meta"))
        {
            try
            {
                var metadata = ReadMetadata(path);
                if (metadata is null) continue;
                var bodyPath = System.IO.Path.Combine(Root, metadata.BodyFile);
                if (File.Exists(bodyPath)) _index[path] = new(bodyPath, new FileInfo(bodyPath).Length + new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        var referenced = _index.Values.Select(x => x.BodyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Root, "*.body"))
            if (!referenced.Contains(path) && System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path),
                @"^[A-F0-9]{64}\.[a-f0-9]{32}\.body$")) DeleteBody(path);
        _indexed = true;
    }
    private void DeleteBody(string path)
    {
        lock (_leaseSync)
        {
            if (_readers.ContainsKey(path)) _pendingDelete.Add(path);
            else DeleteFile(path);
        }
    }
    private void Release(string path)
    {
        lock (_leaseSync)
        {
            if (_readers[path] > 1) _readers[path]--;
            else { _readers.Remove(path); if (_pendingDelete.Remove(path)) DeleteFile(path); }
        }
    }
    private static void DeleteFile(string path)
    { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public bool HasFresh(string key) { using var entry = TryGetFileAsync(key).GetAwaiter().GetResult(); return entry is not null; }
    public bool TryGet(string key, out Entry entry) { entry = TryGetAsync(key).GetAwaiter().GetResult()!; return entry is not null; }
    public void Store(string key, string contentType, Dictionary<string, string> headers, byte[] body)
        => StoreAsync(key, contentType, headers, body).GetAwaiter().GetResult();
    public void Clear()
    {
        _writer.Wait();
        try
        {
            if (Directory.Exists(Root))
            {
                EnsureIndex();
                foreach (var item in _index) { File.Delete(item.Key); DeleteBody(item.Value.BodyPath); }
            }
            _index.Clear(); _indexed = false;
        }
        finally { _writer.Release(); }
    }
    private string PathFor(string key) => System.IO.Path.Combine(Root, Digest(key) + ".meta");
    public void Dispose() { _writer.Dispose(); _captures.Dispose(); }
}
