using Newtonsoft.Json.Linq;
using TrafficVista.Configuration;
using TrafficVista.Contracts;
using TrafficVista.Net.Models;
using TrafficVista.Net.Options;
using TrafficVista.Net.Vendors;

namespace TrafficVista.Net;

internal static class IpzanIntegrationTest
{
    internal static string Run()
    {
        var map = new[] { new IpzanProxyAdapter.Region("湖南省", "430000"), new("长沙市", "430100"),
            new("长沙县", "430121"), new("北京市", "110100"), new("北京", "110000"), new("上海", "310000"),
            new("河北省", "130000"), new("测试市", "130100"), new("测试市", "430200") };
        var vendor = new ProxyVendorOptions { DefaultTtlSeconds = 180 };
        var task = new TaskEnvelope(1, "ipzan-offline", "https://offline.invalid/", DateTime.UtcNow)
            { Payload = new JObject { ["address"] = "湖南[23]:长沙[267]" } };
        var request = new ProxyAcquireRequest { Task = task, Settings = new() { ResolveRealIp = true } };
        const string url = "https://offline.invalid/get?num=20&num=9&area=old&xarea=keep&format=txt&realIp=0&secret=fixture%26value#fragment";
        var result = Query(IpzanProxyAdapter.BuildRequestUrl(url, vendor, request, map));
        Check(result["num"] == "1" && result["area"] == "430100" && result["xarea"] == "keep"
            && result["format"] == "json" && result["realIp"] == "1" && result["secret"] == "fixture&value", "精确参数替换与城市级映射");
        Check(new Uri(IpzanProxyAdapter.BuildRequestUrl(url, vendor, request, map)).Fragment == "#fragment", "保留片段");
        var off = Query(IpzanProxyAdapter.BuildRequestUrl("https://offline.invalid/get", vendor,
            request with { Count = 3, Settings = new() { ResolveRealIp = false } }, map));
        Check(off["num"] == "3" && off["format"] == "txt" && off["realIp"] == "0", "无查询串URL及真实IP关闭");
        ProxyAcquireRequest Address(JToken address) => request with { Task = task with { Payload = new JObject { ["address"] = address } } };
        Check(Query(IpzanProxyAdapter.BuildRequestUrl(url, vendor, Address("all"), map)).ContainsKey("area") == false, "全部地区清除旧定向");
        Check(Query(IpzanProxyAdapter.BuildRequestUrl(url, vendor, Address("湖南[23]:测试[1]"), map))["area"] == "430200", "同名城市校验所属省");
        var selected = new HashSet<string>();
        for (var i = 0; i < 64; i++)
        {
            var code = Query(IpzanProxyAdapter.BuildRequestUrl(url, vendor, Address(new JArray("北京[0]", "上海[2]", "湖南[23]:长沙[267]")), map))["area"];
            Check(new[] { "110100", "310000", "430100" }.Contains(code), "多地区仅选允许地区"); selected.Add(code);
        }
        // Avoid asserting a probabilistic requirement: membership is checked for every selection.
        Fails(() => IpzanProxyAdapter.BuildRequestUrl(url, vendor, Address("不存在[9]"), map), "未知地区不得变成非定向请求");
        Fails(() => IpzanProxyAdapter.BuildRequestUrl(url, vendor, Address("湖南[23]:不存在[9]"), map), "指定城市无匹配不得降级为省");
        Check(Query(IpzanProxyAdapter.BuildRequestUrl(url, vendor, request))["area"] == "430100", "应用目录地区文件加载");

        var acquired = DateTimeOffset.FromUnixTimeMilliseconds(1791959000000);
        var expiry = acquired.AddSeconds(70).ToUnixTimeMilliseconds();
        JObject Item(JToken? realIp = null) => new() { ["ip"] = "218.95.39.66", ["port"] = "12424",
            ["expired"] = expiry, ["net"] = "移动", ["real_ip"] = realIp };
        string Json(params JObject[] items) => new JObject { ["code"] = 0, ["status"] = 200,
            ["data"] = new JObject { ["list"] = new JArray(items) } }.ToString();
        var endpoint = IpzanProxyAdapter.ParseResponse(Json(Item("36.161.115.166")), vendor, acquired);
        Check(endpoint.Host == "218.95.39.66" && endpoint.Port == 12424 && endpoint.RealIp == "36.161.115.166"
            && endpoint.Ttl == TimeSpan.FromSeconds(70) && endpoint.ExpiresAt?.ToUnixTimeMilliseconds() == expiry,
            "给定JSON格式：连接IP与真实IP分离，毫秒绝对截止时间");
        Check(IpzanProxyAdapter.ParseResponse(Json(Item(new JObject())), vendor, acquired).RealIp is null, "无效真实IP保留回退查询");
        var missingExpiry = Item(); missingExpiry.Remove("expired");
        Check(IpzanProxyAdapter.ParseResponse(Json(missingExpiry), vendor, acquired).Ttl == TimeSpan.FromSeconds(180), "未提供过期时间用配置兜底");
        var expired = Item(); expired["expired"] = acquired.AddSeconds(-1).ToUnixTimeMilliseconds();
        var badPort = Item(); badPort["port"] = "99999";
        var malformedExpiry = Item(); malformedExpiry["expired"] = "not-a-timestamp";
        Check(IpzanProxyAdapter.ParseResponse(Json(expired, badPort, malformedExpiry, Item("36.161.115.166")), vendor, acquired).RealIp == "36.161.115.166",
            "批量列表跳过无效及已过期条目");
        Fails(() => IpzanProxyAdapter.ParseResponse(Json(expired), vendor, acquired), "全部已过期");
        Fails(() => IpzanProxyAdapter.ParseResponse(Json(), vendor, acquired), "空列表");
        Fails(() => IpzanProxyAdapter.ParseResponse("{\"code\":1001,\"message\":\"fixture\"}", vendor, acquired), "业务失败");
        Fails(() => IpzanProxyAdapter.ParseResponse("{bad-json", vendor, acquired), "损坏JSON");
        var txt = IpzanProxyAdapter.ParseResponse("invalid\r\nuser:pass@127.0.0.1:8080\r\n", vendor, acquired);
        Check(txt.Host == "127.0.0.1" && txt.Username == "user" && txt.Password == "pass" && txt.RealIp is null, "原TXT及认证解析兼容");
        return "PASS\nipzan专属URL参数替换、默认数量覆盖、多地区/省市归属、无匹配失败、应用目录地图、JSON真实IP及毫秒截止时间、无效/过期条目、TXT兼容验证通过。全部使用离线数据。";
    }
    private static Dictionary<string, string> Query(string url) => new Uri(url).Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(Action action, string message)
    {
        try { action(); } catch (ProxyProtocolException) { return; }
        throw new InvalidOperationException(message);
    }
}
