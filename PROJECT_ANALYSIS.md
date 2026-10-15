# TrafficVista 执行与效率分析

> 此文件记录修复前的分析。2026-10-14 的修复、验证与剩余外部依赖见 [改进记录](IMPROVEMENTS.md)，当前执行协议见 [项目说明](src/README.md)。

分析日期：2026-10-14。范围为当前接线的 WinForms 业务链路、运行配置和本地已有日志；本次未修改业务代码，未调用真实任务、代理、设备接口。

## 执行流程

1. `Program.Main` 初始化日志、全局异常处理和 DI；交互启动时先创建共享 IPlaywright，再进入表单消息循环。
2. 启动按钮保存界面设置，冻结本轮 TrafficVistaOptions；MaxConcurrency 同时映射为任务 Worker 数与全局 UV 许可数。
3. 单生产者 GET 任务接口，解析一批任务；Multiple 将批次复制为多个独立消费单元。
4. 任务进入有界队列（运行基础配置容量为 128）；队列满时生产者等待。
5. Worker 取任务，解析 uv/pv/sleep/jstext，创建独立运行目录；代理模式下每任务领取新代理，经多 IpInfo 接口竞速校验出口。
6. 任务按自己的 UvExecutionInterval 派发 UV，并竞争所有任务共享的 UV SemaphoreSlim；每个 UV 获取设备、写快照、等待浏览器启动许可。
7. 每 UV 启动独立 Chrome、Context、Page，注入任务对象；执行 pv 次导航，每次停留同一份随机 dwell，首次额外等待 PageLoadingDelay。
8. 缓存模式对图片/CSS/字体/媒体按 URL 使用共享磁盘缓存；文档与脚本仍请求网络。
9. 保存 storage-state，关闭 Context 与浏览器，写 result.json，释放 UV 许可；所有 UV 结束后任务才返回。
10. 停止取消生产与消费，等待清理；UI 定时采样任务统计，停止等待上限为 30 秒。

当前主链未接入 HighThroughputScheduler、LocalUvTaskConsumer、旧 HttpProxyProvider、任务本地缓存，也没有业务完成回执调用。保留类的能力不能当成线上链路已具备的能力。

## 当前配置及效率判断

运行目录基础配置与用户覆盖合并后：MaxConcurrency=1，UvExecutionInterval=2000ms，TaskFetchInterval=1000ms，PageLoadingDelay=5s，PageLoadingTimeout=30s，Multiple=1，IsHiddenMode=false，IsProxyMode=false，IsCacheMode=false。代理 IpTtl=60s 已保存，但非代理模式下不生效。任务执行超时为 8 小时，浏览器启动并发上限为 4。

因此当前业务 UV 串行，启动并发上限 4 无法提升吞吐；每 UV 都承担 Chrome 冷启动、设备准备、页面导航、5 秒首页等待、停留与关闭的成本。

可用于容量规划的近似式：

`单 UV 耗时 = 设备/许可等待 + 浏览器启动 + pv × (导航 + dwell) + 首次延时 + 状态保存/关闭`

`UV 吞吐 <= UV 并发 / 平均单 UV 耗时`

这是容量估算，不是实测结果。UvExecutionInterval 在每个任务内独立计时，不是跨任务统一的启动限速；启动许可只控制同时启动数量，也不是每秒限速。

当前日志含 7 次 business-test、7 次 net-test 和交互启动。故意失败的自检与业务记录混合，不能用日志错误数计算生产故障率，也不能用其中毫秒级 IpInfo 时间估计真实代理性能。

目录检查：browser-profiles 有 48 个目录、8298 个文件，约 621.18 MiB；145_s1 约 93.16 MiB。存在历史 profile 残留，需要按创建来源和活跃状态核对，当前证据不足以断言是本轮业务执行器泄漏。business/tasks 有 77 个运行目录，当前约 0.09 MiB，但没有保留期限或总量清理策略。

## 优先问题

### P1：代理信息在业务边界丢失

`Business/NetProxyProvider.cs:30` 只传 host:port 与 IpInfo；BusinessProxy 不保留 Protocol、Username、Password、AcquiredAt、ExpiresAt，浏览器固定使用 http。

结果：Net 层支持的认证代理无法在浏览器中完整使用，供应商真实过期时间也无法约束正在运行的任务。`BrowserTaskConsumer.cs:94` 在代理校验和快照完成后才重新计时 IpTtl，且只计界面时长。应保留完整连接信息，以供应商剩余时长与任务允许时长的较小值约束执行；明确界面时长从哪一时刻开始。

### P1：IpInfo 竞速胜出条件过宽

`Net/ProxyIpInfoProvider.cs:103` 成功解析 JObject 即算胜出，随后取消其他请求；`ProxyService` 才检查 ip/query/origin 是否可解析。

若最快接口返回 `{}` 或 JSON 错误对象，较慢接口能返回真实 IP，也会被提前取消；外层把当前代理当瑕疵，重新领取，浪费代理额度与时间。应在每个竞速分支内验证有效出口信息，再选胜出者。

### P1：取消与超时覆盖不完整

