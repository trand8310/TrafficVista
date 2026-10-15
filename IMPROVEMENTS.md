# TrafficVista 改进与验证记录

日期：2026-10-14。此记录对应 PROJECT_ANALYSIS.md 中的修复前问题；新版可执行文件为 src/Build/MainClient.exe。没有调用真实任务、代理或设备接口，没有自动启动正式任务。

最新调度规则：全局UV并发池直接使用界面并发上限，各任务独立按UV间隔投递；取消整任务预留、历史内存峰值缩减并发和未投递完的任务阻塞其它任务。任务在领取IP前只等待一个首UV名额，后续使用共享空闲名额。资源承压仅暂停新的任务接口请求，已取得的任务继续执行，暂停/恢复输出日志。浏览器同时启动上限与启动间隔单独控制，默认4个/100ms，可通过Browser:MaxConcurrentStarts和StartIntervalMilliseconds配置。下面“自动资源准入与IP优先投递”等段落是此前阶段记录，以此最新规则及src/README.md为准。

新版验证：两项各12UV填满24个共享名额，独立任务间隔、全局并发硬上限、历史异常内存峰值不降低并发、50 Worker共享2个名额、已获取任务在后续承压时继续执行、等待取消恢复、启动限速及释放均通过。配置/表单与本地真实Chrome完整回归通过；共享缓存用例使用CSS验证跨UV命中（当前浏览器环境未发起原图片用例的请求），未降低命中断言。1/2/4/8并发真实Chrome基准通过，但本地机器与页面不代表正式机器吞吐，也不保证每分钟500UV。

2026-10-15浏览器启动方式更新：业务浏览器改用 LaunchAsync 直接启动，保留每 UV 独立 Chrome 进程、Context 和新临时 profile，同 UV 多 PV 共享页面与会话。Playwright管理通信和临时profile清理，不再等待DevToolsActivePort或通过ConnectOverCDPAsync附加。每UV不复用身份数据，图片拦截和共享缓存开关保持原配置。原Windows Job清理与进程统计保留：通过当前浏览器CDP取得主PID，再接管该进程的启动辅助进程；沙箱层级冲突时使用独立嵌套Job。多Job峰值之和为保守内存上界。新增browserLaunchMs/browserSetupMs计时。启动API不支持token，取消等待有界启动和进程接管后再清理。曾实测LaunchPersistentContextAsync，但当前定制Chrome下首次慢页面检查不稳定，因此最终选择LaunchAsync和独立Context，导航/内容判断代码保持原实现。后文挂起启动、单Job和自有profile清理为历史实现，以本段及src/README.md为准。
直接启动验证：构建0警告0错误；完整离线可靠性、单窗口/双UV Cookie与localStorage隔离、启动失败/取消、桌面所属进程计数、代理环境、浏览器关闭与无导出两PV回归均通过。报告见 [direct-launch](docs/validation/2026-10-15/direct-launch/reliability-test.txt)。

2026-10-15静态缓存最终策略（以本段及src/README.md为准）：非GIF图片、CSS、JS、字体、视频正常加载并缓存，无单资源大小限制。普通资源仅用完整URL的SHA256，跨Cookie/UA/代理出口共享；视频Range/If-Range单独区分，合法206校验后回填。保留浏览器UV会话隔离，回填删除Set-Cookie。有效期仅按StaticCacheTtlHours从原写入时间计算，命中不续期，重启修改时长后现有条目也使用新时长；忽略服务器缓存控制/时间/协商策略。v4独立目录避免复用旧键，原子正文/元数据及读取租约继续保护文件，容量软预算仍可提前淘汰。GIF扩展名/MIME/文件头均排除；支持解压正文、chunked与未知长度，框架仍会缓冲正文。关闭共享缓存后使用Chrome原生缓存。专项测试覆盖跨身份同URL复用、服务器禁止缓存/短有效期覆盖、时长边界/重启配置、视频完整与不同Range、GIF排除、大文件及取消/租约；完整回归同时覆盖UV会话隔离。验证报告见[专项测试](docs/validation/2026-10-15/static-cache-v4/static-cache-test.txt)和[完整回归](docs/validation/2026-10-15/static-cache-v4/reliability-test.txt)。
## 已落地的客户端改进

