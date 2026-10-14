using Microsoft.Playwright;
using Newtonsoft.Json.Linq;

namespace TrafficVista;

internal static class ChromeIsolationTest
{
    public static async Task<string> RunAsync(ChromeInstanceManager manager, int count, bool headless = true)
    {
        var instances = Enumerable.Range(1, count).Select(manager.CreateInstance).ToArray();
        var key = "trafficvista_isolation_" + Guid.NewGuid().ToString("N");
        // 在实际实例内拦截同源页面，避免外部网站自身刷新、脚本影响并发验证。
        var testUrl = new Uri(new Uri(ChromeStartup.StartUrl), $"/{key}.html").AbsoluteUri;
        try
        {
            var rejected = false;
            try { await using var duplicate = manager.CreateInstance(1); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("重复用户 ID 未被拒绝。");
            // 另一管理器也不能绕过目录锁，模拟两个应用进程访问相同用户目录。
            using (var limiter = new BrowserLaunchLimiter(1))
            {
                await using var secondManager = new ChromeInstanceManager(instances[0].SharedPlaywright,
                    limiter, Serilog.Log.Logger, Path.GetDirectoryName(instances[0].UserDataDirectory));
                rejected = false;
                try { await using var duplicate = secondManager.CreateInstance(1); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("用户目录锁未生效。");
            }
            await Task.WhenAll(instances.Select(instance => instance.RunAsync(headless: headless,
                startUrl: testUrl, initializePage: page => PrepareTestPageAsync(page, testUrl))));
            if (instances.Select(instance => instance.UserDataDirectory).Distinct().Count() != count)
                throw new InvalidOperationException("实例共用了用户数据目录。");
            if (instances.Any(instance => !ReferenceEquals(instance.SharedPlaywright, instances[0].SharedPlaywright)))
                throw new InvalidOperationException("实例没有共享同一个 IPlaywright。");
            if (manager.PeakConcurrentStarts > manager.MaxConcurrentStarts)
                throw new InvalidOperationException("同时启动的数量超过设定上限。");

            var pages = instances.Select(instance => instance.Page!).ToArray();
            try
            {
                await SetStateAsync(pages[0], key, "instance-0");
                await Task.WhenAll(pages.Skip(1).Select(page => AssertStateAsync(page, key, expected: null)));
                await Task.WhenAll(pages.Skip(1).Select((page, index) => SetStateAsync(page, key, $"instance-{index + 1}")));
                await Task.WhenAll(pages.Select((page, index) => AssertStateAsync(page, key, $"instance-{index}")));
                var queueTestIndex = Math.Min(1, instances.Length - 1);
                await VerifyOperationQueueAsync(instances[queueTestIndex], pages[queueTestIndex]);
                await Task.WhenAll(instances.Select((instance, index) => instance.ExecuteAsync(_ => VerifyTouchAsync(pages[index]))));
            }
            finally
            {
                await Task.WhenAll(pages.Select(page => ClearStateAsync(page, key)));
            }

            await VerifyPersistenceAsync(manager, instances[0], key, testUrl, headless);
            if (instances[0].IsRunning || instances.Skip(1).Any(instance => !instance.IsRunning))
                throw new InvalidOperationException("单独停止实例影响了其他实例。");
            await Task.WhenAll(instances.Skip(1).Select((instance, index) => instance.ExecuteAsync(async _ =>
            {
                var page = pages[index + 1];
                await Assertions.Expect(page.Locator("body")).ToBeVisibleAsync();
                await page.EvaluateAsync<int>("() => 1");
            })));
            return $"PASS\n{count} 个独立实例使用同一个 IPlaywright，Headless={headless}。\n"
                + $"启动并发峰值：{manager.PeakConcurrentStarts}，设定上限：{manager.MaxConcurrentStarts}。\n"
                + "Cookie、localStorage、sessionStorage 隔离通过；单独关闭互不影响。\n"
                + "数字用户 ID 排他、目录锁、关闭后同 ID 恢复 Cookie/localStorage/IndexedDB 通过。\n"
                + "手机视口、触屏能力、真实且可信的 touchstart/click 事件通过；同实例业务操作串行通过。\n"
                + string.Join("\n", instances.Select(instance => instance.UserDataDirectory));
        }
        finally
        {
            await Task.WhenAll(instances.Select(instance => instance.DisposeAsync().AsTask()));
        }
    }

    private static async Task VerifyPersistenceAsync(ChromeInstanceManager manager, ChromeDemo original,
        string key, string url, bool headless)
    {
        var page = original.Page!;
        await SetStateAsync(page, key, "saved-user");
        await page.EvaluateAsync("""
            key => new Promise((resolve, reject) => {
                const request = indexedDB.open(key, 1);
                request.onupgradeneeded = () => request.result.createObjectStore('state');
                request.onerror = () => reject(request.error);
                request.onsuccess = () => {
                    const db = request.result;
                    const tx = db.transaction('state', 'readwrite');
                    tx.objectStore('state').put('saved-user', 'value');
                    tx.oncomplete = () => { db.close(); resolve(); };
                    tx.onerror = () => reject(tx.error);
                };
            })
            """, key);
        await original.DisposeAsync();
        await using var restored = manager.CreateInstance(original.UserId);
        await restored.RunAsync(headless: headless, startUrl: url,
            initializePage: newPage => PrepareTestPageAsync(newPage, url));
        var restoredPage = restored.Page!;
        var cookie = (await restoredPage.Context.CookiesAsync()).SingleOrDefault(value => value.Name == key)?.Value;
        var local = await restoredPage.EvaluateAsync<string?>("key => localStorage.getItem(key)", key);
        var session = await restoredPage.EvaluateAsync<string?>("key => sessionStorage.getItem(key)", key);
        var indexed = await restoredPage.EvaluateAsync<string?>("""
            key => new Promise((resolve, reject) => {
                const request = indexedDB.open(key);
                request.onerror = () => reject(request.error);
                request.onsuccess = () => {
                    const db = request.result;
                    const read = db.transaction('state').objectStore('state').get('value');
                    read.onsuccess = () => { db.close(); resolve(read.result); };
                    read.onerror = () => reject(read.error);
                };
            })
            """, key);
        if (cookie != "saved-user" || local != "saved-user" || indexed != "saved-user" || session is not null)
            throw new InvalidOperationException("同用户会话恢复失败，或错误地恢复了 sessionStorage。");
        await ClearStateAsync(restoredPage, key);
        await restoredPage.EvaluateAsync("key => new Promise((resolve, reject) => { const r = indexedDB.deleteDatabase(key); r.onsuccess = () => resolve(); r.onerror = () => reject(r.error); })", key);
    }

    private static async Task PrepareTestPageAsync(IPage page, string url)
    {
        page.Crash += (_, _) => Serilog.Log.Error("隔离测试页 renderer 崩溃，Url={Url}", page.Url);
        page.Close += (_, _) => Serilog.Log.Information("隔离测试页关闭，Url={Url}", page.Url);
        await page.RouteAsync(url, route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html; charset=utf-8",
            Body = """
                <!doctype html><html><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>H5 isolation test</title></head>
                <body style="margin:20px;font:16px sans-serif">
                <button style="width:160px;height:60px" id="touch">Touch test</button>
                <p id="result" data-touch-count="0">waiting</p>
                <script>
                  document.querySelector('#touch').addEventListener('touchstart', event => {
                    const result = document.querySelector('#result');
                    result.dataset.touchCount = Number(result.dataset.touchCount) + 1;
                    result.dataset.trusted = event.isTrusted;
                  });
                  document.querySelector('#touch').addEventListener('click', () => {
                    document.querySelector('#result').textContent = 'touch-success';
                  });
                </script></body></html>
                """
        }));
    }

