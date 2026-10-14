using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using TrafficVista.Serialization;

namespace TrafficVista.Business;

/// <summary>Bounded cache with headers/body published together by one atomic file replacement.</summary>
public sealed class SharedStaticResourceCache : IDisposable
{
    public sealed record Entry(byte[] Body, string ContentType, Dictionary<string, string> Headers);
    private sealed record Metadata(string ContentType, Dictionary<string, string> Headers, DateTimeOffset ExpiresAt,
        DateTimeOffset StoredAt = default, long InitialAge = 0);
    public const int MaxEntryBytes = 1024 * 1024;
    private const int MaxMetadataBytes = 16384;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly SemaphoreSlim _captures = new(2, 2);
    private readonly Dictionary<string, (long Bytes, DateTime Written)> _index = new();
    private readonly long _maxBytes;
    private readonly TimeSpan _ttl;
    private readonly ILogger _logger;
    private bool _indexed;
    public string Root { get; }
    internal bool TryBeginCapture() => _captures.Wait(0);
    internal void EndCapture() => _captures.Release();

    public SharedStaticResourceCache(BusinessOptions options, ILogger logger)
    {
        Root = Path.Combine(Path.GetFullPath(options.StaticCacheRoot, AppContext.BaseDirectory), "v2");
        _maxBytes = options.StaticCacheMaxMegabytes * 1024L * 1024;
        _ttl = TimeSpan.FromHours(options.StaticCacheTtlHours);
        _logger = logger;
    }

    public static string RequestKey(string url, IReadOnlyDictionary<string, string> headers, string partition)
        => JsonConvert.SerializeObject(new[] { url, partition, headers.GetValueOrDefault("user-agent", ""),
            headers.GetValueOrDefault("accept", ""), headers.GetValueOrDefault("accept-language", ""),
            headers.GetValueOrDefault("accept-encoding", ""),
            headers.GetValueOrDefault("origin", "") });

