using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrafficVista.Serialization;

/// <summary>JSON.NET 读取约定：动态 JSON 中的日期保持字符串，拒绝正文之后的额外内容。</summary>
internal static class JsonData
{
    private static readonly JsonSerializerSettings ReadSettings = new()
    {
        DateParseHandling = DateParseHandling.None,
        CheckAdditionalContent = true,
        TypeNameHandling = TypeNameHandling.None
    };

    public static T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, ReadSettings);

    public static JObject ParseObject(string json) => Deserialize<JObject>(json)
        ?? throw new InvalidDataException("JSON 正文必须是对象，不能为 null。");

    public static JObject ParseObject(ReadOnlyMemory<byte> json) =>
        ParseObject(Encoding.UTF8.GetString(json.Span).TrimStart('\uFEFF'));

    /// <summary>可选配置支持直接对象和 JSON 对象字符串；缺失、null、空白均表示没有配置。</summary>
    public static JObject? ParseOptionalObject(JToken? value, string name)
    {
        if (value is null || value.Type is JTokenType.Null or JTokenType.Undefined) return null;
        if (value is JObject obj) return (JObject)obj.DeepClone();
        if (value.Type != JTokenType.String)
            throw new InvalidDataException($"{name} 必须是 JSON 对象或包含 JSON 对象的字符串。");

        var text = value.Value<string>();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return Deserialize<JObject>(text); }
        catch (JsonException ex)
        { throw new InvalidDataException($"{name} 不是有效 JSON 对象：{ex.Message}", ex); }
    }

    // 保持任务字段的整数校验：字符串、小数和溢出值不会被自动转换为整数。
    public static bool TryGetInt32(JToken? value, out int number)
    {
        number = 0;
        return value?.Type == JTokenType.Integer && int.TryParse(value.ToString(Formatting.None),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }
}
