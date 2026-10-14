# TrafficVista

Windows / .NET 8 WinForms 客户端；每个 UV 使用独立 Chrome 进程及浏览器 Context。修复与实测记录见 ../IMPROVEMENTS.md。

业务 Chrome 启动使用 --no-startup-window，不创建默认 profile 的 about:blank 窗口；独立 Context 的业务页面是该 UV 唯一由客户端创建的页面/窗口。窗口回归通过 CDP 枚举页面目标及可见窗口 ID，覆盖可见/隐藏模式，避免并发 1 时一个 UV 显示两个窗口。

## 配置

基础默认值为 appsettings.json；本机凭证可放在 appsettings.private.json（Git 忽略、开发构建复制、发布不复制）；最后由 appsettings.user.json 覆盖界面项和 Net 配置。用户文件由界面保存，构建/发布不会覆盖。私有配置仅合并 TrafficVista 与 Net 节。仓库默认代理 URL 使用 CONFIGURE_LOCALLY 占位符，真实账号、密钥已迁移到本机私有文件。

界面“并发数量”表示全局 UV 许可上限；许可包含设备准备、浏览器启动等待与执行。界面“任务Worker数”（TrafficVista:TaskWorkerCount）独立控制任务工作协程数量，范围1～1024，默认4，支持50、96等值，保存并在下一次启动生效；运行中不调整Worker数量。旧配置没有新字段时沿用Business:MaxConcurrentTasks。Worker等待准入时不创建浏览器、不领取IP；CPU/内存准入动态限制实际开工任务，Worker数不会突破UV并发上限或改变队列容量。因此按CPU核心数设置Worker是可选的手动配置方式，不意味着浏览器数量跟随CPU核心数。Browser:MaxConcurrentStarts 默认 4，仅限制同时启动；UvExecutionInterval 表示跨所有任务的全局 UV 启动间隔，单位毫秒。

其他界面字段：TaskFetchInterval 为每批轮询后的等待毫秒数；PageLoadingDelay 为首次页面访问额外延时秒数；PageLoadingTimeout 为每次导航超时秒数；Multiple 为任务消费份数；IsHiddenMode 控制 headless；IsProxyMode 控制是否领取代理；IsCacheMode 控制共享缓存。

## Android 屏幕参数

设备接口的 sw/sh 按物理分辨率传入 AndroidViewportMatcher.Match(sw, sh, make, model)。每 UV 获取设备后只匹配一次，使用同一份 CssWidth/CssHeight/DeviceScaleFactor 生成 Chrome 的 window-size、screen-size、screen-avail-size、device-pixel-ratio，以及 Context 的 ViewportSize、ScreenSize、DeviceScaleFactor。已移除固定 DPR=3/3.5 的粗略换算。

每个 UV 保存 viewport.json，包含输入物理分辨率（按 matcher 归一化）、CSS 尺寸、DPR、评分与回退/异常比例标记；result.json 同时记录该匹配结果。回退或异常比例写入警告，沿用 matcher 返回值。同 make/model/分辨率匹配稳定；横向输入遵循现有 matcher 的竖屏归一化规则。匹配器算法保持原样。

真实 Chrome 验证读取 screen.width/height、window.innerWidth/Height、devicePixelRatio，与匹配记录比较；视口测试页面包含 width=device-width 的 meta viewport。

## 执行协议

任务 GET 请求统一设置一个 action=getTask 和一个经过编码的 name，保留其他查询参数。success=true/data 数组为任务；success=false、data=[]、message=没有可用任务为正常空批次。响应上限 4 MiB。

uv 为独立会话数，pv 为每个 UV 的总访问次数（pv=3 是首次访问加两次刷新）；首次导航到任务 URL，后续 Reload 当前页面，重定向后刷新落地页面。同 UV 共享页面、Cookie、存储与缓存；不同 UV 的设备会话、Context 和 userDataDir 独立。不要求 PV 每次回源，不禁用或清空原生缓存。sleep 接受固定秒数或 min-max 范围，每次页面访问后独立抽样；首页加载延时只作用于第一次访问。jstext 为可选对象或对象 JSON 字符串，注入 window.__trafficVistaTask 交由页面读取。