2026-10-15屏幕匹配完善：真实物理分辨率仍沿用竖屏归一化，统一按同一DPR换算两边，不再单独吸附CSS宽度。取消旧CSS高度600及普通手机比例硬拒绝，覆盖低分辨率、宽比例/方屏/平板、4K，并在优先候选不适合时扩展搜索。设备可选devicePixelRatio/deviceScaleFactor/dpr优先使用，缺失才估算；日志明确DprSource和匹配策略，正常方屏不再警告，超出覆盖范围才回退并说明原因。物理分辨率不能唯一还原设备真实缩放。84组一致性、设备DPR贯通、真实Chrome低分辨率/平板/方屏/DPR3以及完整可靠性回归通过，未请求生产接口。详见docs/validation/2026-10-15。

页面内容判断完善：首次DOMContentLoaded后在加载预算内轮询动态内容，每次检查独立2秒上限；正文尺寸不再代表内容，支持绝对定位的零高度body、可见控件/媒体和跨域iframe实际内容。结束检查综合URL、最新主文档响应/网络失败和可见内容，总等待3秒，控制通道检查超时与确实为空分开报错。真实Chrome回归通过动态渲染、特殊布局、跨域iframe；隐藏内容、空/错误iframe、访问后变空和网关错误跳转仍失败。脚本死循环用例在contentCheck报明确超时，UV约5554ms结束且浏览器清理归零；旧Cookie/缓存/PV/取消/代理认证回归通过。没有调用生产接口。

页面准备堆积修复：原启动限速在Context创建后提前释放，NewPage及CDP/初始化阶段不受同时启动数量限制，部分调用缺少取消或独立超时。现启动名额保持到页面准备完成；会话创建、页面创建、CDP环境和初始化拆开显示并共享Business:BrowserSetupTimeoutSeconds（默认20秒），异常/超时先终止该UV进程再放行下一启动。模拟无响应API、启动名额持有/释放、取消分类、真实浏览器关闭和本地完整可靠性回归通过。截图指向页面准备堆积，但未取得正式机器CDP/磁盘等数据，不能仅凭截图断言底层Chrome阻塞原因或进程泄漏。

最新存储规则：Business:TaskRecoveryMode 默认 Discard（有界内存队列，不加载/保存待恢复任务），Save 才持久化并恢复尚未开始的任务；已开始任务不重放。Business:ExportRunData 默认 false，关闭会话、设备及运行结果导出和归档扫描；Logging:FileEnabled 默认 false，实时UI日志保留。共享磁盘缓存仅在界面显式开启时使用。Chrome临时profile仍必需，正常完成后清理；会话快照无需参与Cookie维护，显式开启导出时不采集IndexedDB且等待最多3秒。配置、调度、真实Chrome关闭和不导出两PV用例、完整本地可靠性回归通过。

