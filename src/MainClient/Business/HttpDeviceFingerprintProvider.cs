using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Serilog;
using TrafficVista.Configuration;
using TrafficVista.Serialization;

namespace TrafficVista.Business;

/// <summary>
/// 调设备接口（DevApiUrl）并池化真实设备指纹；每个 UV 发放一台，池空时补拉。
/// 协议：{"success":true,"count":N,"data":[{osv,sw,sh,make,model,ua,androidid,imei,mac,oaid,name,vendor,renderer,cpu,storage}]}。
/// </summary>
public sealed class HttpDeviceFingerprintProvider(HttpClient client, ILogger logger) : IBusinessDeviceProvider, IDisposable
{
    private readonly SemaphoreSlim _fetch = new(1, 1);
    private readonly ConcurrentQueue<BusinessDeviceProfile> _pool = new();
    private long _issued;
    private string? _source;
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public async ValueTask<BusinessDeviceProfile> AcquireAsync(TrafficVistaOptions options, long taskId, long uvId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(options.DevApiUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("请先配置完整的设备接口 HTTP/HTTPS 地址。");
        BusinessDeviceProfile? device = null;
        await _fetch.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_source != uri.AbsoluteUri)
            {
                _pool.Clear();
                _source = uri.AbsoluteUri;
            }
            if (!_pool.TryDequeue(out device))
            {
                await FetchAsync(uri, token).ConfigureAwait(false);
                if (!_pool.TryDequeue(out device)) throw new InvalidDataException("设备接口没有可用设备。");
            }
        }
        finally { _fetch.Release(); }
        // 同一设备记录可以被不同进程周期再次发放；DeviceKey 保证本次 UV 会话唯一可追踪。
        return device! with { DeviceKey = $"{Interlocked.Increment(ref _issued)}:{taskId}:{uvId}" };
    }

    private async Task FetchAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(FetchTimeout);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var batch = ParseDevices(await BusinessHttp.ReadCappedAsync(response, MaxBytes, "设备接口", timeout.Token)
            .ConfigureAwait(false));
        foreach (var device in batch) _pool.Enqueue(device);
        logger.Debug("设备指纹补拉完成，Count={Count}，Pending={Pending}", batch.Count, _pool.Count);
    }

    public static IReadOnlyList<BusinessDeviceProfile> ParseDevices(ReadOnlyMemory<byte> json)
    {
        var root = JsonData.ParseObject(json);
        if (root["success"] is not { Type: JTokenType.Boolean } success || !success.Value<bool>())
            throw new InvalidDataException("设备接口返回 success 不是 true。");
        if (root["data"] is not JArray data)
            throw new InvalidDataException("设备接口 data 必须是数组。");
        var list = new List<BusinessDeviceProfile>();
        foreach (var node in data)
        {
            if (node is not JObject item) throw new InvalidDataException("设备接口 data 的每项必须是对象。");
            var userAgent = GetString(item, "ua");
            var width = GetInt(item, "sw");
            var height = GetInt(item, "sh");
            // 缺关键字段的条目直接跳过，不让单个脏数据毁掉整批。
            if (string.IsNullOrWhiteSpace(userAgent) || width is not > 0 || height is not > 0) continue;
            list.Add(new BusinessDeviceProfile("", GetString(item, "make") ?? "", GetString(item, "model") ?? "",
                GetString(item, "name") ?? "", GetString(item, "osv") ?? "", width.Value, height.Value, userAgent!,
                GetInt(item, "cpu") ?? 8, GetString(item, "vendor") ?? "", GetString(item, "renderer") ?? "",
                GetString(item, "oaid")) { DevicePixelRatio = GetDevicePixelRatio(item) });
        }
        if (list.Count == 0) throw new InvalidDataException("设备接口没有可用设备。");
        return list;
    }

    private static float? GetDevicePixelRatio(JObject item)
    {
        foreach (var name in new[] { "devicePixelRatio", "deviceScaleFactor", "dpr" })
        {
            var value = item.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (value is null || value.Type == JTokenType.Null) continue;
            if (value.Type is not (JTokenType.Integer or JTokenType.Float or JTokenType.String)
                || !float.TryParse(value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Newtonsoft.Json.Formatting.None), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dpr) || !float.IsFinite(dpr) || dpr <= 0)
                throw new InvalidDataException("设备接口DPR必须是有限正数：" + name);
            return dpr;
        }
        return null;
    }

    private static string? GetString(JObject item, string name) =>
        item[name] is { Type: JTokenType.String } value ? value.Value<string>() : null;

    private static int? GetInt(JObject item, string name)
    {
        if (!item.TryGetValue(name, out var value)) return null;
        if (JsonData.TryGetInt32(value, out var number)) return number;
        // 接口部分字段是字符串数字（如 "cpu":"8"）。
        return value.Type == JTokenType.String && int.TryParse(value.Value<string>(), out var parsed) ? parsed : null;
    }

    public void Dispose() => _fetch.Dispose();
}