Multiple 使用惰性批次，每一份有独立 DeliveryId；不会因为同一服务器 TaskId 而去重。Business:MaxExpandedBatchTasks（默认 10000）和 MaxExpandedBatchMegabytes（默认 16）分别约束展开后的数量与估算 JSON 大小。超限批次与无法解析的响应保存在 business/recovery，供人工核查。

每任务先等待资源并预留执行名额，再持久化消费确认并领取新代理。该任务全部 UV 共用此出口，各任务不共享代理。代理连接保留协议、认证、领取时间和供应商截止时间；IpTtl 从领取时刻计算，与供应商截止时间取较早者。IpTtl=0 仅关闭界面时长限制，供应商截止时间仍生效。IpInfo 竞速只接受带可解析 ip/query/origin 的 JSON；所有落败请求取消并清理后才释放许可。请求超时覆盖连接、响应头和正文。直连领取请求复用连接池；每次代理查询有独立通道，内部竞速共享该不可变代理通道。

## 任务地区与 IP 环境

PV 执行顺序：打开或刷新当前页面并等待 load 事件（不是 networkidle）→ 仅首次 PV 等待 PageLoadingDelay → OnPageLoadedAsync 页面操作占位入口 → 停留任务 sleep 秒。入口在 BusinessUvBrowserRunner 内，可使用当前 IPage、BusinessUvRun 和 PV 序号添加操作；必须等待操作完成，再开始 sleep。PageLoadingTimeout 约束导航至 load 的等待，加载失败不进入操作或停留阶段。取消及 IP 到期会中断后续操作和停留。

代理供应商另增 `shenlong` / `xingyu`：分别在 ShenlongProxyAdapter / XingyuProxyAdapter 内拥有请求构造与响应解析入口，已注册到路由。两家当前支持 TXT 的 ip:port 或 user:pass@ip:port；未确认的地区、数量与 JSON 规则保留 TODO，不复用 Ipzan 的供应商参数或地区表。JSON 响应会明确报协议未配置，不误解析为 TXT。真实出口 IP 缺失保持 null，统一按真实IP/IP详情开关补查一次。

两家在 appsettings.json 的 Net:Vendors 中默认关闭。接入时在本机配置填写对应 ApiUrl、Enabled=true，并将 Net:DefaultVendor 改为对应标识；界面的 ProxyIpUrl 非空时会覆盖所选供应商的 ApiUrl，因此也需改为同一家提取接口。真实IP请求参数仅在填写已确认的 RealIpParameterName 及 On/OffValue 时修改。地区、数量及 JSON 需要先在各自适配类补齐，当前没有供应商选择下拉框。

领取 IP 时传入完整 TaskEnvelope、TrafficVistaOptions 配置快照及 Count（默认 1）。任务 Payload 保留地区字符串、数组和未知字段；如 `北京[0],上海[2],湖南[23]:长沙[267]`。每家供应商在自己的适配器（IpzanProxyAdapter / VendorBProxyAdapter）中实现 BuildRequestUrl，独立处理地区格式、数量字段和真实IP参数。VendorB 的地区和数量规则仍留占位。Ipzan 已按确认协议实现：任务 address 可为逗号分隔字符串或字符串数组，多个有效地区随机选一个；省市组合校验省级代码前缀，指定城市无匹配不降级为全省；明确 全部/all 清除旧 area，没有 address 则保留原 URL 定向。全部指定地区无法映射时领取失败，不静默发非定向请求。地图从应用目录 data/net/ipzan/regions.json 延迟加载一次，随构建/发布复制；文件更改后重启加载。

Ipzan 以精确查询参数名替换 area/num/format/realIp，保留其他编码参数与 URL 片段；Count=1 也覆盖 URL 中旧数量。开启真实IP强制 format=json、realIp=1；关闭则 format=txt、realIp=0，覆盖残留的旧开关。数量字段已启用，但业务默认仍为1，现有单代理结果只选择返回列表中的第一个有效代理，不承诺批量领取后的分发。

界面“真实IP”（ResolveRealIp，默认开启）：供应商明确返回有效出口 IP 时直接采用，否则经该代理查询 IP 信息；不把代理连接 Host 当作真实出口。“IP详情”（FetchIpDetails，默认关闭）：即使供应商提供真实 IP，也必须查询完整详情。两个选项都关闭则跳过查询。VendorB 可配置 RealIpParameterName / RealIpParameterOnValue / RealIpParameterOffValue 修改开关，以及 RealIpJsonPath 提取出口字段。Ipzan 使用确认的固定字段：data.list[].ip/port 为连接地址，real_ip 为出口IP，expired 为毫秒 Unix 绝对截止时间，未给 expired 才用 DefaultTtlSeconds；跳过地址/端口/截止时间无效及已过期条目，无可用条目即领取失败。按实际正文识别 JSON/TXT，兼容供应商仍返回文本的情况。

