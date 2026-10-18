using Newtonsoft.Json;
using Serilog;
using TrafficVista.Caching;

namespace TrafficVista.Business;

/// <summary>Browser cache policy adapter. Storage and concurrent fills live in TrafficVista.Caching.</summary>
public sealed class SharedStaticResourceCache : IDisposable
{
    public sealed record Entry(byte[] Body, string ContentType, Dictionary<string, string> Headers, int Status = 200);
    public sealed class FileEntry : IDisposable
    {
        private CacheReadLease? _lease;
        public string Path { get; }
        public string ContentType { get; }
        public Dictionary<string, string> Headers { get; }
        public int Status { get; }
        internal FileEntry(CacheReadLease lease, Dictionary<string, string> headers)
        { _lease = lease; Path = lease.FilePath!; ContentType = lease.Metadata.ContentType; Headers = headers; Status = lease.Metadata.StatusCode; }
        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
    private readonly HybridResourceCache _cache;
    private readonly SemaphoreSlim _captures = new(2, 2);
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    public string Root => _cache.DirectoryPath;
    public CacheStatistics Statistics => _cache.Statistics;
    internal Task BeginCaptureAsync(CancellationToken token) => _captures.WaitAsync(token);
    internal void EndCapture() => _captures.Release();

    public SharedStaticResourceCache(BusinessOptions options, ILogger logger, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _cache = new(new HybridCacheOptions
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(options.StaticCacheRoot, AppContext.BaseDirectory), "v4"),
            MemoryCapacityBytes = options.StaticCacheMemoryMegabytes * 1024L * 1024,
            MaxMemoryEntryBytes = options.StaticCacheMaxMemoryEntryMegabytes * 1024L * 1024,
            DiskCapacityBytes = options.StaticCacheMaxMegabytes * 1024L * 1024,
            WriteQueueCapacity = options.StaticCacheWriteQueueCapacity,
            TimeToLive = TimeSpan.FromHours(options.StaticCacheTtlHours), Clock = _clock,
            OnError = ex => logger.Debug(ex, "静态资源缓存存储异常")
        });
    }
    public static string RequestKey(string url, IReadOnlyDictionary<string, string> headers, string partition)
        => headers.TryGetValue("range", out var range) && !string.IsNullOrEmpty(range)
            ? JsonConvert.SerializeObject(new[] { url, range, headers.GetValueOrDefault("if-range", "") }) : url;
    internal Task<CacheAccess> AcquireAsync(string key, CancellationToken token) => _cache.Coordinator.AcquireAsync(key, token);
    internal Task<CacheReadLease?> TryGetResourceAsync(string key, CancellationToken token) => _cache.TryGetAsync(key, token);
    internal Dictionary<string, string> Headers(CacheReadLease lease)
    {
        var headers = lease.Metadata.Headers is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(lease.Metadata.Headers, StringComparer.OrdinalIgnoreCase);
        headers["age"] = Math.Max(0, (long)(_clock.GetUtcNow() - lease.StoredAt).TotalSeconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return headers;
    }
    public async Task<FileEntry?> TryGetFileAsync(string key, CancellationToken token = default)
    {
        try
        {
            var lease = await _cache.TryGetFileAsync(key, token).ConfigureAwait(false);
            return lease is null ? null : new FileEntry(lease, Headers(lease));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存读取失败"); return null; }
    }
    public async Task<Entry?> TryGetAsync(string key, CancellationToken token = default)
    {
        try
        {
            using var entry = await _cache.TryGetAsync(key, token).ConfigureAwait(false);
            return entry is null ? null : new(await entry.ReadBodyAsync(token).ConfigureAwait(false), entry.Metadata.ContentType,
                Headers(entry), entry.Metadata.StatusCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存读取失败"); return null; }
    }
    private static bool Accept(string contentType, int status, ReadOnlySpan<byte> prefix)
        => status is 200 or 206 && !contentType.Split(';', 2)[0].Trim().Equals("image/gif", StringComparison.OrdinalIgnoreCase)
            && !prefix.IsEmpty && !(prefix.Length >= 6 && (prefix[..6].SequenceEqual("GIF87a"u8) || prefix[..6].SequenceEqual("GIF89a"u8)));
    internal static bool ShouldDeduplicateJavaScript(string method, string url)
        => method == "GET" && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    private static ResourceMetadata Metadata(string type, Dictionary<string, string> headers, int status, bool deduplicateBody = false)
    {
        var clean = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        clean.Remove("set-cookie");
        return new(type, clean, status) { DeduplicateBody = deduplicateBody };
    }
    public Task StoreAsync(string key, string contentType, Dictionary<string, string> headers, byte[] body,
        CancellationToken token = default, int status = 200, bool deduplicateBody = false)
        => StoreBytesAsync(key, contentType, headers, body, token, status, null, deduplicateBody);
    internal Task StoreOwnedAsync(CacheAccess owner, string key, string type, Dictionary<string, string> headers,
        byte[] body, CancellationToken token, int status, bool deduplicateBody = false)
        => StoreBytesAsync(key, type, headers, body, token, status, owner, deduplicateBody);
    private async Task StoreBytesAsync(string key, string type, Dictionary<string, string> headers, byte[] body,
        CancellationToken token, int status, CacheAccess? owner, bool deduplicateBody)
    {
        token.ThrowIfCancellationRequested();
        if (!Accept(type, status, body)) return;
        try
        {
            var metadata = Metadata(type, headers, status, deduplicateBody);
            if (owner is null) await _cache.StoreAsync(key, metadata, body, token).ConfigureAwait(false);
            else await owner.StoreAsync(new ResourceContent(body, metadata), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存写入失败"); }
    }
    public async Task StoreStreamAsync(string key, string contentType, Dictionary<string, string> headers, Stream body,
        CancellationToken token = default, int status = 200)
    {
        var prefix = new byte[6]; var count = 0;
        while (count < prefix.Length)
        {
            var read = await body.ReadAsync(prefix.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (!Accept(contentType, status, prefix.AsSpan(0, count))) return;
        using var input = new PrefixStream(prefix.AsMemory(0, count), body);
        try { await _cache.StoreStreamAsync(key, Metadata(contentType, headers, status), input, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存写入失败"); }
    }
    public bool HasFresh(string key) { using var entry = TryGetFileAsync(key).GetAwaiter().GetResult(); return entry is not null; }
    public bool TryGet(string key, out Entry entry) { entry = TryGetAsync(key).GetAwaiter().GetResult()!; return entry is not null; }
    public void Store(string key, string contentType, Dictionary<string, string> headers, byte[] body)
        => StoreAsync(key, contentType, headers, body).GetAwaiter().GetResult();
    public void Clear() => _cache.ClearAsync().GetAwaiter().GetResult();
    public void Dispose() { _cache.Dispose(); _captures.Dispose(); }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream tail) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.Slice(_position, count).CopyTo(buffer); _position += count; return count;
            }
            return await tail.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
