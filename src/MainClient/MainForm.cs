using Serilog;
using System.Diagnostics;
using System.Globalization;
using TrafficVista.Configuration;
using TrafficVista.Scheduling;
using TrafficVista.DesktopIntegration;
using TrafficVista.Business;

namespace TrafficVista;

public partial class MainForm : Form
{
    private readonly ILogger _logger;
    private readonly TaskSchedulingService _scheduler;
    private readonly TrafficVistaSettings _settings;
    private readonly ToolTip _statusToolTip;
    private bool _settingsDirty;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;
    private Task? _schedulerObservation;
    private readonly DesktopStatusMonitor _desktopMonitor;
    private readonly StartupShortcutService _startupShortcut;
    private bool _startupBusy;
    private int _startupRevision;
    private string? _operationError;
    private string? _transitionText;
    internal bool AutoStartRequested { get; set; }
    internal string? RestartResumeToken { get; set; }
    private bool _autoStartHandled;
    private SchedulingDiagnosticsForm? _diagnosticsForm;
    private long _restartStarted;
    private int _restartMinutes;
    private bool _restartInProgress;
    internal DesktopStatusSnapshot DesktopStatus => _desktopMonitor.Snapshot;

    public MainForm(ILogger logger, TaskSchedulingService scheduler, TrafficVistaSettings settings)
    {
        _logger = logger.ForContext<MainForm>();
        _scheduler = scheduler;
        _settings = settings;
        InitializeComponent();
        this.Text = $"{AppConsts.AppName}(v{AppConsts.AppVersion})";


        _desktopMonitor = new DesktopStatusMonitor();
        _startupShortcut = new StartupShortcutService();
        Disposed += (_, _) => _desktopMonitor.Dispose();
        Shown += async (_, _) =>
        {
            await CheckStartupShortcutAsync();
            if (AutoStartRequested && !_autoStartHandled && !IsDisposed && !_closing)
            {
                _autoStartHandled = true;
                if (!_scheduler.IsRunning && !_busy)
                {
                    _logger.Information("收到 --auto-start，自动触发开始任务");
                    btnStartStop.PerformClick();
                }
            }
        };
        linkLabel2.LinkClicked += linkLabel2_LinkClicked;
        statusStrip1.ShowItemToolTips = true;
        _statusToolTip = new ToolTip(components!) { AutoPopDelay = 20000 };
        label_Status.DoubleClick += (_, _) => OpenDiagnostics();
        var diagnosticsMenu = new ContextMenuStrip(components!);
        diagnosticsMenu.Items.Add("本轮累计统计 / 调度诊断", null, (_, _) => OpenDiagnostics());
        label_Status.ContextMenuStrip = diagnosticsMenu;
        _statusToolTip.SetToolTip(checkBox_RealIp, "代理模式优先使用供应商真实出口IP，缺失时查询；直连模式查询本机出口IP，并传给浏览器环境。");
        _statusToolTip.SetToolTip(checkBox_IpDetails, "通过当前网络通道竞速查询完整IP详情，并设置有效时区和定位；代理查询全部失败则换新IP，直连查询失败则记日志并结束本任务。");
        _statusToolTip.SetToolTip(numericUpDown_MaxConcurrency, "同时处理的任务数量；每个工作者串行消费任务，各任务独立按UV间隔投递，全部UV结束并清理后再取下一项。");
        _statusToolTip.SetToolTip(checkBox_IsHiddenMode, "启动或运行中可修改；后续开始执行的任务使用最新模式，当前任务及其剩余UV保持原模式。");
        _statusToolTip.SetToolTip(checkBox_TaskIntakeBackpressure, "勾选：按启动需求高低水位暂停/恢复领取任务。取消：不因启动需求暂停领取。运行中立即生效，自动保存；浏览器启动并发限制仍生效。");
        _statusToolTip.SetToolTip(numericUpDown_AutoRestartMinutes, "0关闭；正数为本轮运行多少分钟后准备重启。先暂停取任务、暂存队列，等待已领取任务及浏览器收尾完成再重启，自动恢复队列并继续工作。下次启动生效。");
        _statusToolTip.SetToolTip(comboBox_JavaScriptCacheMode, "不缓存：脚本跳过已有缓存并且不写入；URL：完整URL独立保存；URL＋内容：GET且文件名以.js结尾时按正文去重，新URL仍需首次下载。本轮启动后固定。");
        _statusToolTip.SetToolTip(textBox_CacheExcludedUrlPrefixes, "仅针对JS：每行一个完整HTTP/HTTPS URL前缀，例如 https://hm.baidu.com/。匹配的脚本不读写缓存，正常发送网络请求；CSS、图片等仍按原规则缓存。域名不区分大小写，路径及查询区分大小写，不支持通配符。");
        ApplyUiSettings(settings.Value);
        foreach (var control in ConfigurationControls)
        {
            if (control is TextBox text) text.TextChanged += UiSettingsChanged;
            else if (control is NumericUpDown number) number.ValueChanged += UiSettingsChanged;
            else if (control is CheckBox check) check.CheckedChanged += UiSettingsChanged;
            else if (control is ComboBox combo) combo.SelectedIndexChanged += UiSettingsChanged;
        }
        saveSettingsTimer.Tick += (_, _) =>
        {
            saveSettingsTimer.Stop();
            try { SaveUiSettings(); }
            catch (Exception ex) { _operationError = ex.Message; _logger.Error(ex, "保存界面配置失败"); label_Status.Text = $"配置保存失败：{ex.Message}"; }
        };
        refreshTimer.Tick += async (_, _) => { RefreshSchedulingUi(); await CheckScheduledRestartAsync(); };
        refreshTimer.Start();
        FormClosing += MainForm_FormClosing;
        SetBusy(false);
        RefreshStatusBar();
    }

