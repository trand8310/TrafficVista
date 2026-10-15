using System.Globalization;
using System.Text;
using Serilog;
using TrafficVista.Common;

namespace TrafficVista.Business;

internal static class ViewportIntegrationTest
{
    internal static string Run(ILogger logger)
    {
        // Option construction does not create a Playwright driver, launch Chrome or call device APIs.
        var runner = new BusinessUvBrowserRunner(null!, null!, null!, logger);
        var report = new StringBuilder("PASS\n");
        var resolutions = new[]
        {
            (1080, 2400, "Samsung", "SM-G991B"), (720, 1600, "Honor", "AMG-AN00"),
            (1440, 3216, "OnePlus", "NE2210"), (2400, 1080, "Samsung", "SM-G991B"),
            (480, 800, "Test", "LowResolution"), (540, 960, "Test", "OldPhone"),
            (720, 1280, "Test", "ShortPhone"), (1536, 2048, "Test", "Tablet"),
            (1768, 2208, "Test", "Foldable"), (2160, 3840, "Test", "4K"),
            (500, 500, "Test", "Square")
        };
        foreach (var (width, height, make, model) in resolutions)
        {
            var expected = AndroidViewportMatcher.Match(width, height, make, model);
            var repeated = AndroidViewportMatcher.Match(width, height, make, model);
            var device = new BusinessDeviceProfile("viewport-test", make, model, "", "14", width, height,
                "Mozilla/5.0 (Linux; Android 14) Mobile", 8, "", "", null);
            var run = new BusinessUvRun(1, 1, new("http://offline.invalid/"), "unused", device, null,
                new(), TimeSpan.Zero, 1, null);
            var launch = runner.CreateLaunchOptions(run);
            var args = launch.Args?.ToArray() ?? throw new InvalidOperationException("Chrome启动参数缺失。");
            var context = BusinessUvBrowserRunner.CreateContextOptions(run);
            if (expected.CssWidth != context.ViewportSize!.Width || expected.CssHeight != context.ViewportSize.Height
                || expected.CssWidth != context.ScreenSize!.Width || expected.CssHeight != context.ScreenSize.Height
                || expected.DeviceScaleFactor != context.DeviceScaleFactor
                || !args.Contains($"--window-size={expected.CssWidth},{expected.CssHeight}")
                || !args.Contains($"--screen-size={expected.CssWidth},{expected.CssHeight}")
                || !args.Contains($"--screen-avail-size={expected.CssWidth},{expected.CssHeight}")
                || !args.Contains("--device-pixel-ratio=" + expected.DeviceScaleFactor.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("Chrome/Context 参数未完整使用 AndroidViewportMatcher 输出。");
            if (expected.CssWidth != repeated.CssWidth || expected.CssHeight != repeated.CssHeight
                || expected.DeviceScaleFactor != repeated.DeviceScaleFactor)
                throw new InvalidOperationException("同设备屏幕匹配结果不稳定。");
            if (width > height && expected.PhysicalWidth != height)
                throw new InvalidOperationException("横向输入未使用 matcher 的竖屏归一化。");
            if (expected.IsFallback || expected.IsSuspiciousInput)
                throw new InvalidOperationException("有效设备不应因为旧手机比例/高度规则而回退或告警。");
            VerifyPhysicalConsistency(expected);
            report.AppendLine($"{make} {model} 输入={width}x{height} → {expected}");
        }
        foreach (var width in new[] { 240, 320, 480, 640, 720, 1080, 1280, 1440, 1536, 1768, 2160, 3840 })
            foreach (var ratio in new[] { 1.0, 1.25, 1.6, 1.78, 2.22, 2.7, 3.0 })
                VerifyPhysicalConsistency(AndroidViewportMatcher.Match(width, (int)(width * ratio), "Grid", "Physical"));
        foreach (var field in new[] { "dpr", "devicePixelRatio", "deviceScaleFactor" })
        {
            var deviceJson = "{\"success\":true,\"data\":[{\"ua\":\"test\",\"sw\":1080,\"sh\":2400,\"" + field + "\":\"3\"}]}";
            var device = HttpDeviceFingerprintProvider.ParseDevices(Encoding.UTF8.GetBytes(deviceJson)).Single();
            var matched = AndroidViewportMatcher.Match(device.ScreenWidth, device.ScreenHeight, device.Make, device.Model, device.DevicePixelRatio);
            var run = new BusinessUvRun(1, 1, new("http://offline.invalid/"), "unused", device, null, new(), TimeSpan.Zero, 1, null);
            var context = BusinessUvBrowserRunner.CreateContextOptions(run);
            if (matched.DeviceScaleFactor != 3 || matched.CssWidth != 360 || matched.CssHeight != 800
                || matched.DprSource != "device" || context.DeviceScaleFactor != 3 || context.ViewportSize!.Width != 360
                || !runner.CreateLaunchOptions(run).Args!.Contains("--device-pixel-ratio=3"))
                throw new InvalidOperationException("设备真实DPR没有贯通解析、匹配、启动与Context。");
            VerifyPhysicalConsistency(matched);
        }
        foreach (var invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            try { AndroidViewportMatcher.Match(1080, 2400, "Test", "Invalid", invalid); throw new InvalidOperationException("无效DPR未拒绝。"); }
            catch (ArgumentOutOfRangeException) { }
        }
        var limited = AndroidViewportMatcher.Match(64, 64);
        if (!limited.IsFallback || string.IsNullOrWhiteSpace(limited.DiagnosticReason))
            throw new InvalidOperationException("超出估算覆盖范围时缺少原因说明。");
        report.AppendLine("低分辨率、旧手机、方屏/折叠屏/平板、4K、84组尺寸比例一致性、真实DPR优先及贯通、无效DPR拒绝、覆盖限制原因验证通过。");
        return report.ToString();
    }

    private static void VerifyPhysicalConsistency(DeviceProfileResult result)
    {
        var tolerance = result.DeviceScaleFactor / 2.0 + .001;
        if (result.CssWidth <= 0 || result.CssHeight <= 0
            || Math.Abs(result.CssWidth * (double)result.DeviceScaleFactor - result.PhysicalWidth) > tolerance
            || Math.Abs(result.CssHeight * (double)result.DeviceScaleFactor - result.PhysicalHeight) > tolerance)
            throw new InvalidOperationException("CSS宽高没有按同一DPR等比换算：" + result);
    }
}
