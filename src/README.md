# TrafficVista

Windows / .NET 8 WinForms 客户端；每个 UV 使用独立 Chrome 进程及浏览器 Context。修复与实测记录见 ../IMPROVEMENTS.md。

业务 Chrome 使用 LaunchAsync 直接启动，每个 UV 使用独立浏览器进程、新临时 profile 和独立 Context，不跨 UV 复用 Cookie 或存储。同 UV 的多个 PV 继续复用页面和 Context。Playwright 管理浏览器通信、临时 profile 与关闭清理，不再等待 DevToolsActivePort 或通过 ConnectOverCDPAsync 附加。启动无默认页面，一个 UV 只有一个业务页面/窗口。图片拦截与 IsCacheMode 行为保持原配置。

## 配置

基础默认值为 appsettings.json；本机凭证可放在 appsettings.private.json（Git 忽略、开发构建复制、发布不复制）；最后由 appsettings.user.json 覆盖界面项和 Net 配置。用户文件由界面保存，构建/发布不会覆盖。私有配置仅合并 TrafficVista 与 Net 节。仓库默认代理 URL 使用 CONFIGURE_LOCALLY 占位符，真实账号、密钥已迁移到本机私有文件。

界面“并发数量”是全局UV并发上限，包含设备准备、启动等待、页面执行及清理。任务Worker独立配置，多个Worker共享此并发池，等待首UV名额时不创建浏览器、不领取IP。UvExecutionInterval现在是每项任务自己的UV投递间隔，不再跨任务统一限速。Browser:MaxConcurrentStarts默认4，Browser:StartIntervalMilliseconds默认100ms，单独控制同时启动数及相邻浏览器启动的最小间隔。启动速度可按磁盘能力调整，配置在应用重启后生效。

其他界面字段：TaskFetchInterval 为每批轮询后的等待毫秒数；PageLoadingDelay 为首次页面访问额外延时秒数；PageLoadingTimeout 为每次导航超时秒数；Multiple 为任务消费份数；IsHiddenMode 控制 headless；IsProxyMode 控制是否领取代理；IsCacheMode 控制共享缓存。

## Android 屏幕参数

设备接口的 sw/sh 按真实物理分辨率传入 AndroidViewportMatcher。可选 devicePixelRatio、deviceScaleFactor、dpr（按此顺序取首个非空值，支持数值或字符串）作为真实缩放比例，优先使用；不能用DPI数值代替DPR。缺失时才按分辨率和稳定型号种子估算，日志明确显示 DprSource=device/estimated。物理分辨率本身不能唯一反推出真实CSS尺寸。每UV只匹配一次，Chrome启动参数和Context的viewport/screen/DPR共享结果。

估算保留普通手机优先档位，并覆盖低分辨率、旧款短屏、宽比例/方屏、平板与4K；优先档位不适合时扩展DPR搜索。取消CSS高度至少600和普通手机比例的硬拒绝，宽比例屏幕允许更大的CSS宽度。宽高都按同一DPR取整，不再单独吸附CSS宽度。仅在超出估算覆盖范围时保留回退并记录具体原因；合理的方屏比例不视为错误。当前资源保护要求换算视口每边1～8192像素。仍保持既有竖屏归一化约定，同输入跨运行匹配稳定。

仅Business:ExportRunData=true时保存viewport.json及result.json中的匹配信息，默认不落盘。匹配结果包含来源、策略及覆盖限制原因。

真实 Chrome 验证读取 screen.width/height、window.innerWidth/Height、devicePixelRatio，与匹配记录比较；视口测试页面包含 width=device-width 的 meta viewport。

## 执行协议

任务 GET 请求统一设置一个 action=getTask 和一个经过编码的 name，保留其他查询参数。success=true/data 数组为任务；success=false、data=[]、message=没有可用任务为正常空批次。响应上限 4 MiB。