    public static TimeSpan? PublicLifetime(IReadOnlyDictionary<string, string> request, IReadOnlyDictionary<string, string> response)
    {
        if (request.ContainsKey("cookie") || request.ContainsKey("authorization") || request.ContainsKey("range")
            || response.ContainsKey("set-cookie")) return null;
        var directives = response.GetValueOrDefault("cache-control", "").Split(',').Select(x => x.Trim()).ToArray();
        if (!directives.Contains("public", StringComparer.OrdinalIgnoreCase)
            || directives.Any(x => new[] { "private", "no-store", "no-cache" }.Contains(
                x.Split('=', 2)[0], StringComparer.OrdinalIgnoreCase))) return null;
        var allowed = new[] { "user-agent", "accept", "accept-language", "origin", "accept-encoding" };
        if (response.GetValueOrDefault("vary", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(x => !allowed.Contains(x.Trim(), StringComparer.OrdinalIgnoreCase))) return null;
        var ages = directives.Where(x => x.StartsWith("s-maxage=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ages.Length == 0) ages = directives.Where(x => x.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ages.Length != 1 || !long.TryParse(ages[0][(ages[0].IndexOf('=') + 1)..].Trim('"'), out var seconds) || seconds <= 0) return null;
        seconds -= ResponseAge(response);
        return seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, 365 * 86400)) : null;
    }

    private static long ResponseAge(IReadOnlyDictionary<string, string> headers)
    {
        var age = long.TryParse(headers.GetValueOrDefault("age"), out var value) ? Math.Max(0, value) : 0;
        if (DateTimeOffset.TryParse(headers.GetValueOrDefault("date"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            age = Math.Max(age, Math.Max(0, (long)(DateTimeOffset.UtcNow - date).TotalSeconds));
        return age;
    }

    public async Task<Entry?> TryGetAsync(string key, CancellationToken token = default)
    {
        try
        {
            await using var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            if (stream.Length < 5 || stream.Length > MaxEntryBytes + MaxMetadataBytes + 4) return null;
            var prefix = new byte[4];
            await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is < 1 or > MaxMetadataBytes || stream.Length - length - 4 is < 1 or > MaxEntryBytes) return null;
            var metadataBytes = new byte[length];
            await stream.ReadExactlyAsync(metadataBytes, token).ConfigureAwait(false);
            var metadata = JsonData.Deserialize<Metadata>(Encoding.UTF8.GetString(metadataBytes));
            if (metadata is null || metadata.StoredAt == default || metadata.ExpiresAt <= DateTimeOffset.UtcNow) return null;
            var body = new byte[(int)(stream.Length - length - 4)];
            await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(metadata.Headers);
            var residentAge = Math.Max(0, (long)(DateTimeOffset.UtcNow - metadata.StoredAt).TotalSeconds);
            headers["age"] = (Math.Min(long.MaxValue - residentAge, metadata.InitialAge) + residentAge)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new Entry(body, metadata.ContentType, headers);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存读取失败"); return null; }
    }

    public async Task StoreAsync(string key, string contentType, Dictionary<string, string> headers,
        byte[] body, TimeSpan lifetime, CancellationToken token = default)
    {
        if (body.Length is 0 or > MaxEntryBytes || lifetime <= TimeSpan.Zero) return;
        var storedAt = DateTimeOffset.UtcNow;
        var expiresAt = storedAt + (lifetime < _ttl ? lifetime : _ttl);
        var initialAge = ResponseAge(headers);
        await _writer.WaitAsync(token).ConfigureAwait(false);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Root);
            if (DateTimeOffset.UtcNow >= expiresAt) return;
            if (!_indexed)
            {
                // v1 ignored response policy and cannot be reused safely. Remove only its known cache filenames.
                var previousRoot = Path.GetDirectoryName(Root)!;
                foreach (var file in Directory.EnumerateFiles(previousRoot))
                    if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^[a-fA-F0-9]{64}\.(bin|meta)$")
                        && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    { try { File.Delete(file); } catch (IOException) { } }
                foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*.entry"))
                    _index[file.FullName] = (file.Length, file.LastWriteTimeUtc);
                _indexed = true;
            }
            var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Metadata(contentType, headers,
                expiresAt, storedAt, initialAge)));
            if (metadata.Length > MaxMetadataBytes) return;
            var path = PathFor(key);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, metadata.Length);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(prefix, token).ConfigureAwait(false);
                await stream.WriteAsync(metadata, token).ConfigureAwait(false);
                await stream.WriteAsync(body, token).ConfigureAwait(false);
            }
            File.Move(temp, path, true);
            _index[path] = (4 + metadata.Length + body.LongLength, DateTime.UtcNow);
            var total = _index.Values.Sum(x => x.Bytes);
            foreach (var oldest in _index.OrderBy(x => x.Value.Written).ToArray())
            {
                if (total <= _maxBytes) break;
                try { File.Delete(oldest.Key); total -= oldest.Value.Bytes; _index.Remove(oldest.Key); }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.Debug(ex, "静态缓存写入失败"); }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } }
            _writer.Release();
        }
    }

    public bool HasFresh(string key) => TryGetAsync(key).GetAwaiter().GetResult() is not null;
    public bool TryGet(string key, out Entry entry) { entry = TryGetAsync(key).GetAwaiter().GetResult()!; return entry is not null; }
    public void Store(string key, string contentType, Dictionary<string, string> headers, byte[] body)
        => StoreAsync(key, contentType, headers, body, _ttl).GetAwaiter().GetResult();
    public void Clear()
    {
        _writer.Wait();
        try
        {
            if (Directory.Exists(Root)) foreach (var path in Directory.EnumerateFiles(Root, "*.entry")) File.Delete(path);
            _index.Clear(); _indexed = false;
        }
        finally { _writer.Release(); }
    }
    private string PathFor(string key) => Path.Combine(Root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".entry");
    public void Dispose() { _writer.Dispose(); _captures.Dispose(); }
}