查询通过刚领取的代理同时请求配置中的四个 IP 信息接口，首个包含有效 ip/query/origin 的 JSON 胜出，其余取消并清理。全部失败记日志并丢弃该代理，按 AcquireRetryCount 重新领取新 IP；调用方取消不重试。仅为补全真实IP而查询时，不应用地理详情。勾选 IP详情 时完整 JSON 传入 BrowserIpEnvironment，设置有效时区、经纬度及定位权限，并保存 ip-environment.json；未知字段原样保留，无效或缺失字段跳过。真实IP传入现有 Chrome 的 webrtc-ip 参数；这是浏览器环境设置，不改变网络路由，实际网络出口仍由代理决定。

未勾选代理模式时也支持出口信息：勾选真实IP或IP详情即通过本机直连竞速请求同一组接口，每任务查询一次；两个都不勾选则跳过。直连查询与代理查询共用并发上限、正文超时及胜出取消机制。查询结果仅携带真实IP和详情（Server=null），不会调用供应商接口、创建浏览器代理或应用代理IpTtl。仅勾真实IP时传入真实IP；勾IP详情时同时应用有效时区、经纬度与定位权限。直连全部接口失败会记录日志并使本任务失败，不通过领取代理替代本机出口；调用方取消原样传播。

## 自动准入与 IP 优先执行

应用生命周期内后台共享采样系统 CPU、可用物理内存与提交内存余量，每秒一次，准入和状态栏读取同一缓存，不在UI线程查询资源。首次等待约一秒取得 CPU 差分样本；任务重启复用现有系统采样，UV耗时/内存预算学习仍每轮重置。CPU 趋势达到 85% 时暂停新任务，降到 70% 以下才恢复；内存恢复也留有余量。采样失败显示原因并暂停新任务，不影响已有 IP 的投递。领取等待发生在 HTTP 超时计时之前，不会误算为接口失败。

初始每 UV 内存预算 384 MiB，随后取本轮观测的单 Chrome Job 峰值提交内存 × 1.25 与初始值的较大者；系统保留至少 512 MiB 或物理内存的 10%。同时扣除其他任务预留预算，按物理和提交余量的较小值计算可用名额。这是保守预算，不是对实际工作集的测量。

单 UV 时间由本轮最近 128 个成功样本的 P95 非停留耗时估计，包含设备、启动、页面与清理；再结合任务 PV、sleep 上界及首页延时，保留 20% 非停留耗时余量。冷启动每 PV 非停留基准为 5 秒。按估计耗时/UV间隔计算初始名额，间隔为零时使用资源允许的并发。总预留及实际 UV 数永远不超过界面并发硬上限。

已准入任务还有 UV 未投递时，其他任务不能领取新 IP，也暂停获取新批次。新任务首个 UV 的全局启动间隔也在领 IP 前等待，避免领到 IP 后等待上一任务的限速窗口。已有 IP 的任务继续按全局 UV 间隔投递；压力升高不收回其名额。耗时低估时可以优先扩展未预留的空闲名额，扩展不超过领 IP 前的内存预算和全局硬上限。全部 UV 已投递后，有空余预留名额才允许下一个任务准入。名额保留到真实清理结束，因此达并发上限时仍需等待浏览器退出；启动间隔是最小间隔，不保证到点一定启动。

IP 有效期结合界面限制及本轮最近 32 次已领取代理的剩余有效期估算；未知供应商有效期无法在首次领取前判断。估计不够完成的计划记录风险并尽力执行，不无限等待自身无法满足的有效期，也不重新领取 IP 或重投 UV。实际截止时刻仍负责取消并清理。summary.json 保存初始/最终预留数、估算耗时及已知有效期风险。

任务失败不自动重试；部分 UV 成功不会被撤销。每次消费创建 tasks/<TaskId>/<运行标识>，包含 task.json、summary.json；UV 子目录有 device.json、storage-state.json 与 result.json。代理失败、设备失败、取消及部分成功均有可排查记录。