    private Control[] ConfigurationControls =>
    [
        textBox_TaskApiUrl, textBox_ProxyIpUrl, textBox_DevApiUrl, textBox_TaskName,
        numericUpDown_TaskFetchInterval, numericUpDown_UvExecutionInterval,
        numericUpDown_MaxConcurrency, numericUpDown_PlaywrightDriverCount, numericUpDown_AutoRestartMinutes, textBox_PageLoadingDelay, numericUpDown_PageLoadingTimeout,
        numericUpDown_Multiple, numericUpDown_IpTtl, numericUpDown_AcquireRetry,
        checkBox_IsHiddenMode,
        checkBox_IsProxyMode, checkBox_IsCacheMode, comboBox_JavaScriptCacheMode, textBox_CacheExcludedUrlPrefixes,
        checkBox_VerboseLog, checkBox_RealIp, checkBox_IpDetails, checkBox_TaskIntakeBackpressure
    ];

    internal void ApplyUiSettings(TrafficVistaOptions options)
    {
        options.Validate();
        textBox_TaskApiUrl.Text = options.TaskApiUrl;
        textBox_ProxyIpUrl.Text = options.ProxyIpUrl;
        textBox_DevApiUrl.Text = options.DevApiUrl;
        textBox_TaskName.Text = options.TaskName;
        numericUpDown_TaskFetchInterval.Value = options.TaskFetchInterval;
        numericUpDown_UvExecutionInterval.Value = options.UvExecutionInterval;
        numericUpDown_MaxConcurrency.Value = options.MaxConcurrency;
        numericUpDown_PlaywrightDriverCount.Value = options.PlaywrightDriverCount;
        numericUpDown_AutoRestartMinutes.Value = options.AutoRestartMinutes;
        textBox_PageLoadingDelay.Text = options.PageLoadingDelay.ToString(CultureInfo.CurrentCulture);
        numericUpDown_PageLoadingTimeout.Value = options.PageLoadingTimeout;
        numericUpDown_Multiple.Value = options.Multiple;
        numericUpDown_IpTtl.Value = options.IpTtl;
        // IP 获取重试次数来自 Net 配置（Net:AcquireRetryCount，0～10），改后立即对取代理生效。
        numericUpDown_AcquireRetry.Value = Math.Clamp(_settings.Net.AcquireRetryCount,
            (int)numericUpDown_AcquireRetry.Minimum, (int)numericUpDown_AcquireRetry.Maximum);
        checkBox_IsHiddenMode.Checked = options.IsHiddenMode;
        checkBox_TaskIntakeBackpressure.Checked = options.TaskIntakeBackpressureEnabled;
        checkBox_IsProxyMode.Checked = options.IsProxyMode;
        checkBox_RealIp.Checked = options.ResolveRealIp;
        checkBox_IpDetails.Checked = options.FetchIpDetails;
        checkBox_IsCacheMode.Checked = options.IsCacheMode;
        comboBox_JavaScriptCacheMode.SelectedIndex = (int)options.JavaScriptCacheMode - 1;
        textBox_CacheExcludedUrlPrefixes.Text = options.CacheExcludedUrlPrefixes;
        checkBox_VerboseLog.Checked = options.VerboseLog;
        RefreshCacheSettingsAvailability();
        AppLogging.SetVerbose(options.VerboseLog);
    }

