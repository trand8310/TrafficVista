using System.Security.Cryptography;
using System.Text;
using TrafficVista.Caching;

var root = Path.Combine(Path.GetTempPath(), "TrafficVista.CacheTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var metadata = new ResourceMetadata("application/octet-stream", new Dictionary<string, string> { ["x-test"] = "original" });
HybridCacheOptions Options(string name) => new() { DirectoryPath = Path.Combine(root, name), MemoryCapacityBytes = 64,
    MaxMemoryEntryBytes = 32, DiskCapacityBytes = 1024 * 1024, WriteQueueCapacity = 2 };
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task Until(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(5, deadline.Token);
}
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
async Task Test(string name, Func<Task> test)
{
    await test().WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("PASS " + name);
}

await Test("opt-in body deduplication, independent metadata, overwrite, restart and leased clear", async () =>
{
    var options = Options("dedup") with { MemoryCapacityBytes = 8192, MaxMemoryEntryBytes = 8192 };
    var content = Enumerable.Repeat((byte)42, 1024).ToArray();
    var shared = metadata with { DeduplicateBody = true };
    await using (var cache = new HybridResourceCache(options))
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => cache.StoreAsync("script.js?r=" + index,
            shared with { Headers = new Dictionary<string, string> { ["x-url"] = index.ToString() } }, content)));
        Check(Directory.GetFiles(options.DirectoryPath, "*.body").Length == 1, "concurrent equal bodies share one file");
        Check(cache.Statistics.MemoryEntries == 20 && cache.Statistics.MemoryBytes == 1024, "memory budget counts shared body once");
        using var a = await cache.TryGetAsync("script.js?r=0");
        using var b = await cache.TryGetAsync("script.js?r=1");
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(a!.Body, out var arrayA);
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(b!.Body, out var arrayB);
        Check(ReferenceEquals(arrayA.Array, arrayB.Array) && a.Metadata.Headers!["x-url"] == "0"
            && b.Metadata.Headers!["x-url"] == "1", "memory body identity and independent URL headers");
        var changed = new byte[1024];
        await cache.StoreAsync("script.js?r=0", shared, changed);
        using var original = await cache.TryGetFileAsync("script.js?r=1");
        using var overwritten = await cache.TryGetAsync("script.js?r=0");
        Check((await original!.ReadBodyAsync())[0] == 42 && overwritten!.Body.Span[0] == 0, "overwrite preserves other aliases");
        Check(Directory.GetFiles(options.DirectoryPath, "*.body").Length == 2, "different body hash stores separately");
        await cache.StoreAsync("plain.css", metadata, content);
        Check(Directory.GetFiles(options.DirectoryPath, "*.body").Length == 3, "non-opt-in equal body remains separate");
    }
    await using (var restarted = new HybridResourceCache(options))
    {
        using var first = await restarted.TryGetAsync("script.js?r=1");
        using var second = await restarted.TryGetAsync("script.js?r=2");
        Check(restarted.Statistics.MemoryBytes == 1024 && second!.Metadata.Headers!["x-url"] == "2", "restart reconstructs sharing and metadata");
        using var lease = await restarted.TryGetFileAsync("script.js?r=1");
        var path = lease!.FilePath!;
        await restarted.ClearAsync();
        Check(File.Exists(path) && (await lease.ReadBodyAsync())[0] == 42 && restarted.Statistics.DiskBytes == 0,
            "last alias removal protects active lease and clears indexed capacity");
        lease.Dispose();
        Check(!File.Exists(path) && Directory.GetFiles(options.DirectoryPath, "*.body").Length == 0, "last reader releases shared file");
    }
});