| 分析问题 | 当前处理 | 验证 |
|---|---|---|
| 代理协议、认证、领取及过期时间丢失 | NetProxyProvider → BusinessProxy → Chrome 完整传递；界面 TTL 从领取时计时，与供应商截止时间取较早者；IpTtl=0 仍执行供应商有效期 | 本地 407 认证代理，启动/执行中到期，已过期代理不派发 UV |
| 无效 JSON 抢先赢得 IpInfo 竞速 | 各分支先验证 ip/query/origin，再选胜出者；落败请求取消并等待清理 | 快速空对象 + 较慢有效 IP；全部错误、取消、并发隔离 |
| HTTP 正文没有完整超时 | linked CTS 覆盖连接、响应头及整个正文；明确区分内部超时和调用取消 | 响应头立即到达、正文延迟的模拟接口 |
| 浏览器无法立即取消/关闭不彻底 | 每 UV 拥有独立 Windows Job、进程树、profile 与 CDP；浏览器 API 的等待响应 token；关闭、退出均有限时；所有 Chrome 子进程受同一 Job 约束 | 导航中取消、启动阶段有效期到期，UV/浏览器计数归零 |
| 并发 1 时一个 UV 弹出两个窗口 | 取消启动 about:blank 默认窗口，使用 --no-startup-window；业务 Context 创建唯一业务窗口 | 同一会话 CDP 枚举：修复前默认页面 1/页面目标 2/可见窗口 2，修复后 0/1/1；隐藏模式 0/1/0 |
| profile 短暂文件占用被误判为访问失败 | 进程树退出后重试删除；短暂文件占用写入 profile-cleanup.pending，已完成会话维护继续重试 | 实际 Chrome 文件占用场景及已完成会话清理 |
| 承压时优雅关闭慢，被误记为页面访问失败 | Context 优雅关闭超时后仍结束并确认自有 Chrome Job 全部退出；强制清理失败才计清理错误，记录 gracefulContextCloseTimedOut 并观察迟到异常 | 8 并发压力验证中定位、随后修复并复验 |
| 两层并发耦合、领取代理后完全排不上 UV | 界面TaskWorkerCount独立控制任务Worker；FIFO 任务资源预留及首个启动窗口先于消费确认和领 IP；已有 IP 优先投递，UvExecutionInterval 跨任务统一生效；启动许可单独控制 | FIFO、50 Worker保持UV硬上限、等待取消恢复、20 UV 压力变化、耗时低估扩展、全局启动间隔、本地真实 Chrome 基准 |
| 高倍速及大 uv 占用过多内存 | Multiple 惰性展开，数量和估算 JSON 字节双上限；每任务仅保留在途 UV Task，完成结果及时汇总 | 1000 倍批次、稳定独立交付键、完成/部分失败摘要 |
| 停止丢失排队及批次尾部 | 默认先持久化整批，再入队；独立 DeliveryId，领取日志落盘再执行；未领取项恢复；空收件箱安全压缩较大领取日志，替换失败后重新打开原日志 | 满队列取消、未入队尾部保存、重启恢复、不重放已领取项、日志压缩及文件占用失败后继续领取 |
| 失败任务无快照，部分成功不可见 | 代理前先写 task.json；summary.json 包含计划、派发、成功、失败/取消及未派发数量；设备准备失败也有 UV result；非法计划和响应保存至 RecoveryRoot | 代理失败、部分 UV 失败、取消、真实浏览器实际 PV |
| 静态缓存串设备/出口、忽略响应策略 | 只缓存明确公共且新鲜的小图片/CSS/字体；拒绝 Cookie/认证/Range/private/no-store/no-cache/Set-Cookie/未知 Vary；键包含请求协商维度与代理出口；s-maxage、Age/Date 和应用 TTL 共同约束寿命 | 策略、出口隔离、TTL、大条目保护、真实公共图片命中 |
| 大媒体完整读取及缓存写入无约束 | 不捕获媒体、未知/分块长度、压缩正文或超限响应；单条上限 1 MiB，最多两路捕获，限时并追踪写入 | 大条目拒绝、缓存启停、取消后捕获结束 |
| bin/meta 非原子，同步 I/O 阻塞 | 头、正文、寿命信息在单一 .entry 文件中原子发布；运行时异步读写；写入串行化并按容量淘汰；旧 v1 缓存不复用 | 同 URL 并发写入的头/正文一致性 |
| 之前误将 PV 设为强制回源 | 按用户澄清移除 Network.enable/setCacheDisabled/clearBrowserCache；首个 PV 导航，后续 Reload 当前页面，同 UV 保留 Cookie/存储/缓存，不同 UV 隔离 | 同 UV 三次 PV 保留会话、资源只下载一次；下个 UV 独立会话/重新下载；重定向后刷新落地页 |
| action 重复，PV/sleep 文档不一致 | action/name 各保留一个；PV 为总访问次数；sleep 支持固定值和范围，每次访问独立抽样；说明统一 | 参数编码与唯一性、PV=3、现有 JSON/调度验证 |
| 设备池在接口变更后继续发放旧源数据 | 设备接口 URL 改变时清空剩余池内容 | 本地 a/b 两设备源切换 |
| 屏幕参数仍按固定 DPR 粗略换算 | 接入 AndroidViewportMatcher，传入 sw/sh、make/model；每 UV 匹配一次，Chrome启动参数和Context viewport/screen/DPR使用同一结果；保存viewport.json及回退警告 | 720/1080/1440分辨率、横向输入、稳定性、异常比例回退；真实Chrome参数读取 |
| 网络连接及许可生命周期浪费 | 直连领取请求复用连接池；每代理查询使用独立不可变通道，内部竞速共享；Net 许可与通道随服务释放 | Net 全套本地模拟验证 |
| 缺少真实 UV/浏览器统计 | 每轮重置 UV 成功/失败/取消、速率、最近 1024 样本 P50/P95；分开统计排队、限速、启动许可等待、真实启动、设备、导航及清理；UI 显示待恢复项 | 指标与任务摘要、UI 绑定及离屏布局验证 |
| 任务目录无限积累 | 默认 7 天/1024 MiB；每小时清理已完成且带所有权标记的运行，保护活跃租约及链接；支持延迟 profile 删除重试 | 活跃目录不删，已完成过期目录删除 |
| 测试日志混入正式日志、源码混运行数据和凭证 | 自检单独记录到 demo-output/logs，标记 RunId/RunMode；.gitignore 排除 Build/obj/用户配置/profile；账号密钥移入本地私有配置，发布不复制 | Git 忽略检查、配置覆盖、全部自检日志隔离 |