`BusinessUvBrowserRunner` 在浏览器 API 调用前后检查 token，但 LaunchAsync、GotoAsync、StorageStateAsync、CloseAsync 本身未关联取消。取消发生在导航期间时，需要等该操作返回或超时，不能保证 IP 到期立即停止；清理也无单独超时策略。页面超时允许配置到 3600 秒，UI 停止等待却只有 30 秒。

`Net/ProxyTransportFactory.cs:50` 使用 ResponseHeadersRead；后续响应流读取只用调用方 token，没有覆盖整段读取的内部超时。若响应头很快、正文持续拖延，配置的 RequestTimeoutSeconds 不能覆盖整次请求，业务可能一直等到外层 8 小时超时。应使用贯穿连接、响应头、正文的 linked CTS，并区分调用取消与内部超时。

### P1：共享缓存的正确性与内存风险

`SharedStaticResourceCache` 只以 URL 为键，忽略 Vary、Cache-Control、Cookie/授权/设备/地区差异。跨设备、跨代理可能返回错误的内容。应限制可共享资源，明确禁止 private/no-store 响应进入共享缓存，并保留正确的协商语义。

`BusinessUvBrowserRunner.cs:117` 先 BodyAsync 读取完整资源，Store 才拒绝超过 16 MiB 的资源；大视频仍可能先占用大量内存。响应回调任务也未统一追踪等待，页面关闭时可能丢弃尚未完成的缓存写入。bin 与 meta 不是成对原子发布，并发同 URL 写入可能造成元数据和内容不匹配。

### P2：双层调度带来等待、有效期浪费与可观测性偏差

任务 Worker 与 UV 许可共用 MaxConcurrency，一个任务能够同时发起多个 UV。某些任务会先领到代理，再等其他任务释放 UV 许可，有效期在等待中消耗；各任务独立派发，没有明确的轮转公平策略。UV 许可包含设备请求和浏览器启动等待，所以 ActiveUvCount 不等于活跃浏览器数量；UI 只显示任务 Running，更不能代表 UV 执行数。

建议拆开任务准备并发、全局 UV 并发、启动并发及全局启动速率；为各阶段记录排队与耗时后，再设计公平派发和代理领取时机。保持每 UV 独立进程的业务约束，先通过实测评估优化收益。

### P2：任务批次与失败语义需要补齐

有界队列只限制队列，不限制已返回批次及 Multiple 展开的 List。最多 1000 倍复制可在入队前造成大量引用分配；大 uv 任务也保留所有已派发 Task 到任务结束。

真实接线未启用任务缓存。停止时队列余项记 Abandoned，批次尚未入队部分也可能丢失且不计入 Submitted；是否意味着服务器任务丢失取决于领取接口协议。应先明确领取是否破坏性、是否有确认/租约及重投能力，再设计持久化，不能直接以可能重复的 TaskId 去重。

任一 UV 失败使整个任务 Failed，但不撤销已成功 UV，也不自动停止后续 UV；缺少任务层的部分成功摘要。代理失败发生在 task.json 写入之前，失败运行目录可能没有任务快照，排查主要依赖日志。

### P2：协议与文档不一致

`HttpTaskSource.cs:31` 无条件追加 action=getTask。默认 URL 本来包含 action=getTask，会重复传 action；已有测试只检查其存在，未检查唯一性或冲突。

README 与注释说 pv>1 等效再访问一次，执行器实际循环 pv 次。必须根据接口契约统一行为、说明和测试；目前 pv=2 测试不能区分两种解释。每次导航复用一个 UV 初始抽取的 dwell，也与“每次随机停留”的描述不完全一致。

### P2：运行与源码管理

git 当前将整个 src/ 显示为未跟踪，其中混有浏览器二进制、profile、日志、obj 和用户配置。提交前需区分源码、构建依赖和运行数据，设置忽略规则并避免纳入凭证与会话数据。

业务默认允许任务长时间占用资源，但任务目录无清理策略；日志只有阶段性信息，缺少任务领取/开始/结束、UV 阶段耗时、缓存命中、活跃浏览器、P50/P95 和失败分类。未取得基线前不能给出可靠提速百分比或推荐最大并发。

## 验证

- 独立输出目录编译：成功，0 warning，0 error。
- --scheduler-test：通过，本地模拟任务源、空轮询、Cancel/Drain、重启。
- --json-test：通过，解析与旧缓存格式、隔离/去重等。
- --net-test：19 个本地模拟场景通过。
- --settings-test：进程返回 0。
- 未运行 --business-test：该自检需要真实设备接口，当前分析不以产生真实业务调用为前提。

现有测试通过说明其覆盖的场景成立，不覆盖上述竞速无效 JSON 胜出、慢正文超时、浏览器认证透传、高 pv 契约以及运行中立即取消等边界。

## 下一步顺序

1. 修复代理信息透传、真正有效的 IpInfo 竞速、正文完整超时和取消时的资源关闭；针对这些边界补回归验证。
2. 统一 action/pv/sleep 契约，记录任务及 UV 的部分成功与失败阶段。
3. 增加最小性能观测，使用本地可控页面和模拟设备/代理建立并发 1/2/4/8 的基线，测 UV/min、P95、内存峰值、Chrome 数、停止延迟。
4. 根据基线改派发公平性、减少代理等待和缓存 I/O，增加受控缓存写入、容量保护、会话保留策略。
5. 验证任务服务器的领取协议，再决定持久化与回执设计。
