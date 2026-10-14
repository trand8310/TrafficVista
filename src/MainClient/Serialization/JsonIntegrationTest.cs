using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrafficVista.Contracts;
using TrafficVista.Scheduling.Consumers;
using TrafficVista.Scheduling.Persistence;
using TrafficVista.Scheduling.Producers;

namespace TrafficVista.Serialization;

/// <summary>只验证 JSON 契约和旧缓存兼容，不启动浏览器，也不请求接口。</summary>
internal static class JsonIntegrationTest
{
    public static async Task<string> RunAsync()
    {
        const string date = "2026-10-13T08:00:00Z";
        const string response = """
            {"success":true,"data":[{"id":43135,"title":"本地 JSON 测试","url":"http://127.0.0.1/test.html",
             "uv":12,"pv":1,"sleep":"5-5","jstext":"{\"mode\":\"test\"}",
             "extension":{"date":"2026-10-13T08:00:00Z","values":[1,true,null]}}]}
            """;
        var task = HttpTaskSource.ParseResponse(Encoding.UTF8.GetBytes(response)).Single();
        VerifyOptionalJstext(task);
        var payload = task.Payload!;
        if (payload["extension"]!["date"]!.Type != JTokenType.String
            || payload["extension"]!.Value<string>("date") != date
            || !JsonData.TryGetInt32(payload["uv"], out var uv) || uv != 12
            || payload.Value<string>("jstext") != "{\"mode\":\"test\"}")
            throw new InvalidOperationException("任务原始 JSON 字段在解析后发生变化。");
        if (JsonData.TryGetInt32(new JValue("12"), out _)
            || JsonData.TryGetInt32(new JValue(12.5), out _)
            || JsonData.TryGetInt32(new JValue(long.MaxValue), out _))
            throw new InvalidOperationException("非整数或溢出值被错误转换为任务整数。");
        var rejected = false;
        try { JsonData.ParseObject("{} {}"); }
        catch (JsonException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("JSON 正文之后的多余内容未被拒绝。");

        var directory = Path.Combine(AppContext.BaseDirectory, "demo-output", "json-tests",
            DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pending.json");
        // 旧版缓存格式 v2 和消费日志 v1：手写历史 JSON，避免只验证新序列化器写出的文件。
        await File.WriteAllTextAsync(path, """
            {"Version":2,"TaskType":"TrafficVista.Contracts.TaskEnvelope","Tasks":[
             {"Key":"43135","Payload":{"Id":43135,"Name":"\u672c\u5730测试","Url":"http://127.0.0.1/test.html",
              "CreatedAt":"2026-10-13T08:00:00Z","Payload":{"uv":12,"pv":1,"sleep":"5-5",
               "extension":{"date":"2026-10-13T08:00:00Z"}}}}]}
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(path + ".consumed.jsonl",
            "{\"Version\":1,\"TaskType\":\"TrafficVista.Contracts.TaskEnvelope\",\"Keys\":[]}\n").ConfigureAwait(false);
        var options = new LocalTaskCacheOptions { FilePath = path, CheckpointInterval = TimeSpan.FromMinutes(1) };
        static string Key(TaskEnvelope value) => value.Id.ToString(CultureInfo.InvariantCulture);
        await using (var cache = await LocalTaskCache<TaskEnvelope>.OpenAsync(options, Key).ConfigureAwait(false))
        {
            var old = cache.ReplayItems().Single();
            if (old.Name != "本地测试" || old.CreatedAt.Kind != DateTimeKind.Utc
                || old.CreatedAt != new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc)
                || old.Payload!.Value<int>("uv") != 12
                || old.Payload["extension"]!["date"]!.Type != JTokenType.String)
                throw new InvalidOperationException("旧缓存中的任务、UTC 日期或原始 Payload 未正确恢复。");

            var incoming = task with { Id = 43136 };
            if ((await cache.StageBatchAsync([incoming]).ConfigureAwait(false)).Count != 1)
                throw new InvalidOperationException("新任务没有加入缓存。");
            incoming.Payload!["uv"] = 999;
            var restored = cache.ReplayItems().Single(value => value.Id == incoming.Id);
            if (restored.Payload!.Value<int>("uv") != 12)
                throw new InvalidOperationException("修改原始任务影响了缓存快照。");
            restored.Payload["uv"] = 888;
            if (cache.ReplayItems().Single(value => value.Id == incoming.Id).Payload!.Value<int>("uv") != 12)
                throw new InvalidOperationException("修改恢复的任务影响了缓存快照。");
            if (!await cache.ClaimAsync("43135").ConfigureAwait(false)
                || await cache.ClaimAsync("43135").ConfigureAwait(false))
                throw new InvalidOperationException("消费日志未阻止重复领取。");
            await cache.FlushAsync().ConfigureAwait(false);
        }

        await using (var cache = await LocalTaskCache<TaskEnvelope>.OpenAsync(options, Key).ConfigureAwait(false))
        {
            var pending = cache.ReplayItems().Single();
            if (pending.Id != 43136 || pending.Payload!.Value<int>("uv") != 12
                || pending.Payload["extension"]!.Value<string>("date") != date)
                throw new InvalidOperationException("写入新缓存后再次恢复不正确。");
            if ((await cache.StageBatchAsync([task with { Id = 43135 }]).ConfigureAwait(false)).Count != 0)
                throw new InvalidOperationException("重启后已消费的任务重新加入了缓存。");
        }

        var snapshot = JsonData.ParseObject(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        if (snapshot.Value<int>("Version") != 2 || snapshot["Tasks"] is not JArray { Count: 1 })
            throw new InvalidOperationException("迁移后缓存文件的外部结构发生变化。");
        return "PASS\nNewtonsoft.Json：可选 jstext 的缺失/null/空白/对象/对象字符串及任务快照、无效值拒绝、"
            + "动态 Payload 保留字符串日期及嵌套字段、整数校验、额外内容拒绝、"
            + "旧 v2 缓存/v1 消费日志兼容、UTC 日期恢复、缓存快照隔离、重复领取拒绝及重启去重通过。";
    }

    private static void VerifyOptionalJstext(TaskEnvelope template)
    {
        (string Payload, string? Expected)[] cases =
        [
            ("{}", null),
            ("""{"jstext":null}""", null),
            ("""{"jstext":""}""", null),
            ("""{"jstext":"  \t\r\n  "}""", null),
            ("""{"jstext":"null"}""", null),
            ("""{"jstext":{}}""", "{}"),
            ("""{"jstext":"{}"}""", "{}"),
            ("""{"jstext":{"mode":"test","date":"2026-10-13T08:00:00Z","nested":{"enabled":true}}}""",
                """{"mode":"test","date":"2026-10-13T08:00:00Z","nested":{"enabled":true}}"""),
            ("""{"jstext":"{\"mode\":\"test\",\"date\":\"2026-10-13T08:00:00Z\"}"}""",
                """{"mode":"test","date":"2026-10-13T08:00:00Z"}""")
        ];
        foreach (var (json, expectedJson) in cases)
        {
            var raw = JsonData.ParseObject(json);
            var expected = expectedJson is null ? null : JsonData.ParseObject(expectedJson);
            var parsed = JsonData.ParseOptionalObject(raw["jstext"], "任务 jstext");
            if (!JToken.DeepEquals(parsed, expected))
                throw new InvalidOperationException($"可选 jstext 解析失败：{json}");
            // 回归实际报错路径：即使原始值为空，生成任务快照也应成功，并与执行器的数据一致。
            var snapshot = JsonData.ParseObject(BrowserTaskConsumer.BuildTaskSnapshot(
                template with { Payload = raw }, 12, 1, null, parsed));
            if (!JToken.DeepEquals(snapshot["jstext"], expected ?? (JToken)JValue.CreateNull())
                || !JToken.DeepEquals(snapshot["payload"], raw))
                throw new InvalidOperationException($"任务快照与可选 jstext 不一致：{json}");
        }
        string[] invalid =
        [
            """{"jstext":[]}""", """{"jstext":123}""", """{"jstext":true}""",
            """{"jstext":"[1,2]"}""", """{"jstext":"{invalid"}""", """{"jstext":"{} {}"}"""
        ];
        foreach (var json in invalid)
        {
            var rejected = false;
            try { JsonData.ParseOptionalObject(JsonData.ParseObject(json)["jstext"], "任务 jstext"); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException($"无效 jstext 未被拒绝：{json}");
        }
    }
}