await Test("shared body TTL, eviction and failed publication preserve other references", async () =>
{
    var clock = new Clock();
    var options = Options("dedup-lifecycle") with { Clock = clock, TimeToLive = TimeSpan.FromHours(2),
        DiskCapacityBytes = 1500, MemoryCapacityBytes = 0 };
    var shared = metadata with { DeduplicateBody = true };
    await using var cache = new HybridResourceCache(options);
    await cache.StoreAsync("a", shared, new byte[1000]);
    clock.Advance(TimeSpan.FromHours(1));
    await cache.StoreAsync("b", shared, new byte[1000]);
    using (var b = await cache.TryGetFileAsync("b"))
    {
        Check(b is not null && File.Exists(b.FilePath), "evicting older URL does not delete shared body");
        clock.Advance(TimeSpan.FromHours(1));
        Check(await cache.TryGetAsync("a") is null, "old URL independently expires or is evicted");
        using var fresh = await cache.TryGetAsync("b");
        Check(fresh is not null, "new alias has independent TTL");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("failed-alias")));
        Directory.CreateDirectory(Path.Combine(options.DirectoryPath, digest + ".meta"));
        try { await cache.StoreAsync("failed-alias", shared, new byte[1000]); throw new InvalidOperationException("expected metadata failure"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Check(File.Exists(b!.FilePath) && (await b.ReadBodyAsync()).Length == 1000, "failed alias cannot delete shared body");
        await cache.ClearAsync();
        // Republishing identical content while a retired lease exists must cancel its pending deletion.
        await cache.StoreAsync("new-alias", shared, new byte[1000]);
        b.Dispose();
        using var republished = await cache.TryGetAsync("new-alias");
        Check(republished is not null, "reusing leased retired body survives old reader release");
    }
});

await Test("coordination snapshot falls after completion, cancellation, failure and clear", async () =>
{
    await using var cache = new HybridResourceCache(Options("snapshot"));
    using var owner = await cache.Coordinator.AcquireAsync("key");
    using var canceled = new CancellationTokenSource();
    var waiter = cache.Coordinator.AcquireAsync("key");
    var canceledWaiter = cache.Coordinator.AcquireAsync("key", canceled.Token);
    await Until(() => cache.Statistics.WaitingRequests == 2);
    Check(cache.Statistics.ActiveOriginFills == 1, "one current origin");
    canceled.Cancel();
    await Throws<OperationCanceledException>(async () => { using var ignored = await canceledWaiter; });
    Check(cache.Statistics.WaitingRequests == 1 && cache.Statistics.ActiveOriginFills == 1, "cancel removes only waiter");
    await owner.StoreAsync(new ResourceContent(new byte[] { 1 }, metadata));
    owner.Dispose();
    using var hit = await waiter;
    Check(hit.Hit is not null && cache.Statistics.WaitingRequests == 0 && cache.Statistics.ActiveOriginFills == 0,
        "completion drains snapshot");
    Check(cache.Statistics.CoalescedRequests == 2, "cumulative count retained");

    using var failed = await cache.Coordinator.AcquireAsync("failed");
    var failedWaiter = cache.Coordinator.AcquireAsync("failed");
    await Until(() => cache.Statistics.WaitingRequests == 1);
    failed.Fail(new IOException("origin failure"));
    await Throws<IOException>(async () => { using var ignored = await failedWaiter; });
    Check(cache.Statistics.ActiveOriginFills == 0 && cache.Statistics.WaitingRequests == 0, "failure drains snapshot");

    using var cleared = await cache.Coordinator.AcquireAsync("cleared");
    var clearedWaiter = cache.Coordinator.AcquireAsync("cleared");
    await Until(() => cache.Statistics.WaitingRequests == 1);
    await cache.ClearAsync();
    await Throws<OperationCanceledException>(async () => { using var ignored = await clearedWaiter; });
    cleared.Dispose();
    Check(cache.Statistics.ActiveOriginFills == 0 && cache.Statistics.WaitingRequests == 0, "clear drains without double decrement");
});

await Test("memory LRU, buffer/header isolation and disk promotion", async () =>
{
    await using var cache = new HybridResourceCache(Options("lru"));
    var bytes = Enumerable.Repeat((byte)1, 30).ToArray();
    await cache.StoreAsync("a", metadata, bytes); bytes[0] = 99;
    await cache.StoreAsync("b", metadata, new byte[30]);
    using (var a = await cache.TryGetAsync("a"))
    {
        Check(a?.Tier == CacheTier.Memory && a.Body.Span[0] == 1, "owned memory data");
        ((Dictionary<string, string>)a!.Metadata.Headers!)["x-test"] = "changed";
    }
    await cache.StoreAsync("c", metadata, new byte[30]);
    var before = cache.Statistics.DiskHits;
    using var b = await cache.TryGetAsync("b");
    Check(b is not null && cache.Statistics.DiskHits == before + 1 && cache.Statistics.MemoryBytes <= 64, "LRU evicts b and promotes disk hit");
    using var aAgain = await cache.TryGetAsync("a");
    Check(aAgain!.Metadata.Headers!["x-test"] == "original", "headers do not mutate stored value");
});

