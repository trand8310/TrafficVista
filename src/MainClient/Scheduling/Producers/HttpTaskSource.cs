using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Serialization;
using TrafficVista.Business;

namespace TrafficVista.Scheduling.Producers;

/// <summary>按用户提供的 GET 协议取任务；每批调用一次，轮询间隔由调度器负责。</summary>
public sealed class HttpTaskSource(HttpClient client, ILogger logger, BusinessOptions? business = null) : ITaskSource
{
    private TrafficVistaOptions _options = new();
    public void Configure(TrafficVistaOptions options)
    {
        _ = BuildRequestUri(options);
        _options = options with { };
    }

    public static Uri BuildRequestUri(TrafficVistaOptions options)
    {
        if (!Uri.TryCreate(options.TaskApiUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("任务接口请输入完整 HTTP/HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(options.TaskName)) throw new InvalidOperationException("请输入任务名称。");
        // 业务模式允许外部任务接口；本机回环不再强制。
        var builder = new UriBuilder(uri);
        var parts = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !new[] { "name", "action" }.Contains(
                Uri.UnescapeDataString(part.Split('=')[0].Replace("+", " ")), StringComparer.OrdinalIgnoreCase)).ToList();
        //?action=getTask&name=jstest
        parts.Add("action=getTask");
        parts.Add("name=" + Uri.EscapeDataString(options.TaskName));
        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    public async Task<IReadOnlyList<TaskEnvelope>> FetchAsync(CancellationToken token)
    {
        var uri = BuildRequestUri(_options);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("任务响应超过 4 MB。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("任务响应超过 4 MB。");
            await buffer.WriteAsync(bytes.AsMemory(0, read), token).ConfigureAwait(false);
        }
        IReadOnlyList<TaskEnvelope> result;
        try { result = ParseResponse(buffer.ToArray()); }
        catch (Exception)
        {
            var recovery = Path.GetFullPath((business ?? new()).RecoveryRoot, AppContext.BaseDirectory);
            Directory.CreateDirectory(recovery);
            await File.WriteAllBytesAsync(Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".response.json"),
                buffer.ToArray(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        logger.Debug("任务提取完成，TaskName={TaskName}，Count={Count}", _options.TaskName, result.Count);
        return result;
    }

    public static IReadOnlyList<TaskEnvelope> ParseResponse(ReadOnlyMemory<byte> json)
    {
        var root = JsonData.ParseObject(json);
        if (root["success"] is not { Type: JTokenType.Boolean } success)
            throw new InvalidDataException("任务响应 success 必须是布尔值。");
        if (root["data"] is not JArray data) throw new InvalidDataException("任务响应 data 必须是数组。");
        if (!success.Value<bool>())
        {
            var message = root.Value<string>("message");
            if (data.Count == 0 && message == "没有可用任务") return [];
            throw new InvalidOperationException($"任务接口返回失败：{message ?? "未知错误"}");
        }
        var tasks = new List<TaskEnvelope>();
        foreach (var node in data)
        {
            if (node is not JObject item) throw new InvalidDataException("任务响应 data 的每项必须是对象。");
            if (item["id"] is not { Type: JTokenType.Integer } idToken)
                throw new InvalidDataException("任务 id 必须是整数。");
            var id = idToken.Value<long>();
            var url = item.Value<string>("url") ?? "";
            if (id <= 0 || !Uri.TryCreate(url, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("任务 id 或 url 无效。");
            var name = item.Value<string>("title") ?? "";
            // 保留 uv/pv/sleep/jstext 等原始字段，后续业务执行器按协议读取。
            tasks.Add(new TaskEnvelope(id, name, url, DateTime.UtcNow) { Payload = (JObject)item.DeepClone() });
        }
        return tasks;
    }
}