## 关键行为变化

- 界面“并发数量”仍限制 UV 会话数量；新增“任务Worker数”独立配置TaskWorkerCount，范围1～1024、默认4、下一次启动生效。旧配置无新字段时兼容Business:MaxConcurrentTasks；已验证50/96配置保存及1024边界。
- UV 执行间隔现在跨所有任务统一生效；例如 2000ms 表示整个调度器相邻 UV 开始间隔至少两秒。
- IpTtl 从供应商领取时刻计算，包含 IpInfo 查询耗时；供应商真实有效期始终有效。
- 已撤销之前对 PV 强制回源的误解：同 UV 后续 PV 正常刷新，保留 Cookie/存储并允许缓存命中。未开启共享模式时使用 Chrome 原生策略；可选共享缓存仍只处理明确公共且可验证大小的静态资源。
- 待执行任务自动恢复，但使用本次启动配置。已领取任务失败、取消或崩溃后不自动重放；已经成功的 UV 不撤销，剩余 UV 按既有尽力完成语义执行。
- 同一运行目录的相同任务源/名称只允许一个调度器持有收件箱；多个独立客户端需要不同运行目录。
- 默认真实凭证位于被 Git 忽略的 appsettings.private.json；仓库默认 URL 已使用占位符。发布新环境时单独提供凭证或通过界面配置。

## AndroidViewportMatcher 接入

原业务 CssViewport 仅按物理宽度选择固定 DPR=3 或 3.5，未使用已存在的 matcher。已替换为 AndroidViewportMatcher.Match(ScreenWidth, ScreenHeight, Make, Model)，每 UV 只计算一次并供 Chrome 启动参数及 Playwright Context 共用。应用 CssWidth/CssHeight 到 window-size、screen-size、screen-avail-size、ViewportSize 与 ScreenSize；DeviceScaleFactor 同时用于 device-pixel-ratio 与 Context。保留 matcher 的稳定型号种子、归一化和回退算法。

每个 UV 新增 viewport.json，result.json 同时保存匹配结果；回退/异常比例会记录警告。参数验证包含 Samsung 1080×2400 → CSS 411×914/DPR 2.625、Honor 720×1600 → 360×800/DPR 2、OnePlus 1440×3216 → 411×919/DPR 3.5，以及横向、异常比例输入。[参数映射验证](docs/validation/2026-10-14/viewport-test.txt)。真实 Chrome 的 screen、响应式 viewport 和 DPR 与匹配记录一致，详见 [可靠性验证](docs/validation/2026-10-14/reliability-test.txt)。

## UV / PV 会话与缓存规则纠正

用户明确：不同 UV 使用独立设备会话与 userDataDir，同 UV 的不同 PV 相当于刷新，共用 Cookie 和缓存，缓存命中可以接受。之前把“每次 PV 必须回源”当作要求是错误的，已移除强制禁用/清理原生缓存及相关 CDP 会话。首次 PV 导航到任务 URL，后续 Reload 当前页面，重定向之后刷新落地页面。

