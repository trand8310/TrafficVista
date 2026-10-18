# 任务调度

当前接线为 MainForm → TaskSchedulingService → AsyncTaskScheduler<TaskEnvelope> → HttpTaskSource / BrowserTaskConsumer。主项目配置、协议、缓存、代理与验证见 ../../README.md。HighThroughputScheduler、LocalUvTaskConsumer 与自定义准备阶段示例仍保留为通用适配代码，不属于当前业务接线。

## 并发与背压

单生产者串行取批次；应用每轮启动时按界面“任务并发数 × 倍率 × 2”计算有界 Channel 容量。队列满时等待空位，批内入队也等待，不丢弃任务。WorkerCount 来自唯一界面字段 TrafficVista:MaxConcurrency（1～1024，下一次启动生效）。旧双字段配置迁移时优先保留 TaskWorkerCount 的任务工作者数量，保存后只写入 MaxConcurrency；无任务并发字段时兼容 Business:MaxConcurrentTasks。

Parallel.ForEachAsync 遍历 N 个工作者编号，并显式设置 MaxDegreeOfParallelism=N。每次迭代是一个长期消费者，从队列逐项取任务，完整等待执行和清理后才取下一项。生产者与工作者并行运行，队列完成或取消时退出；异步等待不占专用线程。

业务UV没有共享执行池，不做资源预留，也不根据CPU或内存采样暂停任务获取。每个业务任务独立按 UvExecutionInterval 持续投递UV；设备和浏览器准备在UV内部进行，前一个UV尚未结束不会阻塞后续投递。内部配置 Browser:MaxConcurrentStarts 单独限制浏览器启动准备阶段的全局并发数，准备完成即释放名额，运行中UV数量可以超过此值。Browser:StartIntervalMilliseconds 保留实际启动的最小间隔配置。

队列容量仅限制已经入队的项；应用适配层还限制倍速展开的任务数量及估算 JSON 大小，Multiple 使用惰性 IReadOnlyList。每个任务只保留在途 UV 的 Task 对象，完成结果及时汇总。

## 生命周期

StartAsync 校验参数并启动一轮监督任务；Completion 只在生产者、Worker 与清理全部退出后完成。生产错误指数退避并加抖动；超过 MaxConsecutiveSourceErrors 使当前运行 Faulted。任务执行失败不自动重试；假值记失败，异常记失败并保存 LastError；ExecutionTimeout 是合作式取消，业务消费者通过自有进程树实现取消。

Cancel 停止生产者和 Worker；Drain 停止生产者后处理已入队任务。StopTimeout 仅限制调用方等待，不转移正在运行任务的所有权；窗口停止期间不能启动另一轮。保存缓存出错时保持 Stopping 并重试，UI 可显示错误，避免把已领取的数据当作保存成功而退出。

## 客户端持久化

通用核心 LocalCache=null 时不启用持久化。当前应用默认不启用；Business:TaskRecoveryMode=Save 时启用 business/pending/<任务接口与名称 SHA256>.json，只允许一个进程持有该文件锁。

服务器 TaskId 可能重复，应用缓存键使用独立 DeliveryId；倍速各份键也不同。完整批次先持久化，再逐项入队；领取 journal 落盘后才调用消费者。队列和批次尾部尚未领取的项可以恢复；已经领取的项不自动重放。损坏或不完整 journal 不做猜测恢复。

CompactClaimsWhenEmpty 仅适用于全局唯一交付键；待领取快照持久化为空之后，超过 1 MiB 的领取日志才可原子压缩。通用调用方默认不启用该选项，以保留稳定 ID 的去重语义。

业务已确认任务是消耗性的：客户端执行领取到的任务，失败记录后继续，不重投已开始或失败的任务，不需要新增确认或租约接口。该协议提供本地待执行恢复与一次性领取；远程领取成功到本地落盘之间仍有崩溃窗口，不宣称端到端 exactly-once。恢复顺序与并发完成顺序不承诺原始 FIFO。

## 统计

Submitted：本轮成功入队数量；Restored：其中恢复数量；Running：调用消费者的任务数；Completed：正常结束及失败结束数量；Failed：Completed 中的失败数量；Canceled：执行中被本轮取消的数量；Abandoned：入队后未执行的项；Cached：所有仍未领取的持久化项，包含尚未入队的批次尾部，因此可能大于 Queued。

业务 UI 另外显示 UV 会话、已连接浏览器与 UV 级性能；Running 不代表 Chrome 数量。Preparing/Claimed/WaitingForRateLimit 是通用准备/领取/任务启动限速指标，不等同于业务 UV 的设备准备状态。

验证入口：--scheduler-test、--json-test、--reliability-test、--admission-test。离线验证覆盖长期工作者的任务并发边界、UV间隔与存活数量解耦、启动限流与排队取消、故障保留和重新启动、满队列取消、尾部保存及待执行任务恢复。

任务接口诊断：SchedulerSnapshot.Producer 提供生产者当前阶段、阶段持续时间和轮询/重试倒计时；SourceTimeouts 与任务执行 TimedOut 分开统计。HttpTaskSource.Snapshot 记录倍率展开前的HTTP请求与返回统计、当前响应头/正文阶段、最近状态/耗时、最大耗时和请求/有效响应距今秒数。默认日志记录请求开始/结束/失败/中断、队列暂停/恢复、轮询与重试等待；`--scheduler-test` 使用本地桩验证这些场景，不请求真实任务接口。

工作者身份：Parallel.ForEachAsync遍历1～WorkerCount并建立独立异步上下文。实现 IWorkerAssignedTask<T> 的任务在出队时创建带 WorkerId 的执行副本；TaskEnvelope 的WorkerId不进入待执行缓存。UV继承任务WorkerId，日志在发送时捕获编号，异步UI格式化不重新读取上下文。Playwright缓存响应和路由回调显式恢复所属UV编号。工作者之外使用0；实时列表和文件日志均有独立编号字段。`--scheduler-test` 验证并发隔离、异步传递、消费失败后续取任务、UV/归档一致性和上下文清理；`--log-test` 验证独立列、0默认值及压力下后台格式化。
