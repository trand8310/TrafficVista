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
        File.WriteAllText(settings.UserFilePath, "{\"TrafficVista\":{\"StrictJavaScriptUrlCache\":true}}");
        var migratedCache = new TrafficVistaSettings(directory);
        if (migratedCache.Value.JavaScriptCacheMode != JavaScriptCacheMode.Url)
            throw new InvalidOperationException("旧JS严格模式没有迁移为URL缓存模式。");
        migratedCache.Save(migratedCache.Value);
        if (JObject.Parse(File.ReadAllText(settings.UserFilePath))["TrafficVista"]!["StrictJavaScriptUrlCache"] is not null)
            throw new InvalidOperationException("保存后仍保留旧JS严格模式字段。");
        File.WriteAllText(settings.UserFilePath, "{\"TrafficVista\":{\"JavaScriptCacheMode\":1,\"StrictJavaScriptUrlCache\":true}}");
        if (new TrafficVistaSettings(directory).Value.JavaScriptCacheMode != JavaScriptCacheMode.Disabled)
            throw new InvalidOperationException("新JS缓存模式未优先于旧字段。");
        File.Delete(settings.UserFilePath);
        var legacyDefaults = JObject.Parse(originalDefaults);
        legacyDefaults["Business"]!["MaxConcurrentTasks"] = 50;
        ((JObject)legacyDefaults["TrafficVista"]!).Remove(nameof(TrafficVistaOptions.MaxConcurrency));
        File.WriteAllText(defaultsPath, legacyDefaults.ToString());
        if (new TrafficVistaSettings(directory).Value.MaxConcurrency != 50)
            throw new InvalidOperationException("旧Business Worker配置未迁移到新界面默认值。");
        legacyDefaults["TrafficVista"]!["TaskWorkerCount"] = 96;
        File.WriteAllText(defaultsPath, legacyDefaults.ToString());
        if (new TrafficVistaSettings(directory).Value.MaxConcurrency != 96)
            throw new InvalidOperationException("新Worker字段没有优先于旧Business配置。");
        File.WriteAllText(defaultsPath, originalDefaults);
        File.WriteAllText(settings.UserFilePath, """
            {"trafficvista":{"maxconcurrency":5,"FutureOption":"keep","FirstUserId":10,"TaskCount":8,"WorkerCount":4},
             "Scheduling":{"QueueCapacity":1},"Other":"keep"}
            """);
        settings = new TrafficVistaSettings(directory);
        if (settings.Value.MaxConcurrency != 5 || settings.Value.DevApiUrl != defaults.DevApiUrl)
            throw new InvalidOperationException("配置覆盖失败。");
        var legacyUser = JObject.Parse(File.ReadAllText(settings.UserFilePath));
        legacyUser["trafficvista"]!["taskworkercount"] = 96;
        File.WriteAllText(settings.UserFilePath, legacyUser.ToString());
        settings = new TrafficVistaSettings(directory);
        if (settings.Value.MaxConcurrency != 96 || settings.CreateSchedulerOptions(settings.Value).WorkerCount != 96)
            throw new InvalidOperationException("旧双字段用户配置未保留任务工作者数量。");
        var expectedCapacity = JObject.Parse(originalDefaults)["Scheduling"]!.Value<int>("QueueCapacity");
        if (settings.SchedulingDefaults.QueueCapacity != expectedCapacity) throw new InvalidOperationException("固定项被覆盖。");
        var updated = defaults with
        {
            TaskApiUrl = "http://task.test/?action=getTask", TaskName = "测试任务", ProxyIpUrl = "http://proxy.test/",
            DevApiUrl = "http://device.test/", TaskFetchInterval = 350, UvExecutionInterval = 750,
            MaxConcurrency = 8, PlaywrightDriverCount = 4, PageLoadingDelay = 0.5m, PageLoadingTimeout = 30,
            Multiple = 2, IpTtl = 60, IsHiddenMode = true, IsProxyMode = true, IsCacheMode = true, VerboseLog = true,
            ResolveRealIp = false, FetchIpDetails = true, TaskIntakeBackpressureEnabled = false, AutoRestartMinutes = 60
        };
        settings.Save(updated);
        if (settings.Value != updated || new TrafficVistaSettings(directory).Value != updated)
            throw new InvalidOperationException("所有界面字段保存/恢复失败。");
        var saved = JObject.Parse(File.ReadAllText(settings.UserFilePath));
        var section = (JObject)saved["trafficvista"]!;
        if (section.ContainsKey("FirstUserId") || section.ContainsKey("TaskCount") || section.ContainsKey("WorkerCount") || section.ContainsKey("TaskWorkerCount")
            || section.Value<string>("FutureOption") != "keep" || saved.Value<string>("Other") != "keep"
            || File.ReadAllText(defaultsPath) != originalDefaults) throw new InvalidOperationException("字段清理或文件保护失败。");
        var scheduler = settings.CreateSchedulerOptions(updated);
        if (scheduler.WorkerCount != 8 || scheduler.LocalCache is not { LoadOnStart: false, RestartOnly: true }
            || scheduler.QueueCapacity != 32 || scheduler.PollInterval.TotalMilliseconds != 350
            || scheduler.EmptyPollInterval.TotalMilliseconds != 350 || scheduler.CompleteWhenSourceEmpty)
            throw new InvalidOperationException("持续轮询配置映射失败。");
        if (settings.Business.ExportRunData || settings.Business.TaskRecoveryMode != Business.TaskRecoveryMode.Discard)
            throw new InvalidOperationException("默认应关闭运行导出和任务恢复。");
        var saveDefaults = JObject.Parse(originalDefaults);
        saveDefaults["Business"]!["TaskRecoveryMode"] = "Save";
        File.WriteAllText(defaultsPath, saveDefaults.ToString());
        var saving = new TrafficVistaSettings(directory).CreateSchedulerOptions(updated with { AutoRestartMinutes = 0 });
        if (saving.LocalCache is not { LoadOnStart: true })
            throw new InvalidOperationException("Save模式没有启用待执行任务持久化和启动恢复。");
        saveDefaults["Business"]!["TaskRecoveryMode"] = "Discard";
        File.WriteAllText(defaultsPath, saveDefaults.ToString());
        if (new TrafficVistaSettings(directory).CreateSchedulerOptions(updated with { AutoRestartMinutes = 0 }).LocalCache is not null)
            throw new InvalidOperationException("Discard模式应忽略待恢复收件箱。");
        File.WriteAllText(defaultsPath, originalDefaults);
        var largestQueue = settings.CreateSchedulerOptions(updated with { MaxConcurrency = 1024, Multiple = 1000 });
        largestQueue.Validate();
        if (largestQueue.QueueCapacity != 2_048_000
            || settings.CreateSchedulerOptions(updated with { MaxConcurrency = 1, Multiple = 1 }).QueueCapacity != 2)
            throw new InvalidOperationException("并发数 × 倍率 × 2 的队列容量边界映射失败。");
        foreach (var workers in new[] { 1, 96, 1024 })
        {
            var mapped = settings.CreateSchedulerOptions(updated with { MaxConcurrency = workers });
            mapped.Validate();
            if (mapped.WorkerCount != workers || mapped.QueueCapacity != workers * updated.Multiple * 2)
                throw new InvalidOperationException("任务并发数或任务队列容量映射失败。");
        }
        foreach (var invalidWorkers in new[] { 0, 1025 })
        {
            try { settings.CreateSchedulerOptions(updated with { MaxConcurrency = invalidWorkers });
                throw new InvalidOperationException("非法Worker数未被拒绝。"); }
            catch (ArgumentOutOfRangeException) { }
        }
        foreach (var driverCount in new[] { 0, 1, 8, 1024 })
        {
            settings.Save(updated with { PlaywrightDriverCount = driverCount });
            if (new TrafficVistaSettings(directory).Value.PlaywrightDriverCount != driverCount)
                throw new InvalidOperationException("Playwright驱动数保存/恢复失败。");
        }
        settings.Save(updated);
        foreach (var driverCount in new[] { -1, 1025 })
        {
            try { settings.Save(updated with { PlaywrightDriverCount = driverCount });
                throw new InvalidOperationException("非法Playwright驱动数未被拒绝。"); }
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
        return "PASS\n所有界面字段保存/恢复、用户覆盖、固定项隔离、旧任务工作者字段迁移与清理、持续轮询参数映射、非法值和损坏文件保护通过。";
    }
}
