namespace TrafficVista.Caching;

public sealed record HybridCacheOptions
{
    public required string DirectoryPath { get; init; }
    public long MemoryCapacityBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxMemoryEntryBytes { get; init; } = 4L * 1024 * 1024;
    public int MaxMemoryEntries { get; init; } = 4096;
    public long DiskCapacityBytes { get; init; } = 512L * 1024 * 1024;
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(24);
    public int WriteQueueCapacity { get; init; } = 32;
    public int DiskConcurrency { get; init; } = 2;
    public TimeSpan FactoryTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public Action<Exception>? OnError { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfNegative(MemoryCapacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMemoryEntryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMemoryEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DiskCapacityBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DiskConcurrency, 1);
        if (TimeToLive <= TimeSpan.Zero || FactoryTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeToLive));
        if (FactoryTimeout.TotalMilliseconds > uint.MaxValue - 1 || WriteQueueCapacity == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(FactoryTimeout));
        ArgumentNullException.ThrowIfNull(Clock);
    }
}

public sealed record ResourceMetadata(string ContentType, IReadOnlyDictionary<string, string>? Headers = null, int StatusCode = 200)
{
    public bool DeduplicateBody { get; init; }
}
public sealed record ResourceContent(ReadOnlyMemory<byte> Body, ResourceMetadata Metadata);
public enum CacheTier { Memory, Disk }

/// <summary>Dispose after the consumer finishes reading. Large bodies retain a leased file.</summary>
public sealed class CacheReadLease : IDisposable
{
    private Action? _release;
    internal CacheReadLease(ResourceMetadata metadata, DateTimeOffset storedAt, ReadOnlyMemory<byte> body,
        string? path, Action? release = null)
    { Metadata = metadata; StoredAt = storedAt; Body = body; FilePath = path; _release = release; }
    public ResourceMetadata Metadata { get; }
    public DateTimeOffset StoredAt { get; }
    public ReadOnlyMemory<byte> Body { get; }
    public string? FilePath { get; }
    public CacheTier Tier => FilePath is null ? CacheTier.Memory : CacheTier.Disk;
    public Task<byte[]> ReadBodyAsync(CancellationToken token = default)
        => FilePath is { } path ? File.ReadAllBytesAsync(path, token) : Task.FromResult(Body.ToArray());
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed record CacheStatistics(long MemoryHits, long DiskHits, long Misses, long OriginFills,
    long CoalescedRequests, long MemoryBytes, int MemoryEntries, long DiskBytes, int DiskEntries,
    int PendingWrites, long Errors)
{
    public int QueuedWrites { get; init; }
    public int ActiveOriginFills { get; init; }
    public int WaitingRequests { get; init; }
}