await Test("large file leases survive overwrite and clear", async () =>
{
    await using var cache = new HybridResourceCache(Options("leases") with { DiskCapacityBytes = 100 });
    await cache.StoreAsync("large", metadata, new byte[500]);
    using var old = await cache.TryGetAsync("large");
    Check(old?.Tier == CacheTier.Disk && cache.Statistics.MemoryBytes == 0, "large body bypasses memory and soft disk budget");
    var oldPath = old!.FilePath!;
    await cache.StoreAsync("large", metadata, new byte[3]);
    Check(File.Exists(oldPath), "old body retained during lease");
    old.Dispose(); Check(!File.Exists(oldPath), "retired body removed after lease");
    using var current = await cache.TryGetFileAsync("large");
    var currentPath = current!.FilePath!;
    await cache.ClearAsync();
    Check(File.Exists(currentPath) && await cache.TryGetAsync("large") is null, "clear protects reader and removes lookup");
    current.Dispose(); Check(!File.Exists(currentPath), "clear retirement completes");
});

await Test("empty keys/bodies, header normalization and bounded entry count", async () =>
{
    await using var cache = new HybridResourceCache(Options("empty") with { MaxMemoryEntries = 2 });
    var headers = new Dictionary<string, string> { ["Content-Type"] = "first", ["content-type"] = "last" };
    await cache.StoreAsync("", new ResourceMetadata("text/plain", headers), ReadOnlyMemory<byte>.Empty);
    using var empty = await cache.TryGetAsync("");
    Check(empty is not null && empty.Body.Length == 0 && empty.Metadata.Headers!["CONTENT-TYPE"] == "last", "empty resources and case-insensitive headers");
    for (var i = 0; i < 10; i++) await cache.StoreAsync(i.ToString(), metadata, ReadOnlyMemory<byte>.Empty);
    Check(cache.Statistics.MemoryEntries <= 2 && cache.Statistics.MemoryBytes == 0, "zero-length bodies cannot grow memory index without bound");
});

await Test("restart, absolute TTL and backward-compatible v4 metadata", async () =>
{
    var clock = new Clock();
    var options = Options("restart") with { Clock = clock, TimeToLive = TimeSpan.FromHours(2) };
    await using (var cache = new HybridResourceCache(options)) await cache.StoreAsync("key", metadata, new byte[] { 1, 2, 3 });
    // Confirm the format can be generated independently by the previous application's serializer.
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("legacy")));
    var bodyFile = digest + "." + Guid.NewGuid().ToString("N") + ".body";
    await File.WriteAllBytesAsync(Path.Combine(options.DirectoryPath, bodyFile), new byte[] { 7 });
    await File.WriteAllTextAsync(Path.Combine(options.DirectoryPath, digest + ".meta"),
        $$"""{"BodyFile":"{{bodyFile}}","Length":1,"ContentType":"text/plain","Headers":{},"StoredAt":"{{clock.GetUtcNow():O}}","Status":200}""");
    await using var restarted = new HybridResourceCache(options);
    using var hit = await restarted.TryGetAsync("key");
    using var legacy = await restarted.TryGetAsync("legacy");
    Check(hit is not null && legacy?.Body.Span[0] == 7 && restarted.Statistics.DiskHits == 2, "restart/v4 reads");
    clock.Advance(TimeSpan.FromHours(1));
    using var warm = await restarted.TryGetAsync("key");
    clock.Advance(TimeSpan.FromHours(1));
    Check(await restarted.TryGetAsync("key") is null, "hits do not slide TTL");
    await using var changedTtl = new HybridResourceCache(options with { TimeToLive = TimeSpan.FromHours(3) });
    using var fresh = await changedTtl.TryGetAsync("key");
    Check(fresh is not null, "new configured TTL applies to existing files");
});

