using System.Globalization;
using Newtonsoft.Json.Linq;
using TrafficVista.Net;

namespace TrafficVista.Business;

/// <summary>One UV's observed exit environment. Unknown provider fields remain in Details.</summary>
internal sealed record BrowserIpEnvironment(string? RealIp, string? TimezoneId,
    double? Latitude, double? Longitude, JObject? Details)
{
    public static BrowserIpEnvironment From(BusinessUvRun run)
    {
        var enabled = run.Options.ResolveRealIp || run.Options.FetchIpDetails;
        var realIp = enabled ? IpInfoValidation.Parse(run.Proxy?.RealIp)
            ?? IpInfoValidation.GetRealIp(run.Proxy?.IpInfo) : null;
        var info = run.Options.FetchIpDetails ? (JObject?)run.Proxy?.IpInfo?.DeepClone() : null;
        var location = info?["location"] as JObject;
        var zone = String(info?["timezone"]) ?? String((info?["timezone"] as JObject)?["id"])
            ?? String(location?["timezone"]);
        if (zone is not null)
        {
            try
            {
                var resolved = TimeZoneInfo.FindSystemTimeZoneById(zone);
                zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(resolved.Id, out var iana) ? iana : resolved.Id;
            }
            catch (TimeZoneNotFoundException) { zone = null; }
            catch (InvalidTimeZoneException) { zone = null; }
        }
        var lat = Number(info?["lat"]) ?? Number(info?["latitude"]) ?? Number(location?["latitude"]);
        var lon = Number(info?["lon"]) ?? Number(info?["longitude"]) ?? Number(location?["longitude"]);
        if (String(info?["loc"])?.Split(',') is { Length: 2 } coordinates)
        {
            lat ??= Number(new JValue(coordinates[0]));
            lon ??= Number(new JValue(coordinates[1]));
        }
        if (lat is not (>= -90 and <= 90) || lon is not (>= -180 and <= 180)) { lat = null; lon = null; }
        return new(realIp, zone, lat, lon, info);
    }

    private static string? String(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
    private static double? Number(JToken? token) => token is JValue
        && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : null;
}