uv 为独立会话数，pv 为每个 UV 的总访问次数（pv=3 是首次访问加两次刷新）；首次导航到任务 URL，后续 Reload 当前页面，重定向后刷新落地页面。同 UV 共享页面、Cookie、存储与缓存；不同 UV 的设备会话、Context 和 userDataDir 独立。不要求 PV 每次回源，不禁用或清空原生缓存。sleep 接受固定秒数或 min-max 范围，每次页面访问后独立抽样；首页加载延时只作用于第一次访问。jstext 为可选对象或对象 JSON 字符串，注入 window.__trafficVistaTask 交由页面读取。

Multiple 使用惰性批次，每一份有独立 DeliveryId；不会因为同一服务器 TaskId 而去重。Business:MaxExpandedBatchTasks（默认 10000）和 MaxExpandedBatchMegabytes（默认 16）分别约束展开后的数量与估算 JSON 大小。超限批次与无法解析的响应保存在 business/recovery，供人工核查。

每任务先等待一个首UV并发名额，再持久化消费确认并领取新代理。该任务全部 UV 共用此出口，各任务不共享代理。代理连接保留协议、认证、领取时间和供应商截止时间；IpTtl 从领取时刻计算，与供应商截止时间取较早者。IpTtl=0 仅关闭界面时长限制，供应商截止时间仍生效。IpInfo 竞速只接受带可解析 ip/query/origin 的 JSON；所有落败请求取消并清理后才释放许可。请求超时覆盖连接、响应头和正文。直连领取请求复用连接池；每次代理查询有独立通道，内部竞速共享该不可变代理通道。

## 任务地区与 IP 环境

右侧任务摘要显示本轮累计任务提交/成功/失败/取消、UV总完成量与总完成吞吐、成功吞吐和最久在途阶段。双击或右键摘要打开“本轮累计统计 / 调度诊断”，每秒刷新任务排队/准入/执行数量、已消费任务UV计划/投递/完成/未投递终止数量、各阶段数量与最久持续时间、阶段平均耗时及失败数、单UV历史内存峰值（仅参考）、提交内存余量和真实并发硬上限。统计在新一轮启动时重置，不是跨重启历史累计；总完成包含成功、失败及取消，已终止任务的未投递UV单独显示，不能算已执行。

资源承压只暂停新的任务接口请求并输出暂停/恢复日志，已取回任务继续按并发池投递。取消了整任务名额预留、历史内存峰值缩减并发和旧任务尚未投递完就阻塞新任务的规则。诊断显示使用中/空闲UV名额和各阶段等待；页面浏览结束正文不可见会明确报UV失败。

隐藏模式支持在启动过程/运行中直接修改并按原配置保存流程持久化。新任务开始执行时读取最新值，包含修改时仍在排队或等待资源的任务；已经开始执行的任务及其未投递UV保持本任务原有快照，不切换已打开的浏览器、不重建调度准入。每个UV的result.json记录hiddenMode便于核对。

