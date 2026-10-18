namespace TrafficVista.Caching;

internal sealed class MemoryCacheStore(HybridCacheOptions options)
{
    private sealed record Item(string Key, byte[] Body, ResourceMetadata Metadata, DateTimeOffset StoredAt, string? BodyIdentity);
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Item>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<Item> _lru = new();
    private readonly Dictionary<string, (byte[] Body, int References)> _bodies = new(StringComparer.Ordinal);
    private long _bytes;
    internal bool CanHold(long length) => length <= options.MaxMemoryEntryBytes && length <= options.MemoryCapacityBytes
        && options.MemoryCapacityBytes > 0;
    internal (long Bytes, int Count) Size { get { lock (_sync) return (_bytes, _items.Count); } }
    internal CacheReadLease? TryGet(string key)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(key, out var node)) return null;
            if (options.Clock.GetUtcNow() - node.Value.StoredAt >= options.TimeToLive) { Remove(key); return null; }
            _lru.Remove(node); _lru.AddLast(node);
            return new(Copy(node.Value.Metadata), node.Value.StoredAt, node.Value.Body, null);
        }
    }
    internal byte[] Set(string key, byte[] ownedBody, ResourceMetadata metadata, DateTimeOffset storedAt, string? bodyIdentity = null)
    {
        lock (_sync)
        {
            Remove(key);
            if (!CanHold(ownedBody.LongLength) || options.Clock.GetUtcNow() - storedAt >= options.TimeToLive) return ownedBody;
            while (_lru.First is { } oldest && (_bytes + (bodyIdentity is not null && _bodies.ContainsKey(bodyIdentity) ? 0 : ownedBody.LongLength) > options.MemoryCapacityBytes
                || _items.Count >= options.MaxMemoryEntries)) Remove(oldest.Value.Key);
            if (bodyIdentity is not null && _bodies.TryGetValue(bodyIdentity, out var shared))
            { ownedBody = shared.Body; _bodies[bodyIdentity] = (ownedBody, shared.References + 1); }
            else
            {
                _bytes += ownedBody.LongLength;
                if (bodyIdentity is not null) _bodies.Add(bodyIdentity, (ownedBody, 1));
            }
            _items.Add(key, _lru.AddLast(new Item(key, ownedBody, Copy(metadata), storedAt, bodyIdentity)));
            return ownedBody;
        }
    }
    internal void Remove(string key)
    {
        lock (_sync)
            if (_items.Remove(key, out var node))
            {
                _lru.Remove(node);
                if (node.Value.BodyIdentity is { } identity && _bodies[identity].References > 1)
                    _bodies[identity] = (_bodies[identity].Body, _bodies[identity].References - 1);
                else
                { _bytes -= node.Value.Body.LongLength; if (node.Value.BodyIdentity is { } last) _bodies.Remove(last); }
            }
    }
    internal void Clear() { lock (_sync) { _items.Clear(); _lru.Clear(); _bodies.Clear(); _bytes = 0; } }
    internal static ResourceMetadata Copy(ResourceMetadata metadata) => metadata with
    { Headers = CopyHeaders(metadata.Headers) };
    internal static Dictionary<string, string> CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null) foreach (var header in headers) copy[header.Key] = header.Value;
        return copy;
    }
}
