# 任务调度

当前接线为 MainForm → TaskSchedulingService → AsyncTaskScheduler<TaskEnvelope> → HttpTaskSource / BrowserTaskConsumer。主项目配置、协议、缓存、代理与验证见 ../../README.md。HighThroughputScheduler、LocalUvTaskConsumer 与自定义准备阶段示例仍保留为通用适配代码，不属于当前业务接线。

## 并发与背压

单生产者串行取批次；应用每轮启动时按界面“并发数 × 倍率 × 2”计算有界 Channel 容量，覆盖固定 Scheduling:QueueCapacity。队列满时等待空位，暂停新批次领取；批内入队也等待空位，不丢弃任务。WorkerCount 来自界面TrafficVista:TaskWorkerCount（1～1024，下一次启动生效）；没有新字段时兼容Business:MaxConcurrentTasks。全局 UV 硬上限来自界面 MaxConcurrency，任务 FIFO 预留名额，UvExecutionInterval 跨任务统一限速。Worker只增加任务处理/准入等待数量，不绕过CPU、内存和已有IP优先规则，也不增加队列容量。浏览器启动另受 Browser:MaxConcurrentStarts 限制。

业务消费者通过 IPreparedTaskConsumer 在持久化消费确认前等待资源并预留名额，不领取 IP。预留失败/取消不会确认消费，待执行任务可以恢复。执行阶段才领取 IP，预留名额保持到所有 UV 清理完成。已有任务剩余 UV 投递优先于新 IP；耗时低估可扩展空闲未预留名额，受领取前内存预算与硬上限约束。ITaskFetchAdmission 在远程 FetchTimeout 开始前等待系统资源，承压等待不计来源错误。自动估算细节见主项目 README。

队列容量仅限制已经入队的项；应用适配层还限制倍速展开的任务数量及估算 JSON 大小，Multiple 使用惰性 IReadOnlyList。每个任务只保留在途 UV 的 Task 对象，完成结果及时汇总。

## 生命周期

StartAsync 校验参数并启动一轮监督任务；Completion 只在生产者、Worker 与清理全部退出后完成。生产错误指数退避并加抖动；超过 MaxConsecutiveSourceErrors 使当前运行 Faulted。任务执行失败不自动重试；假值记失败，异常记失败并保存 LastError；ExecutionTimeout 是合作式取消，业务消费者通过自有进程树实现取消。

Cancel 停止生产者和 Worker；Drain 停止生产者后处理已入队任务。StopTimeout 仅限制调用方等待，不转移正在运行任务的所有权；窗口停止期间不能启动另一轮。保存缓存出错时保持 Stopping 并重试，UI 可显示错误，避免把已领取的数据当作保存成功而退出。

## 客户端持久化

通用核心 LocalCache=null 时不启用持久化。当前应用默认启用：business/pending/<任务接口与名称 SHA256>.json；只允许一个进程持有该文件锁。

服务器 TaskId 可能重复，应用缓存键使用独立 DeliveryId；倍速各份键也不同。完整批次先持久化，再逐项入队；领取 journal 落盘后才调用消费者。队列和批次尾部尚未领取的项可以恢复；已经领取的项不自动重放。损坏或不完整 journal 不做猜测恢复。

CompactClaimsWhenEmpty 仅适用于全局唯一交付键；待领取快照持久化为空之后，超过 1 MiB 的领取日志才可原子压缩。通用调用方默认不启用该选项，以保留稳定 ID 的去重语义。

业务已确认任务是消耗性的：客户端执行领取到的任务，失败记录后继续，不重投已开始或失败的任务，不需要新增确认或租约接口。该协议提供本地待执行恢复与一次性领取；远程领取成功到本地落盘之间仍有崩溃窗口，不宣称端到端 exactly-once。恢复顺序与并发完成顺序不承诺原始 FIFO。

## 统计

Submitted：本轮成功入队数量；Restored：其中恢复数量；Running：调用消费者的任务数；Completed：正常结束及失败结束数量；Failed：Completed 中的失败数量；Canceled：执行中被本轮取消的数量；Abandoned：入队后未执行的项；Cached：所有仍未领取的持久化项，包含尚未入队的批次尾部，因此可能大于 Queued。

业务 UI 另外显示 UV 会话、已连接浏览器与 UV 级性能；Running 不代表 Chrome 数量。Preparing/Claimed/WaitingForRateLimit 是通用准备/领取/任务启动限速指标，不等同于业务 UV 的设备准备状态。

验证入口：--scheduler-test、--json-test、--reliability-test、--admission-test。离线可靠性验证覆盖满队列取消、尾部保存、重启只恢复待执行项、同服务器 ID 独立交付与领取日志压缩；准入验证覆盖压力恢复、IP 优先、20 UV 分批投递、耗时低估扩展、等待取消恢复与来源超时隔离。