await Test("50 shared fills; canceling leader's wait does not cancel shared factory", async () =>
{
    await using var cache = new HybridResourceCache(Options("flight"));
    var entered = Signal(); var release = Signal(); var calls = 0;
    async Task<ResourceContent?> Factory(CancellationToken token)
    { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.WaitAsync(token); return new(new byte[] { 42 }, metadata); }
    using var stop = new CancellationTokenSource();
    var leader = cache.GetOrCreateAsync("key", Factory, stop.Token);
    await entered.Task;
    var followers = Enumerable.Range(0, 50).Select(_ => cache.GetOrCreateAsync("key", Factory)).ToArray();
    try
    {
        await Until(() => cache.Statistics.CoalescedRequests == 50);
        stop.Cancel(); await Throws<OperationCanceledException>(async () => { using var value = await leader; });
    }
    finally { release.TrySetResult(); }
    var results = await Task.WhenAll(followers);
    Check(calls == 1 && results.All(value => value?.Body.Span[0] == 42), "one factory result for all surviving waiters");
    foreach (var result in results) result?.Dispose();
});

await Test("distinct keys run concurrently and failed fills release waiters", async () =>
{
    await using var cache = new HybridResourceCache(Options("parallel"));
    var entered = 0; var release = Signal();
    var work = Enumerable.Range(0, 8).Select(i => cache.GetOrCreateAsync("key-" + i, async token =>
    { Interlocked.Increment(ref entered); await release.Task.WaitAsync(token); return new(new byte[] { 1 }, metadata); })).ToArray();
    try { await Until(() => entered == 8); } finally { release.TrySetResult(); }
    foreach (var value in await Task.WhenAll(work)) value?.Dispose();
    var failEntered = Signal(); var failRelease = Signal();
    async Task<ResourceContent?> Fail(CancellationToken token)
    { failEntered.TrySetResult(); await failRelease.Task.WaitAsync(token); throw new IOException("origin fixture"); }
    var first = cache.GetOrCreateAsync("fail", Fail);
    await failEntered.Task;
    var second = cache.GetOrCreateAsync("fail", Fail);
    await Until(() => cache.Statistics.CoalescedRequests >= 1); failRelease.SetResult();
    await Throws<IOException>(async () => { using var value = await first; });
    await Throws<IOException>(async () => { using var value = await second; });
    using var retried = await cache.GetOrCreateAsync("fail", _ => Task.FromResult<ResourceContent?>(new(new byte[] { 2 }, metadata)));
    Check(retried?.Body.Span[0] == 2, "failure does not poison key");
});

await Test("external browser owner tickets and waiter cancellation", async () =>
{
    await using var cache = new HybridResourceCache(Options("external"));
    using var owner = await cache.Coordinator.AcquireAsync("key");
    Check(owner.IsOwner, "first browser owns origin");
    using var stop = new CancellationTokenSource();
    var canceled = cache.Coordinator.AcquireAsync("key", stop.Token);
    var next = cache.Coordinator.AcquireAsync("key");
    await Until(() => cache.Statistics.CoalescedRequests == 2); stop.Cancel();
    await Throws<OperationCanceledException>(async () => { using var result = await canceled; });
    await owner.StoreAsync(new(new byte[] { 3 }, metadata)); owner.Dispose();
    using var following = await next;
    Check(!following.IsOwner && following.Hit?.Body.Span[0] == 3, "other browsers reuse owner's publication");
    using var abandoned = await cache.Coordinator.AcquireAsync("abandoned");
    var waiter = cache.Coordinator.AcquireAsync("abandoned");
    abandoned.Dispose();
    using var miss = await waiter;
    Check(miss.Hit is null && !miss.IsOwner, "abandoned request releases all waiters for fallback");
});

