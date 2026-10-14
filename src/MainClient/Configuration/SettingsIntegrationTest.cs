using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrafficVista.Configuration;

internal static class SettingsIntegrationTest
{
    public static string Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "settings-tests", DateTime.UtcNow.Ticks.ToString());
        Directory.CreateDirectory(directory);
        var defaultsPath = Path.Combine(directory, "appsettings.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), defaultsPath);
        var originalDefaults = File.ReadAllText(defaultsPath);
        var settings = new TrafficVistaSettings(directory);
        if (File.Exists(settings.UserFilePath)) throw new InvalidOperationException("加载默认值生成了用户文件。");
        var defaults = settings.Value;
        var legacyDefaults = JObject.Parse(originalDefaults);
        legacyDefaults["Business"]!["MaxConcurrentTasks"] = 50;
        ((JObject)legacyDefaults["TrafficVista"]!).Remove(nameof(TrafficVistaOptions.TaskWorkerCount));
        File.WriteAllText(defaultsPath, legacyDefaults.ToString());
        if (new TrafficVistaSettings(directory).Value.TaskWorkerCount != 50)
            throw new InvalidOperationException("旧Business Worker配置未迁移到新界面默认值。");
        legacyDefaults["TrafficVista"]![nameof(TrafficVistaOptions.TaskWorkerCount)] = 96;
        File.WriteAllText(defaultsPath, legacyDefaults.ToString());
        if (new TrafficVistaSettings(directory).Value.TaskWorkerCount != 96)
            throw new InvalidOperationException("新Worker字段没有优先于旧Business配置。");
        File.WriteAllText(defaultsPath, originalDefaults);
        File.WriteAllText(settings.UserFilePath, """
            {"trafficvista":{"maxconcurrency":5,"FutureOption":"keep","FirstUserId":10,"TaskCount":8,"WorkerCount":4},
             "Scheduling":{"QueueCapacity":1},"Other":"keep"}
            """);
        settings = new TrafficVistaSettings(directory);
        if (settings.Value.MaxConcurrency != 5 || settings.Value.DevApiUrl != defaults.DevApiUrl)
            throw new InvalidOperationException("配置覆盖失败。");
        var expectedCapacity = JObject.Parse(originalDefaults)["Scheduling"]!.Value<int>("QueueCapacity");
        if (settings.SchedulingDefaults.QueueCapacity != expectedCapacity) throw new InvalidOperationException("固定项被覆盖。");
        var updated = defaults with
        {
            TaskApiUrl = "http://task.test/?action=getTask", TaskName = "测试任务", ProxyIpUrl = "http://proxy.test/",
            DevApiUrl = "http://device.test/", TaskFetchInterval = 350, UvExecutionInterval = 750,
            MaxConcurrency = 8, TaskWorkerCount = 50, PageLoadingDelay = 0.5m, PageLoadingTimeout = 30,
            Multiple = 2, IpTtl = 60, IsHiddenMode = true, IsProxyMode = true, IsCacheMode = true, VerboseLog = true,
            ResolveRealIp = false, FetchIpDetails = true
        };
        settings.Save(updated);
        if (settings.Value != updated || new TrafficVistaSettings(directory).Value != updated)
            throw new InvalidOperationException("所有界面字段保存/恢复失败。");
        var saved = JObject.Parse(File.ReadAllText(settings.UserFilePath));
        var section = (JObject)saved["trafficvista"]!;
        if (section.ContainsKey("FirstUserId") || section.ContainsKey("TaskCount") || section.ContainsKey("WorkerCount")
            || section.Value<string>("FutureOption") != "keep" || saved.Value<string>("Other") != "keep"
            || File.ReadAllText(defaultsPath) != originalDefaults) throw new InvalidOperationException("字段清理或文件保护失败。");
        var scheduler = settings.CreateSchedulerOptions(updated);
        if (scheduler.WorkerCount != 50 || scheduler.LocalCache is not { LoadOnStart: true }
            || scheduler.QueueCapacity != 32 || scheduler.PollInterval.TotalMilliseconds != 350
            || scheduler.EmptyPollInterval.TotalMilliseconds != 350 || scheduler.CompleteWhenSourceEmpty)
            throw new InvalidOperationException("持续轮询配置映射失败。");
        var largestQueue = settings.CreateSchedulerOptions(updated with { MaxConcurrency = 1024, Multiple = 1000 });
        largestQueue.Validate();
        if (largestQueue.QueueCapacity != 2_048_000
            || settings.CreateSchedulerOptions(updated with { MaxConcurrency = 1, Multiple = 1 }).QueueCapacity != 2)
            throw new InvalidOperationException("并发数 × 倍率 × 2 的队列容量边界映射失败。");
        foreach (var workers in new[] { 1, 96, 1024 })
        {
            var mapped = settings.CreateSchedulerOptions(updated with { TaskWorkerCount = workers });
            mapped.Validate();
            if (mapped.WorkerCount != workers || mapped.QueueCapacity != 32)
                throw new InvalidOperationException("Worker边界或与UV队列容量隔离失败。");
        }
        foreach (var invalidWorkers in new[] { 0, 1025 })
        {
            try { settings.CreateSchedulerOptions(updated with { TaskWorkerCount = invalidWorkers });
                throw new InvalidOperationException("非法Worker数未被拒绝。"); }
            catch (ArgumentOutOfRangeException) { }
        }
        var rejected = false;
        try { settings.Save(updated with { MaxConcurrency = 0 }); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        if (!rejected || new TrafficVistaSettings(directory).Value != updated) throw new InvalidOperationException("非法值未被保护。");
        File.WriteAllText(settings.UserFilePath, "{invalid");
        rejected = false;
        try { settings.Save(updated); }
        catch (JsonException) { rejected = true; }
        if (!rejected || File.ReadAllText(settings.UserFilePath) != "{invalid") throw new InvalidOperationException("损坏文件未被保护。");
        return "PASS\n所有界面字段保存/恢复、用户覆盖、固定项隔离、旧演示字段清理、持续轮询参数映射、非法值和损坏文件保护通过。";
    }
}
