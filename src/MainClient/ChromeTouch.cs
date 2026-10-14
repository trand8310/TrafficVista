using Microsoft.Playwright;

namespace TrafficVista;

// 当前指定 Chrome 的触摸点击封装，发送真实 touch 手势，不用 JS 派发事件。
public static class ChromeTouch
{
    public static async Task TapAsync(IPage page, ILocator target)
    {
        await target.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var box = await target.BoundingBoxAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("触摸目标不可见。");
        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height / 2).ConfigureAwait(false);
    }
}
