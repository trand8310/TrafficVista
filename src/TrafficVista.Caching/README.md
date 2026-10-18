# TrafficVista.Caching

独立的 .NET 8 资源缓存库，支持内存热点缓存、磁盘持久化和按键合并并发获取。无 Playwright、WinForms、Serilog 或第三方 NuGet 依赖。

## 项目引用

```xml
<ProjectReference Include="../TrafficVista.Caching/TrafficVista.Caching.csproj" />
```

也可引用构建后的 `TrafficVista.Caching.dll`，或安装本地 `TrafficVista.Caching.1.0.0.nupkg`。目标项目需支持 .NET 8。

## 基本使用

```csharp
using TrafficVista.Caching;

await using var cache = new HybridResourceCache(new HybridCacheOptions
{
    DirectoryPath = Path.Combine(AppContext.BaseDirectory, "resource-cache"),
    MemoryCapacityBytes = 64L * 1024 * 1024,
    MaxMemoryEntryBytes = 4L * 1024 * 1024,
    MaxMemoryEntries = 4096,
    DiskCapacityBytes = 512L * 1024 * 1024,
    TimeToLive = TimeSpan.FromHours(24),
    WriteQueueCapacity = 32,
    DiskConcurrency = 2
});

using var resource = await cache.GetOrCreateAsync("resource-key", async cancellationToken =>
{
    // 获取方式由调用项目决定，可替换为HTTP、文件、数据库等。
    var bytes = await File.ReadAllBytesAsync("resource.bin", cancellationToken);
    return new ResourceContent(bytes, new ResourceMetadata("application/octet-stream"));
});
if (resource is not null)
{
    var bytes = await resource.ReadBodyAsync();
    // 使用bytes；读取结束后释放resource租约。
}
```

同键并发只执行一个factory，不同键可以并行。取消某个调用者仅取消其等待，其他调用者继续复用获取结果。factory接收缓存生命周期与超时令牌，默认超时60秒，需响应取消。factory抛出的异常会传递给同批等待者；之后可重新尝试。factory返回null表示不缓存。

## 由浏览器等外部系统负责下载

Playwright仍由项目自身引用。使用协调器获取所有者票据，浏览器第一个请求继续使用原连接、代理和Cookie，其响应完成后发布缓存；其他请求等待后复用。

```csharp
using var access = await cache.Coordinator.AcquireAsync(key, cancellationToken);
if (access.Hit is { } hit)
{
    // hit.Body为内存正文；hit.FilePath为大资源的磁盘路径。
    // 使用结束后释放access，磁盘租约随之释放。
}
else if (access.IsOwner)
{
    // 使用已有连接下载资源，得到body与metadata。
    await access.StoreAsync(new ResourceContent(body, metadata), cancellationToken);
    // Dispose票据才通知其他等待者读取已发布的数据。
}
else
{
    // 所有者放弃了缓存，调用者可自行回源。
}
```

异步响应回调场景可将所有者票据转交回调；在响应成功、失败、取消或页面关闭的所有路径都须Dispose。取消一个等待者不会释放另一个请求的所有者票据。过滤规则（如GIF、视频Range、响应头与缓存键）由引用项目决定。

## 存储、容量与生命周期

正文去重由调用者显式设置 `ResourceMetadata.DeduplicateBody = true`。库对完整正文计算SHA-256，相同内容共用 `js-<SHA256>.body` 文件和驻留内存正文；各键的响应头、状态和写入时间独立保留。内存/磁盘容量按共享正文实际占用计数，条目数仍为键数量。覆盖、淘汰、清空通过引用与读租约保护其他键。新键仍需先获取正文，不会省略首次下载；已有v4文件按原格式读取，新写入才使用去重格式。HTTP筛选由主项目负责：仅GET且URL路径以`.js`结尾（忽略大小写），查询参数继续参与缓存键；非JS文件和POST不启用此标记。

- 内存按LRU淘汰，限制正文总字节数、单资源大小和条目数量。较大资源继续缓存到磁盘，不强制读进内存。预算不包含调用者缓冲区及已经交给调用者、仍在使用的租约正文。
- `StoreAsync`、`StoreStreamAsync`在磁盘原子发布完成后返回。调用者输入缓冲区/流在该await完成前应保持可读且不修改。
- 写入队列存放暂存文件引用，不堆积大正文数组；队列名额在创建暂存文件前取得，限制并发暂存数量。队列满时异步等待。
- 磁盘预算淘汰较旧条目；一个超过预算的完整新文件仍保留，磁盘预算不计正在使用的退休正文或暂存文件。正文与元数据分开原子发布。
- 命中不延长TTL。修改TTL后重启，旧文件按原始写入时刻与新时长判断。可读取TrafficVista已有v4格式。
- 读租约保护活跃文件，覆盖/清空后等最后一位读者释放再删除。清空后旧的暂存写入、旧所有者票据不会重新发布数据。
- 同一应用、同一目录共享一个缓存实例及协调器；不提供跨进程缓存一致性或跨进程请求合并。
- 使用`await using`或`DisposeAsync`关闭。停止接收新操作、取消factory与未入队操作，等待已接收写入及清理退出；外部下载票据也会被释放。

`Statistics`显示内存/磁盘命中、未命中、回源票据、合并等待、容量和待写入数量；命中/未命中是查询次数（包括协调器内部复查），不是浏览器HTTP请求次数。`ActiveOriginFills`和`WaitingRequests`是当前协调器回源票据数和正在等待回源完成的请求数，完成、失败或取消后下降；`OriginFills`和`CoalescedRequests`仍为应用生命周期累计次数。`OnError`可接入任意日志系统，诊断回调异常不会破坏缓存状态。

## 构建、测试与打包

从仓库根目录执行：

```powershell
dotnet build src/TrafficVista.Caching/TrafficVista.Caching.csproj
dotnet run --project src/TrafficVista.Caching.Tests/TrafficVista.Caching.Tests.csproj
dotnet pack src/TrafficVista.Caching/TrafficVista.Caching.csproj -c Release -o src/Build/cache-library
```

测试程序仅使用本地文件和模拟获取器，无第三方测试框架依赖。TrafficVista的`--static-cache-test`另覆盖真实Chrome内存命中、同一冷资源的并发合并、GIF排除、gzip、视频Range及文件租约兼容性。