    internal TrafficVistaOptions ReadUiSettings()
    {
        var delayText = textBox_PageLoadingDelay.Text.Trim();
        var delay = 0m;
        if (delayText.Length > 0 && !decimal.TryParse(delayText, NumberStyles.Number, CultureInfo.CurrentCulture, out delay))
            throw new InvalidOperationException("首页加载延时请输入秒数，例如 0 或 0.5。");
        return new()
        {
            TaskApiUrl = textBox_TaskApiUrl.Text.Trim(),
            ProxyIpUrl = textBox_ProxyIpUrl.Text.Trim(),
            DevApiUrl = textBox_DevApiUrl.Text.Trim(),
            TaskName = textBox_TaskName.Text.Trim(),
            TaskFetchInterval = (int)numericUpDown_TaskFetchInterval.Value,
            UvExecutionInterval = (int)numericUpDown_UvExecutionInterval.Value,
            MaxConcurrency = (int)numericUpDown_MaxConcurrency.Value,
            PlaywrightDriverCount = (int)numericUpDown_PlaywrightDriverCount.Value,
            AutoRestartMinutes = (int)numericUpDown_AutoRestartMinutes.Value,
            PageLoadingDelay = delay,
            PageLoadingTimeout = (int)numericUpDown_PageLoadingTimeout.Value,
            Multiple = (int)numericUpDown_Multiple.Value,
            IpTtl = (int)numericUpDown_IpTtl.Value,
            IsHiddenMode = checkBox_IsHiddenMode.Checked,
            TaskIntakeBackpressureEnabled = checkBox_TaskIntakeBackpressure.Checked,
            IsProxyMode = checkBox_IsProxyMode.Checked,
            ResolveRealIp = checkBox_RealIp.Checked,
            FetchIpDetails = checkBox_IpDetails.Checked,
            IsCacheMode = checkBox_IsCacheMode.Checked,
            JavaScriptCacheMode = (JavaScriptCacheMode)(comboBox_JavaScriptCacheMode.SelectedIndex + 1),
            CacheExcludedUrlPrefixes = textBox_CacheExcludedUrlPrefixes.Text,
            VerboseLog = checkBox_VerboseLog.Checked
        };
    }

    private void UiSettingsChanged(object? sender, EventArgs e)
    {
        RefreshCacheSettingsAvailability();
        _settingsDirty = true;
        saveSettingsTimer.Stop();
        saveSettingsTimer.Start();
        AppLogging.SetVerbose(checkBox_VerboseLog.Checked);
        if (ReferenceEquals(sender, checkBox_TaskIntakeBackpressure))
            _scheduler.UpdateTaskIntakeBackpressure(checkBox_TaskIntakeBackpressure.Checked);
        if (ReferenceEquals(sender, checkBox_IsHiddenMode))
        {
            _scheduler.UpdateHiddenMode(checkBox_IsHiddenMode.Checked);
            if (_scheduler.IsRunning || _busy)
                _logger.Information("隐藏模式已修改为{Hidden}，后续开始执行的任务生效", checkBox_IsHiddenMode.Checked);
        }
    }