## 停止与恢复

业务 Chrome 由 Windows Job Object 持有；独立 profile、CDP 端点与进程树均属于当前 UV。取消关联进程树终止，同时取消等待中的浏览器 API；正常关闭也结束所有子进程。浏览器连接、Context 关闭与进程退出有独立上限。Context 优雅关闭超时后，继续强制结束并确认自有 Job 全部退出；强制清理出错仍记失败，优雅关闭慢本身不会把已完成访问误记失败，结果保存 gracefulContextCloseTimedOut。迟到的关闭异常会被观察。文件被索引器/杀毒程序短暂占用时，保留 profile-cleanup.pending，后续会话清理重试，不把页面访问成功误判为失败。

Business:PersistPendingTasks 默认 true。客户端先将整批交付持久化，再入队；资源准备阶段没有消费确认，也不领取 IP。停止时排队、准入等待及尚未入队的批次尾部会保留，下次同任务接口和名称启动时恢复，并使用本次启动的执行配置。已经持久化消费确认的任务（包括执行中取消/进程崩溃）不自动重放，避免重复访问。使用全局唯一 DeliveryId，空收件箱落盘后可压缩超过 1 MiB 的领取日志。

任务按已确认的消耗性规则处理：失败后记录结果并继续下一个，不自动重投已开始或失败的任务，无需服务端确认或租约。队列容量每轮启动按界面“并发数 × 倍率 × 2”计算，队列满时等待空位并暂停获取下一批。这提供客户端待执行恢复与一次性领取，远程领取成功但本地尚未落盘仍有崩溃窗口，不宣称端到端 exactly-once。恢复顺序不承诺原始批次顺序。第二个进程使用同一收件箱会被排他锁拒绝；如需同机多个独立调度器，应使用不同运行目录。

## 缓存与保留

未开启可选共享缓存模式时，使用 Chrome 正常缓存策略，允许 Service Worker；同 UV 刷新可以命中原生缓存，不清 Cookie、不清缓存。当前用户配置 IsCacheMode=false 对应这一模式，而不是关闭 Chrome 原生缓存。

开启 IsCacheMode 时使用共享磁盘缓存，只接受明确 public + 正 max-age 的图片/CSS/字体响应；Cookie、Authorization、Range、Set-Cookie、private/no-store/no-cache、未知 Vary、未知长度、压缩正文、媒体及超过 1 MiB 的资源全部绕过。键包括 URL、UA、Accept、Accept-Language、Accept-Encoding、Origin 和代理出口信息。TTL 同时受响应剩余寿命与 StaticCacheTtlHours 约束；头和正文在一个 .entry 文件中原子发布，读写采用异步 I/O，最多两个缓存捕获同时进行。旧 v1 缓存不再复用。此模式借助 Playwright Route 拦截，框架会自动关闭原生 HTTP 缓存，并阻止 Service Worker；其缓存命中来自项目共享缓存。[Playwright Route 说明](https://playwright.dev/dotnet/docs/api/class-page#page-route)。

SessionRetentionDays 默认 7，SessionMaxMegabytes 默认 1024；每小时检查带 run.lock 与 summary.json 的已完成运行，按期限与容量清理。活跃锁、链接路径、历史未标记 profile 都不会自动删除。延迟删除的临时 profile 在清理周期重试。静态缓存有独立容量上限。

## 观测与验证

UI 分别显示任务、UV 与浏览器数量，并显示 UV 成功/失败/取消、成功 UV/min、最近 1024 个样本的 P95 和待恢复数量。UV 延迟从获得许可后的设备准备开始，不包含此前排队；平均阶段耗时包括 UV 排队、代理、设备、启动、导航与清理。result.json 记录实际 PV、失败阶段、阶段耗时、单 UV 整个 Chrome Job 的峰值提交内存。

准入状态显示暂停原因、预留名额、等待准入任务和已准入任务尚未投递的 UV 数；CPU、可用内存和新任务内存名额可悬停查看。尚未准入的排队任务 UV 不计入“待投递UV”。自动学习仅在当前轮次有效，下轮重新采样与校准。

交互日志位于 logs；自检日志位于 demo-output/logs，标明 RunId 与 self-test。自检不会混入交互日志。界面详细日志开关控制 Debug/Information。

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
