using Microsoft.Playwright;

namespace TrafficVista;

// 在这里统一初始化网址、Chrome 启动参数和页面参数。
internal static class ChromeStartup
{
    public const string StartUrl = "http://106.53.161.213:6617/beijing.html";
    // 只限制同时启动并加载首个页面的数量，不限制已运行实例总数。
    public const int MaxConcurrentStarts = 4;
    public const int ViewportWidth = 412;
    public const int ViewportHeight = 829;
    public const int ScreenHeight = 915;
    public const float PixelRatio = 2.625f;
    public const string MobileUserAgent = "Mozilla/5.0 (Linux; U; Android 16; zh-cn; 23117RK66C Build/BP2A.250605.031.A3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36";

    // 对应批处理的 %~dp0145_s1；每个实例在此目录下有独立的 ID 子目录。
    public static string ProfileRoot => Path.Combine(AppContext.BaseDirectory, "145_s1");

    public static BrowserTypeLaunchOptions CreateLaunchOptions(bool headless = false,
        string? userDataDirectory = null)
    {
        // %~dp0 是批处理语法；C# 使用程序目录构造绝对路径。
        var chromeLogPath = Path.Combine(userDataDirectory ?? AppContext.BaseDirectory, "145.log");
        return new BrowserTypeLaunchOptions
        {
            ExecutablePath = Path.Combine(AppContext.BaseDirectory,
                "File", "chrome-win", "145.0.7632.110", "chrome.exe"),
            Headless = headless,
            // 并发业务默认不加演示延迟；观察操作时可改为 350。
            SlowMo = 0,
            Timeout = 30_000,
            // H5 手机触屏模式；如需平板可调整视口和屏幕尺寸。
            // TimezoneId = "Asia/Shanghai",

            // 你的 Chrome 145 默认启动参数统一放在这里。
            // LaunchAsync 使用临时 Chrome profile；数字用户目录保存 StorageState。
            // 不要在 Args 中传 --user-data-dir；完整 profile 需要 LaunchPersistentContextAsync。
            Args = new[]
            {
                "--lang=zh-CN",
                "--enable-logging",
                "--v=1",
                $"--log-file={chromeLogPath}",
                "--no-first-run",
                "--no-default-browser-check",
                "--show-avatar-button=never",
                "--force-prefers-no-reduced-motion",
                "--enable-unsafe-swiftshader",
                "--hide-bad-flags",
                "--hide-crashed-bubble",
                "--ignore-certificate-errors",
                "--webgl-vendor=Google Inc. (Qualcomm)",
                "--webgl-renderer=ANGLE (Qualcomm, Adreno (TM) 750, OpenGL ES 3.2)",
                "--hardware-concurrency=8",
                "--device-memory=8",
                "--js-memory-info=29400000|31200000|1130000000",
                "--storage-quota=0|138500073060",
                $"--user-agent={MobileUserAgent}",
                "--force-webrtc-ip-handling-policy",
                "--webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--webrtc-ip=118.248.225.244",
                "--window-position=0,0",
                "--window-size=412,915",
                "--device-pixel-ratio=2.625",
                "--screen-size=412,915",
                "--screen-avail-size=412,829",
                "--screen-color-depth=24",
                "--platform=Android",
                "--platform-version=16.0.0",
                "--product-model=23117RK66C",
                "--make=samsung",
                "--brand-name=XiaoMiBrowser",
                "--full-version=128.0.6613.100",
                "--max-touch-points=5",
                "--touch-events=enabled",
                "--netinfo-type=4g",
                "--netinfo-effective=4g",
                "--netinfo-rtt=200",
                "--enable-battery-charging=0",
                "--battery-level=0.85",
                "--battery-charging-time=0",
                "--battery-discharging-time=0",
                "--geolocation-permission=allow",
                "--ssl-grease-cipher=1231",
                "--fingerprint=1233456"
            },

            // 如需代理，可在这里配置：
            // Proxy = new Proxy { Server = "http://127.0.0.1:7890" },
        };
    }

    public static BrowserNewContextOptions CreateContextOptions(string statePath) => new()
    {
        HasTouch = true,
        IsMobile = true,
        ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        ScreenSize = new ScreenSize { Width = ViewportWidth, Height = ScreenHeight },
        DeviceScaleFactor = PixelRatio,
        UserAgent = MobileUserAgent,
        IgnoreHTTPSErrors = true,
        ReducedMotion = ReducedMotion.NoPreference,
        Locale = "zh-CN",
        StorageStatePath = File.Exists(statePath) ? statePath : null
    };

}