真实 Chrome 验证：同 UV 三次 PV 的 Cookie/LocalStorage 保持一致，测试资源只下载一次；下一个 UV 初始不带前一个 UV 的 Cookie，存储计数从一开始，资源重新下载。[可靠性验证](docs/validation/2026-10-14/reliability-test.txt)。测试页面文档使用 no-store 以便确认实际三次刷新，资源以 force-cache 读取验证缓存允许复用；这不是要求业务网站必须回源的策略。

当前配置 IsCacheMode=false 表示不启用项目共享缓存，Chrome 原生缓存正常工作且允许 Service Worker。IsCacheMode=true 的可选共享缓存保留：通过 Route 处理明确公共静态资源，Playwright 自身会禁用原生 HTTP 缓存，Service Worker 为拦截设为 Block；命中来自项目共享缓存。[Playwright Route 缓存行为](https://playwright.dev/dotnet/docs/api/class-page#page-route)。

## 单 UV 窗口修复

运行配置确认为并发 1、倍率 1、可见模式、UV 间隔 5000ms。浏览器原启动参数打开 about:blank 默认窗口，随后独立业务 Context 又创建一个窗口；这两个窗口属于同一个 UV 会话，不是两个调度名额。隔离测试通过 Target.getTargets 和 Browser.getWindowForTarget 复现并确认窗口 ID 数量。启动改为 --no-startup-window，业务页面正常创建，可见/隐藏模式及进程/profile 清理均通过。[修复前窗口计数](docs/validation/2026-10-14/window-baseline.txt)、[修复后验证](docs/validation/2026-10-14/window-test.txt)。Chromium 定义此参数用于禁止自动打开启动窗口。[Chromium 启动参数定义](https://chromium.googlesource.com/chromium/src/+/refs/tags/147.0.7727.118/chrome/common/chrome_switches.cc)。

## 自动资源准入与 IP 优先投递

流程改为：持久化待执行任务 → 准备阶段等待资源/预留任务名额/首个全局启动窗口 → 持久化消费确认 → 领取 IP → 按间隔分批投递 UV → 等待全部清理 → 释放预留。任务服务直接接入 IPreparedTaskConsumer，避免委托包装绕过准备阶段。停止发生在等待资源或首个窗口时，任务未确认消费、不领 IP，重启可以恢复。

自动处理 CPU 趋势、物理/提交内存余量、单浏览器内存预算、UV 耗时和任务预留数，无新增界面参数。CPU 一秒差分采样并平滑，85% 暂停/70% 恢复；物理内存保留 10% 且至少 512 MiB。每 UV 初始预算 384 MiB，之后按本轮单 Chrome Job 峰值提交内存的 1.25 倍保守上调；此预算不是实际工作集。CPU 首次需要约一秒建立基线，采样失败暂停新任务并显示原因。

时间估算采用最近 128 个成功 UV 的 P95 非停留耗时，结合 PV、sleep 上界和首页延时；初始每 PV 基准 5 秒，非停留耗时留 20% 余量。按耗时/执行间隔计算初始预留，间隔为零时使用资源允许的并发。全局预留总量及实际 UV 都不超过界面硬上限。耗时低估时，已有 IP 可优先扩展未预留空位，仍受领 IP 前内存预算和硬上限约束。

已有任务还有 UV 未投递时，不允许新任务领 IP，也暂停新批次领取；当前任务在压力升高后保留名额并继续投递。全部投递后才允许空余名额供下一任务使用；原任务预留直到真实清理结束。间隔计时在实际派发后更新，首个窗口等待发生在领 IP 前。资源等待在远程 FetchTimeout 开始前发生，不计接口失败。

已知 IP 有效期取界面限制及最近 32 次代理剩余寿命中的保守值。未知供应商寿命只能在领 IP 后获知；任务自身在间隔/硬上限下无法按估算完成时，记录风险并尽力执行，不无限等待、不重领 IP、不重投 UV。summary.json 保存初始/最终预留、预计 UV/任务时间及已知有效期风险。自动学习仅限本轮，不保证未知页面实际耗时或有效期内全部完成。

新增离线 [准入验证](docs/validation/2026-10-14/admission-test.txt)：自动估算、CPU 恢复滞回、内存限额、大 UV 计划、20 UV 任务领取后承压继续投递、新 IP 等待、空位扩展、停止恢复、首个限速窗口取消、资源等待不误计 HTTP 超时，以及 Windows 原生采样。

重构后的真实 Chrome 复验，每组 8 UV，共 32 UV 全部成功，结束时浏览器计数归零：

| 配置并发硬上限 | 含准入等待总耗时 | 成功 UV/min | UV P95 | 峰值已连接浏览器 |
|---:|---:|---:|---:|---:|
| 1 | 28.66s | 16.8 | 4.24s | 1 |
| 2 | 27.14s | 17.7 | 7.08s | 2 |
| 4 | 32.29s | 14.9 | 22.69s | 4 |
| 8 | 29.65s | 16.2 | 25.13s | 6 |

本次准入等待平均约 1.1～6.2 秒，计入总耗时；高并发下启动和清理延迟增加。该结果用于验证承压时的完成和清理，不是提速承诺，不能与下方不同时间采样作严格 A/B 比较。压力验证曾发现 Context 优雅关闭超时误记失败；本轮真实 Chrome 复验未触发该超时，另以确定性延迟关闭自检覆盖超时处理、迟到异常及其他关闭错误。原始 [重构后 benchmark.json](docs/validation/2026-10-14/admission-benchmark.json)、[benchmark.txt](docs/validation/2026-10-14/admission-benchmark.txt)。

## 首次修复后的本地效率基准

以下为自动准入重构前、首次客户端修复后的记录。每组 8 个 UV，真实本地 Chrome、设备接口桩、本地简单页面、零停留、UV 间隔 0、共享缓存关闭；启动并发上限 4。数据不是与旧版本的 A/B 提速对比，也不代表真实代理、广告/交互页面或带停留任务的吞吐。

| UV 并发 | 总耗时 | 成功 UV/min | UV P95 | 峰值已连接浏览器 |
|---:|---:|---:|---:|---:|
| 1 | 22.27s | 21.5 | 3.27s | 1 |
| 2 | 17.37s | 27.6 | 5.27s | 2 |
| 4 | 14.28s | 33.6 | 8.16s | 4 |
| 8 | 21.14s | 22.7 | 20.98s | 6 |

本机这次样本中，并发 4 比 8 的吞吐更高，8 的启动等待与单 UV 延迟明显增加。不能据此为真实业务固定最优并发：停留、网络、页面脚本和设备接口耗时都会改变结果。

主进程峰值工作集约 74～83 MiB；单 UV 整个 Chrome Job 的峰值提交内存约 275～291 MiB。两项口径不同，不应相加冒充全机峰值；还未覆盖整个系统及 Playwright 驱动的峰值。浏览器指标统计已连接的业务浏览器，不包含刚创建且尚未连接的 Chrome 启动阶段。

原始结果：[benchmark.json](docs/validation/2026-10-14/benchmark.json)、[benchmark.txt](docs/validation/2026-10-14/benchmark.txt)。

## 验证记录

- 正常输出目录构建通过：0 warning、0 error。
- 配置、调度、JSON、Net、表单布局、全局异常六组自检通过；均使用本地数据/接口。
- 离线可靠性自检覆盖表格中的边界，并使用真实 Chrome 验证代理认证、PV 刷新与会话保持、原生缓存复用/UV 隔离、重定向、共享缓存开关与取消/到期清理；输出见 [reliability-test.txt](docs/validation/2026-10-14/reliability-test.txt)。
- 本地并发 1/2/4/8 基准四组、共 32 个 UV 全部成功。
- [表单预览](docs/validation/2026-10-14/mainform-preview.png) 已检查，任务统计、UV 指标与错误摘要完整显示；完整异常可悬停查看。

验证日志中的故意失败属于测试场景，不代表正式业务错误。未用真实接口进行生产负载验证。

## 已确认的任务规则与运行注意事项

**消耗性任务**：已确认客户端只消耗领取到的任务。队列容量每轮启动按界面“并发数 × 倍率 × 2”计算，队列满时等待空位并暂停获取下一批；任务 Worker 与 UV 并发分别控制。执行失败后记录结果并继续下一任务，不自动重投已开始或失败的任务，无需增加服务端确认或租约接口。只恢复已领取但尚未开始的本地待执行任务。远程领取成功到本地落盘之间仍存在崩溃窗口，客户端不宣称端到端 exactly-once。

**历史 profile**：修复前 browser-profiles 的 48 个目录约 621 MiB，检查时没有 Chrome 正在使用，但它们没有当前代码的运行所有权标记，可能含历史会话。已生成 [历史目录清单](docs/validation/2026-10-14/legacy-profiles.json)，保留数据；没有批量删除无法确认用途的旧会话。新版业务 profile 生命周期及新任务目录保留策略已修复。

**磁盘故障**：持久化落盘失败时仍保护任务所有权并显示 Stopping/错误，不能声称已保存；修复磁盘空间、权限或占用问题后才能继续正常停止。

## 实现依据

MainForm 实时日志接线：Serilog 增加独立显示 Sink，生产线程仅向512条有界队列TryWrite，后台格式化并保留2000条历史；界面虚拟ListView每200ms最多取128条，仅构造可见行，双缓冲绘制。超过显示缓冲上限淘汰旧副本并显示计数，文件记录不因UI过载丢弃。每条显示限制4096字符，异常详情可双击查看，超长原文在日志文件；支持暂停显示、关闭跟随和清空显示。状态摘要保持在配置面板右侧，日志占用下方空间，窗体隐藏/关闭停止Timer并释放控件。

新增 [log-test.txt](docs/validation/2026-10-14/log-test.txt)：4线程突发4万条及持续8000条，覆盖环形游标、显示与字符上限、异常详情、暂停/继续、清空、释放和UI心跳。实际显示通道测试突发入队522.2ms、手动批量更新最大55.1ms；持续阶段6.44s内UI心跳106次，数据受机器负载影响，未包含文件I/O，也不承诺所有高频消息均展示。MainForm实际Sink接线/虚拟列表及 [布局预览](docs/validation/2026-10-14/mainform-preview.png) 已验证，全局异常回归通过；独立目录构建0警告0错误，未停止运行中的用户程序。

新增任务地区/IP环境接线：领取请求提供完整任务、配置快照及默认1的数量；多地区原始数据保留。每家供应商在自己的适配类中独立处理地区、数量和真实IP参数。Ipzan 已按确认规则实现 address 映射、随机多地区选择及 num/format/realIp 精确替换；地区文件按应用目录加载并随构建/发布复制。VendorB 的地区和数量仍留占位。界面增加“真实IP”“IP详情”并保存恢复：按开关决定供应商出口IP复用、缺失回退查询或强制查询详情。四接口经代理竞速，全失败重新领取；取消不重试。完整详情传入每UV环境设置，有效时区、经纬度和定位权限作用于Context，真实IP传入Chrome参数；未知字段留档。

Ipzan JSON 解析支持 data.list 的 ip/port/real_ip/expired，连接地址与真实出口分离，毫秒绝对截止时间精确转换为剩余 TTL；仅缺失截止时间时配置兜底，跳过已过期/无效条目，兼容原TXT。业务默认Count=1；Count>1虽修改num，但当前返回单代理，不支持批量分发。[ipzan-test.txt](docs/validation/2026-10-14/ipzan-test.txt) 与Net场景二十一覆盖地区、URL边界及JSON真实IP流程。由于运行中的客户端/Visual Studio锁住正常输出，本轮在 .audit-build/ipzan-current 构建通过（0警告0错误）并通过离线测试；未终止用户进程，正常输出需要停止运行后重新构建。

直连模式补充：真实IP/详情任一开启就通过本机通道查询一次同组接口，全部关闭则跳过。直连元数据的Server为null，HasProxy=false，浏览器启动和Context只在HasProxy时设置代理，直连不受租赁IpTtl限制。实际本地HTTP竞速、无供应商请求、失败/取消传播及四种直连开关组合已验证；真实本地Chrome验证无代理设置仍能应用出口IP参数、时区和定位。查询失败明确结束任务，不静默换成代理模式。

新增 [proxy-request-test.txt](docs/validation/2026-10-14/proxy-request-test.txt) 覆盖四种开关组合、请求快照隔离、URL占位、业务入口传递、失败换新/重试上限/取消，以及真实本地Chrome时区和定位；Net测试补充JSON供应商出口字段提取及无效字段回退。表单保存恢复和预览已验证；原有可靠性与屏幕匹配回归通过。构建0警告0错误，测试未调用真实供应商或公网IP信息接口。

Windows 子进程先挂起创建、加入 Job 再恢复；Job 负责所属进程树的终止与进程退出清理。[微软 Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)、[AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)。

系统资源采样使用Windows物理/提交可用量及CPU时间差分。共享后台任务每秒采样，准入/状态栏读同一不可变缓存；单处理器组用GetSystemTimes，多组机器改用Processor Information总计数器，避免只统计调用线程的主处理器组。[GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)、[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)、[PdhAddEnglishCounterW](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)、[PdhGetFormattedCounterValue](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue)。

状态栏/桌面入口已接入：四项状态含应用运行状态、逻辑CPU/趋势使用率、物理内存/使用率、所属Chrome Job进程数；悬停显示UV会话，不扫描其他Chrome。开机启动通过后台STA创建当前用户快捷方式，携带 --auto-start，创建后打开启动目录；窗体初始化后自动触发开始按钮且仅触发一次。清理同目标的旧Startup快捷方式、Run/RunOnce和开机/登录计划任务，权限失败单独记日志；不根据名称删除其它程序或其它安装路径。临时快捷方式位于系统临时目录，重复创建幂等。应用目录通过资源管理器打开。右侧摘要按用户缩小后的布局压缩为短行，完整错误保留悬停。[desktop-test.txt](docs/validation/2026-10-14/desktop-test.txt) 在隔离目录验证shortcut目标/参数/工作目录/更新/失败、旧快捷方式清理及其它程序保留，真实Chrome所属进程与清理归零；[form-test.txt](docs/validation/2026-10-14/form-test.txt) 使用空任务HTTP桩验证自动启动按钮及只触发一次。真实Windows启动目录、注册表和计划任务未在测试中改动；本机4逻辑核，未在真实多处理器组硬件验收。构建0警告0错误。

共享缓存的 s-maxage 优先级、Age/Date、新鲜度及 private/Vary 处理依据 HTTP 缓存规范；实现采用更保守的资源和响应白名单，并未实现通用 HTTP 缓存代理的全部功能。[RFC 9111](https://www.rfc-editor.org/rfc/rfc9111.html)。

2026-10-15 HTTP 644任务保护：按同任务UV完成顺序连续3次主文档644，停止后续投递并中断投递等待，已投UV继续完成；成功或其他错误清零，计数不跨任务/消费轮次。导航状态用类型化异常穿过AggregateException，避免按字符串误判；失败摘要标记consecutive-http-644。专项覆盖阈值/重置、同ID新运行、其他任务继续、在途UV保留、许可释放、未投摘要和真实Chrome 644/200导航。报告见[http644-test.txt](docs/validation/2026-10-15/http644/http644-test.txt)。

2026-10-15代理不可用停止投递：仅代理模式的主文档明确代理连接/隧道/SOCKS/认证/证书/协议错误或407一次即停止该任务后续UV，已投UV正常完成；通用超时/重置/DNS/502/504不作代理失效证明，保留连续3次644规则。类型化错误穿过清理聚合异常，任务摘要保留proxy-unavailable原因。扩展专项覆盖全部明确错误码、407、直连/通用错误排除及真实Chrome连接本地已关闭代理端口，确认只派发1/5、余下4个停止且浏览器/许可归零。[专项报告](docs/validation/2026-10-15/proxy-unavailable/http644-test.txt)。错误分类参考[Chromium网络错误定义](https://raw.githubusercontent.com/chromium/chromium/main/net/base/net_error_list.h)。