    internal void SaveUiSettings()
    {
        saveSettingsTimer.Stop();
        if (!_settingsDirty) return;
        // “IP获取重试次数”写入用户文件 Net 节并即时刷新运行中的 Net 配置单例。
        _settings.Save(ReadUiSettings(), (int)numericUpDown_AcquireRetry.Value);
        _settingsDirty = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnStartStop.Enabled = !busy && !_closing;
        foreach (var control in ConfigurationControls)
        {
            if (ReferenceEquals(control, numericUpDown_AutoRestartMinutes))
                control.Enabled = !busy && !_closing && !_scheduler.IsRunning;
            else if (control is TextBox text)
            {
                text.ReadOnly = !(!busy && !_closing && !_scheduler.IsRunning);
            }
            else if (control is NumericUpDown number)
            {
                number.ReadOnly = !(!busy && !_closing && !_scheduler.IsRunning);
            }
            else if (ReferenceEquals(control, checkBox_IsHiddenMode) || ReferenceEquals(control, checkBox_TaskIntakeBackpressure))
                control.Enabled = !_closing;
            else
                control.Enabled = !busy && !_closing && !_scheduler.IsRunning;
        }
        btnStartStop.Text = _scheduler.IsRunning ? "停止" : "启动";
        RefreshCacheSettingsAvailability();
        RefreshApplicationState();
    }

    private void RefreshCacheSettingsAvailability()
    {
        var editable = checkBox_IsCacheMode.Checked && !_busy && !_closing && !_scheduler.IsRunning;
        comboBox_JavaScriptCacheMode.Enabled = editable;
        textBox_CacheExcludedUrlPrefixes.Enabled = editable;
    }

    private async void btnStartStop_Click(object sender, EventArgs e)
    {
        if (_busy || _closing) return;
        _operationError = null;
        _transitionText = _scheduler.IsRunning ? "应用：停止中" : "应用：启动中";
        SetBusy(true);
        try
        {
            if (_scheduler.IsRunning) await _scheduler.StopAsync(SchedulerStopMode.Cancel);
            else
            {
                var options = ReadUiSettings();
                options.Validate();
                SaveUiSettings();
                var schedulerOptions = _settings.CreateSchedulerOptions(options, RestartResumeToken);
                RestartResumeToken = null;
                await _scheduler.StartAsync(options, schedulerOptions);
                _restartStarted = Stopwatch.GetTimestamp();
                _restartMinutes = options.AutoRestartMinutes;
                _schedulerObservation = ObserveSchedulerAsync(_scheduler.Completion);
            }
            RefreshSchedulingUi();
        }
        catch (Exception ex) { _operationError = ex.Message; _logger.Error(ex, "启动或停止调度失败"); label_Status.Text = ex.Message; }
        finally { _transitionText = null; SetBusy(false); }
    }

    private async Task ObserveSchedulerAsync(Task<SchedulerSnapshot> completion)
    {
        try
        {
            var snapshot = await completion;
            if (snapshot.State == SchedulerState.Faulted) _logger.Error("调度故障：{Error}", snapshot.LastError);
            else _logger.Information("调度结束，完成={Completed}，失败={Failed}，取消={Canceled}", snapshot.Completed, snapshot.Failed, snapshot.Canceled);
            RefreshSchedulingUi();
        }
        catch (Exception ex) { _logger.Error(ex, "观察调度结束失败"); }
    }

