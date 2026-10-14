using Microsoft.Extensions.Options;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrafficVista.Scheduling;
using TrafficVista.Business;
using TrafficVista.LocalTesting;
using TrafficVista.Net.Options;
using TrafficVista.Serialization;

namespace TrafficVista.Configuration;

/// <summary>读取分层配置；只向用户文件写入界面项。</summary>
public sealed class TrafficVistaSettings : IOptions<TrafficVistaOptions>
{
    private readonly object _sync = new();
    private TrafficVistaOptions _current;
    public string UserFilePath { get; }
    public SchedulerOptions SchedulingDefaults { get; }
    public int MaxConcurrentBrowserStarts { get; }
    public LocalTestingOptions LocalTesting { get; }
    public BusinessOptions Business { get; }
    /// <summary>Net 模块配置；供应商默认值取基础文件，用户文件可覆盖所选供应商与 URL 等。</summary>
    public NetOptions Net { get; }
    public TrafficVistaOptions Value { get { lock (_sync) return _current with { }; } }

    public TrafficVistaSettings(string directory)
    {
        directory = Path.GetFullPath(directory);
        UserFilePath = Path.Combine(directory, "appsettings.user.json");
        var defaults = JsonData.ParseObject(File.ReadAllText(Path.Combine(directory, "appsettings.json")));
        var privatePath = Path.Combine(directory, "appsettings.private.json");
        if (File.Exists(privatePath))
        {
            var privateSettings = JsonData.ParseObject(File.ReadAllText(privatePath));
            foreach (var section in new[] { "TrafficVista", "Net" })
            {
                if (GetSection(privateSettings, section) is { } privateSection)
                {
                    var destination = GetSection(defaults, section) ?? new JObject();
                    destination.Merge(privateSection, new JsonMergeSettings
                    { PropertyNameComparison = StringComparison.OrdinalIgnoreCase, MergeArrayHandling = MergeArrayHandling.Replace });
                    defaults[section] = destination;
                }
            }
        }
        // 固定项只取基础文件，用户文件只覆盖 TrafficVista 界面项。
        SchedulingDefaults = GetSection(defaults, "Scheduling")?.ToObject<SchedulerOptions>() ?? new();
        SchedulingDefaults.Validate();
        LocalTesting = GetSection(defaults, "LocalTesting")?.ToObject<LocalTestingOptions>() ?? new();
        LocalTesting.Validate();
        Business = GetSection(defaults, "Business")?.ToObject<BusinessOptions>() ?? new();
        Business.Validate();
        MaxConcurrentBrowserStarts = GetSection(defaults, "Browser")?
            .GetValue("MaxConcurrentStarts", StringComparison.OrdinalIgnoreCase)?.ToObject<int>()
            ?? ChromeStartup.MaxConcurrentStarts;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentBrowserStarts);
        var merged = (JObject?)GetSection(defaults, TrafficVistaOptions.SectionName)?.DeepClone() ?? new JObject();
        var netMerged = (JObject?)GetSection(defaults, NetOptions.SectionName)?.DeepClone() ?? new JObject();
        if (File.Exists(UserFilePath))
        {
            var user = JsonData.ParseObject(File.ReadAllText(UserFilePath));
            if (GetSection(user, TrafficVistaOptions.SectionName) is { } userOptions)
                merged.Merge(userOptions, new JsonMergeSettings
                {
                    PropertyNameComparison = StringComparison.OrdinalIgnoreCase,
                    MergeArrayHandling = MergeArrayHandling.Replace
                });
            // 用户文件可覆盖 Net：所选供应商（DefaultVendor）、各家 URL 等；固定限制仍以基础文件为准合并。
            if (GetSection(user, NetOptions.SectionName) is { } userNet)
                netMerged.Merge(userNet, new JsonMergeSettings
                {
                    PropertyNameComparison = StringComparison.OrdinalIgnoreCase,
                    MergeArrayHandling = MergeArrayHandling.Replace
                });
        }
        Net = netMerged.ToObject<NetOptions>() ?? new();
        Net.Validate();
        // 旧版本只有Business:MaxConcurrentTasks；首次打开新界面沿用旧值，保存后由界面字段管理。
        if (merged.GetValue(nameof(TrafficVistaOptions.TaskWorkerCount), StringComparison.OrdinalIgnoreCase) is null)
            merged[nameof(TrafficVistaOptions.TaskWorkerCount)] = Business.MaxConcurrentTasks;
        _current = merged.ToObject<TrafficVistaOptions>() ?? new();
        _current.Validate();
    }

    private static JObject? GetSection(JObject root, string name)
    {
        var value = root.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (value is null || value.Type == JTokenType.Null) return null;
        return value as JObject ?? throw new InvalidDataException($"配置的 {name} 节必须是 JSON 对象。");
    }

    public SchedulerOptions CreateSchedulerOptions(TrafficVistaOptions uiOptions)
    {
        uiOptions.Validate();
        return SchedulingDefaults with
        {
            WorkerCount = uiOptions.TaskWorkerCount,
            QueueCapacity = checked(uiOptions.MaxConcurrency * uiOptions.Multiple * 2),
            PollInterval = TimeSpan.FromMilliseconds(uiOptions.TaskFetchInterval),
            EmptyPollInterval = TimeSpan.FromMilliseconds(uiOptions.TaskFetchInterval),
            CompleteWhenSourceEmpty = false,
            LocalCache = Business.PersistPendingTasks ? new Scheduling.Persistence.LocalTaskCacheOptions
            {
                FilePath = Path.Combine(Path.GetDirectoryName(UserFilePath)!, "business", "pending",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(uiOptions.TaskApiUrl + "\n" + uiOptions.TaskName))) + ".json"),
                LoadOnStart = true, CompactClaimsWhenEmpty = true
            } : SchedulingDefaults.LocalCache
        };
    }

    /// <summary>
    /// 保存界面项到用户文件；acquireRetryCount 非空时同时写入 Net:AcquireRetryCount，
    /// 并即时更新内存中的 <see cref="Net"/> 单例（服务按次读取，下一轮取代理即用新值）。
    /// </summary>
    public void Save(TrafficVistaOptions options, int? acquireRetryCount = null)
    {
        options.Validate();
        if (acquireRetryCount is < 0 or > 10)
            throw new InvalidDataException($"Net:AcquireRetryCount 必须在 0～10 之间（当前 {acquireRetryCount}）。");
        lock (_sync)
        {
            // 保留其他用户配置字段；损坏文件报错，不覆盖原文件。
            var root = File.Exists(UserFilePath)
                ? JsonData.ParseObject(File.ReadAllText(UserFilePath))
                : new JObject();
            var sectionKey = root.Property(TrafficVistaOptions.SectionName, StringComparison.OrdinalIgnoreCase)?.Name
                ?? TrafficVistaOptions.SectionName;
            var section = GetSection(root, sectionKey) ?? new JObject();
            // 清理旧演示界面字段；其余未知配置继续保留。
            foreach (var property in section.Properties().Where(property =>
                new[] { "FirstUserId", "TaskCount", "WorkerCount" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
                property.Remove();
            foreach (var property in JObject.FromObject(options).Properties())
            {
                foreach (var previous in section.Properties().Where(previous =>
                    string.Equals(previous.Name, property.Name, StringComparison.OrdinalIgnoreCase)).ToArray())
                    previous.Remove();
                section[property.Name] = property.Value.DeepClone();
            }
            if (root[sectionKey] is not JObject) root[sectionKey] = section;
            if (acquireRetryCount is { } retry)
            {
                var netKey = root.Property(NetOptions.SectionName, StringComparison.OrdinalIgnoreCase)?.Name
                    ?? NetOptions.SectionName;
                var netSection = GetSection(root, netKey) ?? new JObject();
                foreach (var previous in netSection.Properties().Where(previous =>
                    string.Equals(previous.Name, nameof(NetOptions.AcquireRetryCount), StringComparison.OrdinalIgnoreCase)).ToArray())
                    previous.Remove();
                netSection[nameof(NetOptions.AcquireRetryCount)] = retry;
                if (root[netKey] is not JObject) root[netKey] = netSection;
            }
            var temporaryPath = UserFilePath + ".tmp";
            File.WriteAllText(temporaryPath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.Move(temporaryPath, UserFilePath, overwrite: true);
            _current = options with { };
            if (acquireRetryCount is { } live) Net.AcquireRetryCount = live;
        }
    }
}