浏览器生命周期由 BrowserLifetimeGuard 监听业务页面Close/Crash、Context.Close与Browser.Disconnected。非预期关闭会立即取消当前UV的页面操作、PageLoadingDelay及sleep，完成所属Chrome进程/profile清理后释放UV名额；不会停止其它UV，也不会重投已消耗的UV。正常finally清理前解除监听，避免正常关闭误报。外部停止/IP到期仍保持取消分类；页面或浏览器非预期退出记录为失败，并在result.json写入browserInterrupted及browserInterruptionReason。关闭和崩溃可能触发相同断开事件，日志不会声称能精确辨别是否由用户关闭。[Playwright页面关闭事件](https://playwright.dev/dotnet/docs/api/class-page#page-event-close)、[浏览器断开事件](https://playwright.dev/dotnet/docs/api/class-browser#browser-event-disconnected)。

`--browser-close-test` 使用隔离的真实Chrome和本地页面，模拟首次60秒加载延时中关闭页面、60秒sleep中关闭页面、浏览器退出、外部取消及正常完成，验证及时中断、失败/取消分类和所属进程/浏览器计数归零。

PV 执行顺序：首次导航等待 DOMContentLoaded 后验证可见正文/图形内容，后续刷新仍等待 load → 仅首次 PV 等待 PageLoadingDelay → OnPageLoadedAsync 页面操作占位入口 → 停留任务 sleep 秒。入口在 BusinessUvBrowserRunner 内，可使用当前 IPage、BusinessUvRun 和 PV 序号添加操作；必须等待操作完成，再开始 sleep。取消及 IP 到期会中断后续操作和停留。

首次导航在 InitialPageNavigation 中处理：慢图片等资源不再阻塞整个 load；PageLoadingTimeout 内导航完成且有可见内容就静默继续，包含未识别为网络/代理错误的非2xx响应，不因其它资源仍在加载而提示。只有导航超时后主文档已响应且页面存在可见内容的放宽继续才记录加载提示。主文档连接/DNS/代理隧道错误、HTTP407、Chrome错误页、常见代理/网关错误正文、空白页仍失败；子资源失败不单独判主页面失败。可见性检查最多额外2秒，取消不转换为成功。提示同时写日志和 result.json 的 navigationWarnings。识别按已列举错误特征，不能保证覆盖每个供应商自定义错误页；页面可打开并不表示其业务脚本或后台统计已经完成。后续PV仍保留原有load及HTTP成功要求。[Playwright导航与HTTP状态说明](https://playwright.dev/dotnet/docs/api/class-page#page-goto)。

代理供应商另增 `shenlong` / `xingyu`：分别在 ShenlongProxyAdapter / XingyuProxyAdapter 内拥有请求构造与响应解析入口，已注册到路由。两家当前支持 TXT 的 ip:port 或 user:pass@ip:port；未确认的地区、数量与 JSON 规则保留 TODO，不复用 Ipzan 的供应商参数或地区表。JSON 响应会明确报协议未配置，不误解析为 TXT。真实出口 IP 缺失保持 null，统一按真实IP/IP详情开关补查一次。

两家在 appsettings.json 的 Net:Vendors 中默认关闭。接入时在本机配置填写对应 ApiUrl、Enabled=true，并将 Net:DefaultVendor 改为对应标识；界面的 ProxyIpUrl 非空时会覆盖所选供应商的 ApiUrl，因此也需改为同一家提取接口。真实IP请求参数仅在填写已确认的 RealIpParameterName 及 On/OffValue 时修改。地区、数量及 JSON 需要先在各自适配类补齐，当前没有供应商选择下拉框。

领取 IP 时传入完整 TaskEnvelope、TrafficVistaOptions 配置快照及 Count（默认 1）。任务 Payload 保留地区字符串、数组和未知字段；如 `北京[0],上海[2],湖南[23]:长沙[267]`。每家供应商在自己的适配器（IpzanProxyAdapter / VendorBProxyAdapter）中实现 BuildRequestUrl，独立处理地区格式、数量字段和真实IP参数。VendorB 的地区和数量规则仍留占位。Ipzan 已按确认协议实现：任务 address 可为逗号分隔字符串或字符串数组，多个有效地区随机选一个；省市组合校验省级代码前缀，指定城市无匹配不降级为全省；明确 全部/all 清除旧 area，没有 address 则保留原 URL 定向。全部指定地区无法映射时领取失败，不静默发非定向请求。地图从应用目录 data/net/ipzan/regions.json 延迟加载一次，随构建/发布复制；文件更改后重启加载。

Ipzan 以精确查询参数名替换 area/num/format/realIp，保留其他编码参数与 URL 片段；Count=1 也覆盖 URL 中旧数量。开启真实IP强制 format=json、realIp=1；关闭则 format=txt、realIp=0，覆盖残留的旧开关。数量字段已启用，但业务默认仍为1，现有单代理结果只选择返回列表中的第一个有效代理，不承诺批量领取后的分发。

界面“真实IP”（ResolveRealIp，默认开启）：供应商明确返回有效出口 IP 时直接采用，否则经该代理查询 IP 信息；不把代理连接 Host 当作真实出口。“IP详情”（FetchIpDetails，默认关闭）：即使供应商提供真实 IP，也必须查询完整详情。两个选项都关闭则跳过查询。VendorB 可配置 RealIpParameterName / RealIpParameterOnValue / RealIpParameterOffValue 修改开关，以及 RealIpJsonPath 提取出口字段。Ipzan 使用确认的固定字段：data.list[].ip/port 为连接地址，real_ip 为出口IP，expired 为毫秒 Unix 绝对截止时间，未给 expired 才用 DefaultTtlSeconds；跳过地址/端口/截止时间无效及已过期条目，无可用条目即领取失败。按实际正文识别 JSON/TXT，兼容供应商仍返回文本的情况。

查询通过刚领取的代理同时请求配置中的四个 IP 信息接口，首个包含有效 ip/query/origin 的 JSON 胜出，其余取消并清理。全部失败记日志并丢弃该代理，按 AcquireRetryCount 重新领取新 IP；调用方取消不重试。仅为补全真实IP而查询时，不应用地理详情。勾选 IP详情 时完整 JSON 传入 BrowserIpEnvironment，设置有效时区、经纬度及定位权限，并保存 ip-environment.json；未知字段原样保留，无效或缺失字段跳过。真实IP传入现有 Chrome 的 webrtc-ip 参数；这是浏览器环境设置，不改变网络路由，实际网络出口仍由代理决定。

未勾选代理模式时也支持出口信息：勾选真实IP或IP详情即通过本机直连竞速请求同一组接口，每任务查询一次；两个都不勾选则跳过。直连查询与代理查询共用并发上限、正文超时及胜出取消机制。查询结果仅携带真实IP和详情（Server=null），不会调用供应商接口、创建浏览器代理或应用代理IpTtl。仅勾真实IP时传入真实IP；勾IP详情时同时应用有效时区、经纬度与定位权限。直连全部接口失败会记录日志并使本任务失败，不通过领取代理替代本机出口；调用方取消原样传播。

## 并发池与资源暂停

全局Semaphore控制UV并发，任务先占一个首UV名额后才确认消费和领取IP；后续UV从共享池逐个取得空闲名额，完成所有清理后归还。多个任务可以同时投递，各自独立计时UV间隔，互不挡住空闲容量。当前任务还有未投递UV时，也允许其它任务开始。不按CPU核心数或单浏览器历史内存峰值缩减设定并发。

CPU趋势85%或物理/提交可用内存低于max(512MiB,总物理内存10%)时暂停新任务接口请求；CPU回落70%且内存恢复到保留值1.25倍后恢复。采样不可用也暂停获取并输出原因。压力不再阻止已取回任务的准备和执行；队列仍按并发×倍率×2保持有界。资源等待不计HTTP请求超时，暂停/恢复转换各输出一次日志。

浏览器启动速度独立控制：默认同时最多4个启动，相邻启动至少100ms，不影响已启动的浏览器持续运行。UV间隔是任务投递的最小间隔，启动排队、设备接口及UV名额不足会造成额外延迟。12个UV各30秒、任务间隔1秒、空闲并发足够时，理想完成时间为30+11=41秒；多个任务竞争容量时耗时会增加。

耗时学习只用于提示，不用于预留整任务名额。IP截止仍取界面限制和供应商实际截止时间的较早者；到期取消在途任务，不重领IP、不重投已消耗UV。summary.json中initialReservedSlots为首UV的1个名额，reservedSlots记录本任务实际占用名额的峰值；预计耗时基于并发可用的理想情况，不承诺完成期限。

## 停止与恢复

业务 Chrome 由 Playwright 启动后，使用该浏览器内部 CDP 查询主进程 ID，再通过系统父子进程快照将启动时已有的辅助进程一并纳入 Windows Job Object；已有沙箱进程必要时使用独立嵌套 Job，后续子进程继承归属，不通过名称查杀其它 Chrome。独立 profile 和进程树均属于当前 UV。取消关联进程树终止，同时取消等待中的浏览器 API；正常关闭也结束所有子进程。启动默认30秒、进程归属设置10秒、Context关闭和进程退出各有独立上限。启动 API 不支持 token 时，取消会等待有界启动任务完成并关闭迟到的浏览器后再释放启动名额。Context 优雅关闭超时后，继续强制结束并确认自有 Job 全部退出；强制清理出错仍记失败，优雅关闭慢本身不会把已完成访问误记失败，结果保存 gracefulContextCloseTimedOut。迟到的关闭异常会被观察。临时profile由Playwright驱动管理；profileCleanupDeferred保留为旧结果字段，当前路径不生成自有profile-cleanup.pending。历史运行的延迟清理标记仍由会话维护处理。

Business:TaskRecoveryMode 默认 Discard：取回的任务进入有界内存队列，停止、异常退出后不恢复，也不读取历史收件箱。设为 Save 时，客户端先保存整批交付再入队；停止时排队、首UV名额等待及尚未入队的批次尾部会保留，下次同任务接口和名称启动时恢复，并使用本次启动的执行配置。已开始执行的任务（包括执行中取消、失败和进程崩溃）不自动重放，避免重复执行已完成UV。此配置替代旧 PersistPendingTasks 字段，修改后重启生效。切回 Discard 不自动删除旧收件箱。

Business:ExportRunData 默认 false：不导出 task/device/viewport/IP环境/result/summary/storage-state，也不做归档保留扫描。Cookie、localStorage 和同UV多个PV的会话由同一浏览器Context维护，无需导出。Chrome临时profile由Playwright在系统临时目录创建并在浏览器关闭后清理；TrafficVista/uv只用于临时业务运行目录。显式设为 true 才保存业务运行归档；会话快照不收集IndexedDB，导出最多等待3秒，失败只提示并继续关闭浏览器。诊断阶段分别显示正文检查、导出会话、缓存收尾。

启动限速名额持续占用到Context、页面、CDP环境及初始化全部准备完成，之后页面正常运行不占启动名额。Business:BrowserSetupTimeoutSeconds 默认20秒，Chrome启动后上述准备共用此超时，与页面导航超时分开；超时或取消先终止该UV的Chrome进程树，再放行下一启动，最终清理释放UV并发名额。诊断拆分会话创建、页面创建、CDP环境设置、页面初始化，超时日志记录TaskId/UvId及具体失败阶段。

代理模式下，主文档出现明确代理通道错误（ERR_PROXY_CONNECTION_FAILED、ERR_TUNNEL_CONNECTION_FAILED、SOCKS连接/目标不可达、代理认证/证书/配置协议错误等）或HTTP 407，一次即停止该任务后续UV投递。仅从浏览器主文档失败事件或导航Playwright异常提取已知错误码，不按普通错误字符串判断，也不因图片/JS加载失败中断任务。直连不启用此规则。ERR_CONNECTION_RESET/REFUSED/TIMED_OUT/NAME_NOT_RESOLVED等通用错误以及502/504可能来自目标网站，不单凭这些认定代理不可用。已投UV继续完成，其他任务继续；任务摘要原因是proxy-unavailable:<错误码>。代理获取失败/到期仍按原有规则处理。

同一任务内，按UV完成顺序连续3次主文档HTTP 644失败后，停止尚未投递的UV；等待UV名额/投递间隔也立即结束，已投出的UV继续正常完成和清理。成功、非644失败会清零连续次数；其他任务和同ID的下一轮消费各自独立。失败任务摘要原因记为consecutive-http-644，未投UV单独计数，不冒充取消或已完成。首次导航、刷新及结束检查的644保留类型化状态，识别不依赖错误文本；确定644后无需继续等待空白页超时。`--http644-test`覆盖阈值、清零、任务隔离、在途UV保留及真实Chrome导航。

任务按已确认的消耗性规则处理：失败后记录结果并继续下一个，不自动重投已开始或失败的任务，无需服务端确认或租约。队列容量每轮启动按界面“并发数 × 倍率 × 2”计算，队列满时等待空位并暂停获取下一批。这提供客户端待执行恢复与一次性领取，远程领取成功但本地尚未落盘仍有崩溃窗口，不宣称端到端 exactly-once。恢复顺序不承诺原始批次顺序。第二个进程使用同一收件箱会被排他锁拒绝；如需同机多个独立调度器，应使用不同运行目录。

## 缓存与保留

IsCacheMode=false 时不安装资源拦截，字体和视频正常加载，Chrome 使用原生 HTTP 缓存并允许 Service Worker；同 UV 的 Cookie/localStorage 和缓存持续保留，不同 UV 独立。此前字体/媒体的禁止加载 Route 已按最新要求移除。

IsCacheMode=true 时，共享磁盘缓存接收非 GIF 图片、CSS、JS、字体和视频（含音轨及常见 HLS/DASH 分段请求）。GIF 按 URL 扩展名（忽略大小写和查询参数）、image/gif MIME 与 GIF87a/GIF89a 文件头识别，正常走网络、不入库。支持 gzip/br 等浏览器已解码响应、chunked/无 Content-Length 的完整响应，不设单资源大小上限，不再施加2秒正文捕获超时；UV 取消仍会终止捕获。Playwright 的 BodyAsync 与文件回填接口仍会缓冲响应，超大资源实际受可用内存、磁盘和框架能力约束，不能保证恒定内存占用。

只缓存 GET 的200响应，以及具备有效Content-Range、明确Range请求且字节长度匹配的206响应；视频每个Range/If-Range组合独立成键，不把分段作为完整文件回填，压缩206不缓存。普通资源仅按完整URL（含查询参数）的SHA256定位；Cookie、UA、其他协商请求头和代理出口不参与键，相同URL跨UV共享同一正文。URL不变时，即使不同身份或服务端返回内容已改变，到期前仍复用已存正文。浏览器Context、Cookie及存储仍独立；回填剔除Set-Cookie，避免把响应Cookie写入其他UV。共享缓存不再受Cache-Control（包括private/no-store/no-cache/max-age）、Expires、Age、Date、Vary或Authorization限制。命中不重复捕获、不延长首次写入的配置TTL。

缓存位于StaticCacheRoot/v4。正文文件和原子元数据配合读取租约，避免覆盖/容量清理破坏活跃回填；保留Content-Type字符集和206的Content-Range，移除压缩、分块等传输头。StaticCacheMaxMegabytes默认512，作为总容量软预算淘汰较旧条目；单个新文件超过预算仍保留，不因此拒绝大资源。TTL仅使用StaticCacheTtlHours（默认24小时），从资源写入时间计算；修改配置并重启后，现有v4条目也按原写入时间加新时长判断。到期后下次访问重新下载；容量清理仍可能提前淘汰条目。旧v1/v2/v3缓存不复用，旧目录不自动删除。最多两个资源同时捕获，繁忙时绕过；未下载完成的响应不写入。共享缓存依靠Route实现，会禁用原生HTTP缓存并阻止Service Worker；关闭此模式才使用原生缓存。[Playwright Route说明](https://playwright.dev/dotnet/docs/api/class-browsercontext#browser-context-route)。

`--static-cache-test` 使用本地真实Chrome验证大图片、gzip JS、chunked CSS、字体、视频完整响应与两个206分段的跨UV复用、GIF排除、跨Cookie/UV同URL复用、配置TTL覆盖服务器策略、过期边界/重启配置、取消写入和活跃读取租约。视频用例验证HTTP内容与Range回填，不验证所有媒体编码格式的实际播放。
SessionRetentionDays 默认 7，SessionMaxMegabytes 默认 1024；每小时检查带 run.lock 与 summary.json 的已完成运行，按期限与容量清理。活跃锁、链接路径、历史未标记 profile 都不会自动删除。延迟删除的临时 profile 在清理周期重试。静态缓存有独立容量上限。

## 观测与验证

浏览器归零但系统内存仍高时，在出现问题的机器运行随程序附带的 `Collect-MemoryDiagnostics.ps1`。它读取所有会话的进程驻留/私有提交量及系统内存池、缓存、修改页、待机页；默认只显示控制台输出，不清理内存、不终止进程、不写报告。示例：`powershell -NoProfile -ExecutionPolicy Bypass -File .\Collect-MemoryDiagnostics.ps1`。需要显式保存JSON时传 `-OutputPath .\memory-report.json`（拒绝覆盖已有文件），或使用 `-AsJson` 仅输出JSON。受保护进程可能缺少计数，null表示不可用；内存类别有重叠，不能把进程工作集简单相加或相减认定泄漏。待机页已包含在可用物理内存中；若内存池/活跃缓存无法解释差额，需在故障机器用微软RAMMap查看Driver Locked、Page Table、Mapped File等分类。采集脚本在开发机验证过，不代表远程正式机器结果。

页面内容检查不再以body自身尺寸判断成功：识别可见文字、控件、媒体和可见iframe的实际内容，支持body零高度的绝对定位布局；隐藏内容、空iframe和iframe内错误页不算有效内容。首次DOMContentLoaded后会在PageLoadingTimeout剩余时间内等待动态渲染；每次检查调用独立限时2秒，导航超时后仍允许最后一次检查已有内容。浏览结束后综合实际URL、主文档响应/网络失败与内容，整个最终检查最多3秒；检查调用超时和页面确实为空分开报错，随后清理该UV浏览器。Chrome网络错误、代理407及常见网关错误仍判失败，正常非2xx有内容页保留现有策略。

右上角“投N/分”统计最近60秒实际投递的UV（按秒统计，不是计划量或完成量；不足60秒显示实际投递数量），并显示本轮累计投递数。用最多60个秒桶汇总，空闲时过期归零，新轮次启动重置。原“总/分、成/分”为本轮运行平均完成/成功速率，详细窗口明确区分统计口径。

右侧统计栏每项指标独占一行，显示速率、UV累计量、任务累计量、并发名额和最慢阶段。统计栏独立使用窗体右侧高度，小窗口可滚动；日志保留在左侧，双击统计仍可打开详细诊断。

UI 分别显示任务、UV 与浏览器数量，并显示 UV 成功/失败/取消、成功 UV/min、最近 1024 个样本的 P95 和待恢复数量。UV 延迟从获得许可后的设备准备开始，不包含此前排队；平均阶段耗时包括 UV 排队、代理、设备、启动、导航与清理。result.json 记录实际 PV、失败阶段、阶段耗时、单 UV Chrome Job 的峰值提交内存；需要多个启动子进程 Job 时，记录各 Job 峰值之和，作为保守上界。启动计时额外拆出 browserLaunchMs（启动和进程归属设置）与 browserSetupMs（页面及环境初始化），保留 browserStartMs 总准备耗时。

准入状态显示暂停原因、预留名额、等待准入任务和已准入任务尚未投递的 UV 数；CPU、可用内存和新任务内存名额可悬停查看。尚未准入的排队任务 UV 不计入“待投递UV”。自动学习仅在当前轮次有效，下轮重新采样与校准。

Logging:FileEnabled 默认 false，实时日志仍显示在界面；显式开启后交互文件日志位于 logs。自检显式保存日志到 demo-output/logs，标明 RunId 与 self-test。界面详细日志开关控制 Debug/Information。

底部状态栏四项分别显示应用状态、系统逻辑CPU数量/趋势使用率、已用/总物理内存及使用率、本应用所属Chrome Job的进程数。浏览器进程数包括主进程、渲染器、GPU等子进程，包含启动/清理阶段，不统计其他用户浏览器；悬停显示UV会话数。CPU/内存与任务准入共享后台缓存，进程计数也在后台查询，UI每秒刷新一次；不可用时显示采样中或不可用而不伪造0%。逻辑CPU数量涵盖所有处理器组，多组机器CPU使用率通过Processor Information总计数器采样；单组机器保留轻量GetSystemTimes差分。多组计数器失败时暂停新任务，不退回不完整的组内CPU数据。

“开机启动”点击后在当前用户Startup目录创建/更新TrafficVista.lnk，目标为当前应用，工作目录为应用目录，参数追加 --auto-start；启用后链接显示✓，创建后打开Startup目录供查看。程序收到参数后，在窗体Shown、配置及按钮初始化完成后自动PerformClick触发开始按钮，复用配置校验和调度启动，只执行一次；普通打开仍手动启动。--auto-start 按正常交互启动处理，日志和异常处理不进入自检模式。

创建成功后按实际应用目标清理当前用户/公共Startup中的其它快捷方式、HKCU/HKLM的32/64位Run/RunOnce，以及含开机/登录触发器且全部执行动作均指向本应用的计划任务。不按启动项名称删除其它程序；dotnet需精确匹配本应用dll。目标为其它安装路径、脚本封装或Windows服务的项目不会按名称猜测删除。权限不足或无法读取的项目记录清理警告，不阻止已验证的快捷方式生效；日志记录删除数量，UI提示部分清理失败。临时快捷方式放在系统临时目录，避免Startup出现第二份可执行启动项。

重复点击更新同一个快捷方式，COM操作在后台STA线程执行。dotnet宿主情况下优先指向同目录MainClient.exe，未提供exe则指向dotnet和应用dll，不复制自检参数。“应用目录”点击通过资源管理器打开AppContext.BaseDirectory。`--desktop-test` 在隔离目录验证自动启动参数、同目标旧快捷方式清理、其它程序保留与真实Chrome进程计数；`--form-test` 用空任务HTTP桩验证自动触发按钮及仅触发一次，不修改真实Windows启动目录/注册表/计划任务。

Windows启动项与计划任务的接口依据：[Run/RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)、[计划任务触发器](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-triggers-tasktype-element)。

MainForm 下方实时日志使用虚拟 ListView，只创建可见行；日志生产线程向有界队列 TryWrite，不操作控件或排队 BeginInvoke。专用后台工作任务格式化消息和异常，待格式化最多512条、格式化历史及界面各最多2000条、每条显示最多4096字符。UI每200ms最多读取128条批量更新，双缓冲绘制；超过缓冲上限淘汰旧的显示副本并显示计数，文件日志保留原来的记录行为和滚动策略。单条超长日志仅在界面截断，原文查看日志文件。

可暂停显示、关闭“跟随最新”以查看历史、清空显示；双击一条日志查看异常等详情。暂停不阻止后台日志写入，恢复时仅保留有限历史；关闭或隐藏窗体停止刷新，释放控件时释放Timer，退出时Serilog释放后台Sink。`--log-test` 验证并发突发/持续高频日志、有界保留及UI心跳；`--form-test` 验证实际MainForm接线及布局，测试不启动浏览器或调用真实接口。

```powershell
dotnet build src/MainClient/MainClient.csproj
dotnet run --project src/MainClient/MainClient.csproj -- --settings-test
dotnet run --project src/MainClient/MainClient.csproj -- --scheduler-test
dotnet run --project src/MainClient/MainClient.csproj -- --json-test
dotnet run --project src/MainClient/MainClient.csproj -- --net-test
dotnet run --project src/MainClient/MainClient.csproj -- --form-test
dotnet run --project src/MainClient/MainClient.csproj -- --exception-test
dotnet run --project src/MainClient/MainClient.csproj -- --reliability-test
dotnet run --project src/MainClient/MainClient.csproj -- --admission-test
dotnet run --project src/MainClient/MainClient.csproj -- --window-test
dotnet run --project src/MainClient/MainClient.csproj -- --viewport-test
dotnet run --project src/MainClient/MainClient.csproj -- --benchmark
```

--business-test 是 --reliability-test 的兼容入口，全部使用隔离的本地页面、设备与代理桩。浏览器验证需要已安装的本地 Chrome；可通过 TRAFFICVISTA_TEST_CHROME 指定验证用可执行文件。常规业务路径由 Business:ChromeExecutablePath 指定。--benchmark 在本地零停留页面分别测试并发 1/2/4/8，每组 8 UV；输出 JSON 包含吞吐、P50/P95、活跃浏览器及内存指标，不代表真实代理与目标页面吞吐。

演示 ChromeInstanceManager/ChromeDemo 与 LocalUv 链路不接入业务任务；演示同用户会话恢复能力不能当成业务 UV 自动重试能力。