await Test("clear rejects previously staged writes and old owner publications", async () =>
{
    await using var cache = new HybridResourceCache(Options("epoch"));
    var entered = Signal(); var release = Signal();
    using var input = new SlowStream(entered, release);
    var write = cache.StoreStreamAsync("slow", metadata, input);
    await entered.Task;
    try { await cache.ClearAsync(); } finally { release.TrySetResult(); }
    await write;
    Check(await cache.TryGetAsync("slow") is null, "staging before clear cannot repopulate cache");
    using var oldOwner = await cache.Coordinator.AcquireAsync("owner");
    await cache.ClearAsync();
    using var newOwner = await cache.Coordinator.AcquireAsync("owner");
    await oldOwner.StoreAsync(new(new byte[] { 1 }, metadata)); oldOwner.Dispose();
    Check(await cache.TryGetAsync("owner") is null && newOwner.IsOwner, "stale ticket neither publishes nor removes new owner");
    await newOwner.StoreAsync(new(new byte[] { 2 }, metadata)); newOwner.Dispose();
    using var current = await cache.TryGetAsync("owner");
    Check(current?.Body.Span[0] == 2, "new generation owner publishes");
});

await Test("canceled stores and failed atomic publication leave no partial bodies", async () =>
{
    var options = Options("failures");
    await using var cache = new HybridResourceCache(options with { OnError = _ => throw new Exception("diagnostic callback") });
    using var stop = new CancellationTokenSource(); stop.Cancel();
    await Throws<OperationCanceledException>(() => cache.StoreAsync("cancel", metadata, new byte[] { 1 }, stop.Token));
    using var ready = await cache.TryGetAsync("cancel");
    Check(ready is null, "canceled write has no entry");
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("broken")));
    Directory.CreateDirectory(Path.Combine(options.DirectoryPath, digest + ".meta"));
    var failed = false;
    try { await cache.StoreAsync("broken", metadata, new byte[] { 1 }); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
    Check(failed, "filesystem publish error is surfaced");
    await Until(() => cache.Statistics.PendingWrites == 0);
    Check(!Directory.EnumerateFiles(options.DirectoryPath, "*.tmp").Any()
        && !Directory.EnumerateFiles(options.DirectoryPath, "*.body").Any(), "failed publish cleans staged/body files");
});

await Test("bounded staging slots and disposal cancel pending factories", async () =>
{
    var options = Options("bounded") with { WriteQueueCapacity = 1 };
    await using (var cache = new HybridResourceCache(options))
    {
        var entered = 0; var release = Signal();
        var streams = Enumerable.Range(0, 10).Select(_ => new SlowStream(null, release, () => Interlocked.Increment(ref entered))).ToArray();
        var writes = streams.Select((stream, i) => cache.StoreStreamAsync("key-" + i, metadata, stream)).ToArray();
        await Until(() => entered == 2); await Task.Delay(40);
        Check(entered == 2 && cache.Statistics.QueuedWrites <= 1, "only queue capacity plus active publication may stage bodies");
        release.SetResult(); await Task.WhenAll(writes);
        foreach (var stream in streams) stream.Dispose();
    }
    var closing = new HybridResourceCache(Options("dispose"));
    var factoryEntered = Signal();
    var pending = closing.GetOrCreateAsync("key", async token =>
    { factoryEntered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; });
    await factoryEntered.Task;
    await closing.DisposeAsync();
    await Throws<ObjectDisposedException>(async () => { using var value = await pending; });
    await closing.DisposeAsync();
});

Console.WriteLine("All cache checks passed. Output: " + root);

sealed class Clock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan time) => _now += time;
}
sealed class SlowStream(TaskCompletionSource? entered, TaskCompletionSource release, Action? onEnter = null) : MemoryStream(new byte[] { 1, 2, 3 })
{
    private bool _entered;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (!_entered) { _entered = true; entered?.TrySetResult(); onEnter?.Invoke(); }
        await release.Task.WaitAsync(token);
        return await base.ReadAsync(buffer, token);
    }
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        => CopySlowAsync(destination, token);
    private async Task CopySlowAsync(Stream output, CancellationToken token)
    {
        var buffer = new byte[16]; int read;
        while ((read = await ReadAsync(buffer, token)) > 0) await output.WriteAsync(buffer.AsMemory(0, read), token);
    }
}