    private static async Task VerifyTouchAsync(IPage page)
    {
        var metrics = await page.EvaluateAsync<string>("""
            () => JSON.stringify({touchPoints:navigator.maxTouchPoints,coarse:matchMedia('(pointer: coarse)').matches,
                width:innerWidth,screenWidth:screen.width,scale:devicePixelRatio,url:location.href})
            """);
        var actual = JObject.Parse(metrics);
        if (actual.Value<int>("touchPoints") < 1
            || !actual.Value<bool>("coarse")
            || actual.Value<int>("width") != ChromeStartup.ViewportWidth
            || actual.Value<int>("screenWidth") != ChromeStartup.ViewportWidth
            || Math.Abs(actual.Value<double>("scale") - ChromeStartup.PixelRatio) > 0.00001)
            throw new InvalidOperationException($"手机视口或触屏能力未生效：{metrics}");
        await page.BringToFrontAsync();
        await ChromeTouch.TapAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Touch test" }));
        // 手机浏览器可能延迟合成 click，等待真实页面结果，不只检查指令返回。
        await Assertions.Expect(page.Locator("#result")).ToHaveTextAsync("touch-success");
        var valid = await page.EvaluateAsync<bool>("""
            () => document.querySelector('#result').dataset.touchCount === '1'
                && document.querySelector('#result').dataset.trusted === 'true'
            """);
        if (!valid)
            throw new InvalidOperationException("没有收到可信的 touchstart 事件。");
    }

    private static async Task VerifyOperationQueueAsync(ChromeDemo instance, IPage page)
    {
        await instance.ExecuteAsync(_ => page.EvaluateAsync("() => document.documentElement.dataset.operationCount = '0'"));
        // 非原子读写含异步间隔：如果同页请求并发交错，最终值会小于 8。
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => instance.ExecuteAsync(_ => page.EvaluateAsync("""
            async () => {
                const value = Number(document.documentElement.dataset.operationCount);
                await new Promise(resolve => setTimeout(resolve, 25));
                document.documentElement.dataset.operationCount = value + 1;
            }
            """))));
        await instance.ExecuteAsync(async _ =>
        {
            if (await page.EvaluateAsync<int>("() => Number(document.documentElement.dataset.operationCount)") != 8)
                throw new InvalidOperationException("同实例的业务操作发生了交错。");
        });
    }

    private static async Task SetStateAsync(IPage page, string key, string value)
    {
        await page.Context.AddCookiesAsync(new[]
        {
            new Cookie { Name = key, Value = value, Url = ChromeStartup.StartUrl }
        });
        await page.EvaluateAsync("""
            ([key, value]) => {
                localStorage.setItem(key, value);
                sessionStorage.setItem(key, value);
            }
            """, new[] { key, value });
    }

    private static async Task AssertStateAsync(IPage page, string key, string? expected)
    {
        var cookies = await page.Context.CookiesAsync(new[] { ChromeStartup.StartUrl });
        var cookieValue = cookies.SingleOrDefault(cookie => cookie.Name == key)?.Value;
        var localValue = await page.EvaluateAsync<string?>("key => localStorage.getItem(key)", key);
        var sessionValue = await page.EvaluateAsync<string?>("key => sessionStorage.getItem(key)", key);
        if (cookieValue != expected || localValue != expected || sessionValue != expected)
            throw new InvalidOperationException("Cookie 或页面存储的实例隔离验证失败。");
    }

    private static async Task ClearStateAsync(IPage page, string key)
    {
        if (page.IsClosed)
            return;
        await page.Context.ClearCookiesAsync(new() { Name = key });
        await page.EvaluateAsync("""
            key => {
                localStorage.removeItem(key);
                sessionStorage.removeItem(key);
            }
            """, key);
    }
}