    private async Task CheckScheduledRestartAsync()
    {
        if (_restartInProgress || _busy || _closing || !_scheduler.IsRunning || _restartMinutes == 0
            || Stopwatch.GetElapsedTime(_restartStarted) < TimeSpan.FromMinutes(_restartMinutes)) return;
        _restartInProgress = true;
        _transitionText = "应用：暂存队列，等待在途任务完成后重启";
        SetBusy(true);
        try
        {
            SaveUiSettings();
            Program.RestartResumeToken = await _scheduler.PrepareRestartAsync();
            if (_schedulerObservation is not null) await _schedulerObservation;
            Program.RestartRequested = true;
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "定时重启未完成，保留应用及暂存任务供检查");
            _operationError = "定时重启失败：" + ex.Message;
            _restartMinutes = 0;
        }
        finally
        {
            if (!_allowClose) { _transitionText = null; _restartInProgress = false; SetBusy(false); }
        }
    }

    private void RefreshSchedulingUi()
    {
        RefreshStatusBar();
        if (_schedulerObservation is null) return;
        var s = _scheduler.Snapshot;
        var p = _scheduler.Performance;
        var dispatch = _scheduler.Dispatch;
        var currentError = s.CacheError ?? s.LastError;
        label_Status.Text = BuildTaskStatusText(s, p, _scheduler.ActiveUvSessions, dispatch, currentError);
        if (_restartInProgress) label_Status.Text = "定时重启：已暂停取任务，等待在途任务完成及队列落盘\n" + label_Status.Text;
        else if (_scheduler.IsRunning && _restartMinutes > 0)
            label_Status.Text = $"定时重启：约{Math.Max(0, _restartMinutes - Stopwatch.GetElapsedTime(_restartStarted).TotalMinutes):F1}分钟后开始收尾\n" + label_Status.Text;
        var dispatchDetails = dispatch is null ? "" : $"按任务UV间隔投递；待投递UV：{dispatch.PendingUvs}，执行中任务：{dispatch.ActiveTasks}";
        _statusToolTip.SetToolTip(label_Status, "双击或右键查看累计统计、当前阶段及等待时长。\n" + dispatchDetails + (currentError is null ? "" : "\n" + currentError));
        SetBusy(_busy);
    }

    internal static string BuildTaskStatusText(SchedulerSnapshot s, BusinessMetricsSnapshot p, int active,
        TaskDispatchSnapshot? dispatch, string? error)
    {
        static string Short(string value, int length) => value.Length <= length ? value : value[..length] + "…";
        var text = $"运行时间：{p.RunElapsedText}"
            + $"\n提交数量（UV开始运行）：{p.DispatchedUvs}"
            + $"\n执行数量（浏览器开始启动）：{p.BrowserStartAttempts}"
            + $"\n曝光数量（首次页面打开成功）：{p.ExposedUvs}"
            + $"\nUV投递 / 分（最近60秒）：{p.DispatchesPerMinute:F0}"
            + $"\nUV完成 / 分（本轮平均）：{p.AttemptsPerMinute:F1}"
            + $"\nUV成功 / 分（本轮平均）：{p.UvsPerMinute:F1}"
            + $"\nUV耗时 P95：{p.UvP95Ms / 1000:F1}秒"
            + $"\nUV累计投递：{p.DispatchedUvs}"
            + $"\nUV累计完成：{p.FinishedUvs}"
            + $"\nUV运行中：{active}"
            + $"\nUV成功：{p.SucceededUvs}"
            + $"\nUV失败：{p.FailedUvs}"
            + $"\nUV取消：{p.CanceledUvs}"
            + $"\n任务累计提交：{s.Submitted}"
            + $"\n任务排队：{s.Queued}"
            + $"\n任务等待执行：{s.Preparing}"
            + $"\n任务执行中：{s.Running}"
            + $"\n任务成功：{s.Succeeded}"
            + $"\n任务失败：{s.Failed}"
            + $"\n任务取消：{s.Canceled}"
            + $"\n任务待恢复：{s.Cached}";
        if (dispatch is not null) text += $"\n待投递UV：{dispatch.PendingUvs}"
            + $"\n执行中任务：{dispatch.ActiveTasks}";
        var slow = p.ActiveStages.OrderByDescending(item => item.Value.OldestSeconds).FirstOrDefault();
        if (slow.Key is not null) text += $"\n最慢阶段：{Short(slow.Key, 12)}"
            + $"\n该阶段数量：{slow.Value.Count}"
            + $"\n该阶段最久耗时：{slow.Value.OldestSeconds:F0}秒";
        if (error is not null) text += "\n错误：" + Short(error.Split('\n')[0].Trim(), 22);
        return text;
    }

    private void OpenDiagnostics()
    {
        if (_diagnosticsForm is { IsDisposed: false }) { _diagnosticsForm.Activate(); return; }
        _diagnosticsForm = new SchedulingDiagnosticsForm(_scheduler);
        _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
        _diagnosticsForm.Show(this);
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || _busy) return;
        _closing = true;
        refreshTimer.Stop();
        SetBusy(true);
        try
        {
            await _scheduler.StopAsync(SchedulerStopMode.Cancel);
            if (_schedulerObservation is not null) await _schedulerObservation;
            SaveUiSettings();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "关闭窗口失败");
            label_Status.Text = ex.Message;
            _closing = false;
            refreshTimer.Start();
        }
        finally { if (!_allowClose) SetBusy(false); }
    }

    internal static string ApplicationState(SchedulerState state, bool busy, bool closing, bool running,
        bool waitingForTasks, bool hasError, bool hasStarted = true)
    {
        if (closing || state == SchedulerState.Stopping) return "应用：停止中";
        if (hasError || state == SchedulerState.Faulted) return "应用：故障";
        if (busy) return running ? "应用：停止中" : "应用：启动中";
        return state switch
        {
            SchedulerState.Running => waitingForTasks ? "应用：运行中（等待任务）" : "应用：运行中",
            SchedulerState.Stopped => hasStarted ? "应用：已停止" : "应用：待启动",
            SchedulerState.Disposed => "应用：已停止",
            _ => "应用：待启动"
        };
    }

    private void RefreshApplicationState()
    {
        if (IsDisposed) return;
        var snapshot = _scheduler.Snapshot;
        var waiting = snapshot.Queued == 0 && snapshot.Running == 0 && snapshot.Preparing == 0;
        toolStripStatusLabel1.Text = _busy && !_closing && _transitionText is not null ? _transitionText
            : ApplicationState(snapshot.State, _busy, _closing, _scheduler.IsRunning, waiting, _operationError is not null, _schedulerObservation is not null);
        toolStripStatusLabel1.ToolTipText = _operationError ?? $"任务Worker：{snapshot.Workers}；按任务UV间隔投递";
    }

    internal void RefreshStatusBar()
    {
        RefreshApplicationState();
        var desktop = _desktopMonitor.Snapshot;
        var cores = desktop.LogicalCores > 0 ? desktop.LogicalCores.ToString() : "--";
        var sampling = desktop.LogicalCores == 0 || desktop.Resources.UnavailableReason?.Contains("正在采样") == true;
        toolStripStatusLabel2.Text = $"CPU：{cores}逻辑核 / " + (desktop.Resources.Available ? $"{desktop.Resources.CpuPercent:F1}%" : sampling ? "采样中" : "不可用");
        toolStripStatusLabel2.ToolTipText = desktop.Resources.UnavailableReason ?? "系统CPU趋势使用率，和任务准入共用每秒资源采样。";
        if (desktop.Resources.TotalPhysicalBytes > 0)
        {
            var used = desktop.Resources.TotalPhysicalBytes - Math.Min(desktop.Resources.TotalPhysicalBytes, desktop.Resources.AvailablePhysicalBytes);
            var percent = 100.0 * used / desktop.Resources.TotalPhysicalBytes;
            toolStripStatusLabel3.Text = $"内存：{used / 1073741824.0:F1}/{desktop.Resources.TotalPhysicalBytes / 1073741824.0:F1}GB / {percent:F1}%";
            toolStripStatusLabel3.ToolTipText = $"系统物理内存；可用：{desktop.Resources.AvailablePhysicalBytes / 1048576.0:F0} MiB；可用提交内存：{desktop.Resources.AvailableCommitBytes / 1048576.0:F0} MiB。";
        }
        else { toolStripStatusLabel3.Text = sampling ? "内存：采样中" : "内存：不可用"; toolStripStatusLabel3.ToolTipText = desktop.Resources.UnavailableReason ?? "等待系统内存采样。"; }
        toolStripStatusLabel4.Text = desktop.BrowserProcesses is { } count ? $"浏览器进程：{count}" : desktop.ProcessError is null ? "浏览器进程：采样中" : "浏览器进程：不可用";
        toolStripStatusLabel4.ToolTipText = desktop.ProcessError ?? $"只统计本应用所属Chrome Job（含主进程、渲染器、GPU等子进程）；UV会话：{_scheduler.ActiveUvSessions}。";
    }

    private async Task CheckStartupShortcutAsync()
    {
        var revision = _startupRevision;
        try
        {
            var enabled = await _startupShortcut.IsEnabledAsync();
            if (!IsDisposed && revision == _startupRevision) DisplayStartupState(enabled);
        }
        catch (Exception ex)
        { _logger.Warning(ex, "检查开机启动快捷方式失败"); if (!IsDisposed) _statusToolTip.SetToolTip(linkLabel1, "检查失败，可点击重建：" + ex.Message); }
    }

    private void DisplayStartupState(bool enabled)
    {
        linkLabel1.Text = enabled ? "开机启动 ✓" : "开机启动";
        linkLabel1.LinkColor = enabled ? Color.ForestGreen : Color.Blue;
        _statusToolTip.SetToolTip(linkLabel1, (enabled ? "已启用" : "未启用，点击创建") + "：" + _startupShortcut.ShortcutPath + "\n带 --auto-start，登录后自动开始任务。");
    }

    private async void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        if (_startupBusy) return;
        _startupBusy = true; _startupRevision++; linkLabel1.Enabled = false;
        try
        {
            var result = await _startupShortcut.EnsureEnabledAsync();
            _logger.Information("开机启动已启用，快捷方式={Path}，旧启动项清理数={Count}；自动开始任务", _startupShortcut.ShortcutPath, result.RemovedEntries);
            foreach (var warning in result.Warnings) _logger.Warning("旧启动项清理未完成：{Warning}", warning);
            if (!IsDisposed) DisplayStartupState(true);
            if (result.Warnings.Count > 0 && !IsDisposed)
            {
                label_Status.Text = "开机启动已创建，部分旧启动项清理失败，请查看日志。";
                _statusToolTip.SetToolTip(linkLabel1, string.Join("\n", result.Warnings));
            }
            try
            {
                await Task.Run(() => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_startupShortcut.ShortcutPath)!)
                { UseShellExecute = true, Verb = "open" })?.Dispose());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "开机启动已创建，但打开启动目录失败");
                if (!IsDisposed) label_Status.Text = "开机启动已创建，但打开目录失败：" + ex.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建开机启动快捷方式失败");
            if (!IsDisposed) { linkLabel1.Text = "开机启动"; _statusToolTip.SetToolTip(linkLabel1, "创建失败：" + ex.Message); label_Status.Text = "开机启动设置失败：" + ex.Message; }
        }
        finally { _startupBusy = false; if (!IsDisposed) linkLabel1.Enabled = true; }
    }

    private async void linkLabel2_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try { await Task.Run(() => Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true, Verb = "open" })?.Dispose()); }
        catch (Exception ex) { _logger.Error(ex, "打开应用目录失败"); if (!IsDisposed) label_Status.Text = "打开应用目录失败：" + ex.Message; }
    }

    private void panel1_Paint(object sender, PaintEventArgs e)
    {

    }

    private void MainForm_Load(object sender, EventArgs e)
    {

    }
}
