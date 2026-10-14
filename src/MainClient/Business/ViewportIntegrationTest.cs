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
            (500, 500, "Test", "Fallback")
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
            if (width == height && (!expected.IsFallback || !expected.IsSuspiciousInput))
                throw new InvalidOperationException("异常屏幕比例回退标记丢失。");
            report.AppendLine($"{make} {model} 输入={width}x{height} → {expected}");
        }
        report.AppendLine("Chrome启动参数、Context viewport/screen/DPR、稳定性、横向输入归一化及回退标记验证通过。");
        return report.ToString();
    }
}
