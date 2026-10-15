namespace TrafficVista.Common;

using System.Text;

public sealed class DeviceProfileResult
{
    public int PhysicalWidth { get; set; }
    public int PhysicalHeight { get; set; }
    public int CssWidth { get; set; }
    public int CssHeight { get; set; }
    public float DeviceScaleFactor { get; set; }
    public double DprX { get; set; }
    public double DprY { get; set; }
    public double Score { get; set; }
    // 兼容已有调用；真实的方屏、宽屏比例不再视为输入异常。
    public bool IsSuspiciousInput { get; set; }
    public bool IsFallback { get; set; }
    public string DprSource { get; set; } = "estimated";
    public string MatchStrategy { get; set; } = "preferred";
    public string? DiagnosticReason { get; set; }
    public override string ToString() =>
        $"Physical={PhysicalWidth}x{PhysicalHeight}, CSS={CssWidth}x{CssHeight}, " +
        $"DPR={DeviceScaleFactor:F3}, DprX={DprX:F4}, DprY={DprY:F4}, " +
        $"Source={DprSource}, Strategy={MatchStrategy}, Fallback={IsFallback}, Reason={DiagnosticReason}";
}

/// <summary>优先使用设备DPR；缺失时稳定估算。仅凭物理分辨率不能还原设备真实缩放设置。</summary>
public static class AndroidViewportMatcher
{
    private const float Step = .125f;
    private const float MinEstimatedDpr = .5f;
    private const float MaxEstimatedDpr = 8f;
    private const int MinCssWidth = 320;
    private const int MaxCssHeight = 8192;
    private static readonly int[] CommonCssWidths = [320, 360, 384, 390, 393, 400, 411, 412, 420, 427, 432, 448, 450, 480, 512, 540];

    public static DeviceProfileResult Match(int width, int height) => Match(width, height, null, null);

    public static DeviceProfileResult Match(int width, int height, string? make, string? model, float? devicePixelRatio = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // 保持既有竖屏归一化约定；不裁剪、补齐或替换任何物理像素。
        int physicalWidth = Math.Min(width, height), physicalHeight = Math.Max(width, height);
        if (devicePixelRatio is { } supplied)
        {
            if (!float.IsFinite(supplied) || supplied <= 0)
                throw new ArgumentOutOfRangeException(nameof(devicePixelRatio), "设备DPR必须是有限正数。");
            return Create(physicalWidth, physicalHeight, supplied, 0, "device", "device-dpr");
        }

        var wideLayout = (double)physicalHeight / physicalWidth < 1.70;
        int maxCssWidth = wideLayout ? 1280 : 560;
        float preferredDpr = GetBaseDpr(physicalWidth, wideLayout);
        int variant = GetStableVariant(make, model, physicalWidth, physicalHeight);
        float[] preferred = variant switch
        {
            1 => [preferredDpr - Step, preferredDpr, preferredDpr + Step],
            2 => [preferredDpr + Step, preferredDpr, preferredDpr - Step],
            _ => [preferredDpr, preferredDpr - Step, preferredDpr + Step]
        };

        DeviceProfileResult? Choose(IEnumerable<float> candidates, string strategy)
        {
            DeviceProfileResult? best = null;
            int order = 0;
            foreach (var dpr in candidates.Distinct())
            {
                int rank = order++;
                if (dpr < MinEstimatedDpr || dpr > MaxEstimatedDpr) continue;
                var cw = Math.Round(physicalWidth / (double)dpr, MidpointRounding.AwayFromZero);
                var ch = Math.Round(physicalHeight / (double)dpr, MidpointRounding.AwayFromZero);
                if (cw < MinCssWidth || cw > maxCssWidth || ch < 1 || ch > MaxCssHeight) continue;
                var candidate = Create(physicalWidth, physicalHeight, dpr, 0, "estimated", strategy);
                // 不吸附常见CSS宽度：两个方向均仅按同一DPR取整。
                candidate.Score = Math.Abs(dpr - preferredDpr) * 1.5 + rank * .001
                    + CommonCssWidths.Min(w => Math.Abs(w - candidate.CssWidth)) * .002;
                if (best is null || candidate.Score < best.Score) best = candidate;
            }
            return best;
        }

        var result = Choose(preferred, "preferred");
        if (result is not null) return result;
        // 原档位不适合时扩展搜索，低分辨率设备不再困在旧DPR下限内。
        result = Choose(Enumerable.Range(4, 61).Select(i => i * Step), "expanded");
        if (result is not null) return result;

        // 超出覆盖范围时保留实际尺寸与缩放关系，提示估算限制。
        float limitedDpr = Math.Clamp(preferredDpr, MinEstimatedDpr, MaxEstimatedDpr);
        result = Create(physicalWidth, physicalHeight, limitedDpr, 9999, "estimated", "limited");
        result.IsFallback = true;
        result.DiagnosticReason = $"DPR估算范围{MinEstimatedDpr}～{MaxEstimatedDpr}无法满足CSS短边{MinCssWidth}～{maxCssWidth}，保留原分辨率按比例换算。";
        return result;
    }

    private static DeviceProfileResult Create(int width, int height, float dpr, double score, string source, string strategy)
    {
        var cssWidth = Math.Round(width / (double)dpr, MidpointRounding.AwayFromZero);
        var cssHeight = Math.Round(height / (double)dpr, MidpointRounding.AwayFromZero);
        if (cssWidth < 1 || cssHeight < 1 || cssWidth > 8192 || cssHeight > MaxCssHeight)
            throw new ArgumentOutOfRangeException(nameof(dpr), "换算后的视口不在当前资源保护范围1～8192像素内，请核对分辨率和DPR单位。");
        return new()
        {
            PhysicalWidth = width, PhysicalHeight = height,
            CssWidth = (int)cssWidth, CssHeight = (int)cssHeight, DeviceScaleFactor = dpr,
            DprX = width / cssWidth, DprY = height / cssHeight,
            Score = score, DprSource = source, MatchStrategy = strategy
        };
    }

    private static float GetBaseDpr(int width, bool wideLayout)
    {
        if (wideLayout && width >= 1200) return 2f;
        if (width < 400) return 1f;
        if (width < 600) return 1.5f;
        if (width < 720) return 1.75f;
        if (width < 900) return 2f;
        if (width < 1000) return 2.25f;
        if (width < 1150) return 2.625f;
        if (width < 1250) return 2.875f;
        if (width < 1380) return 3.125f;
        return 3.5f;
    }

    private static int GetStableVariant(string? make, string? model, int width, int height)
    {
        var key = (make ?? "").Trim().ToLowerInvariant() + "|" + (model ?? "").Trim().ToLowerInvariant() + "|" + width + "x" + height;
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(key)) { hash ^= value; hash = unchecked(hash * 16777619); }
        return (hash % 10) switch { <= 5 => 0, <= 7 => 1, _ => 2 };
    }
}
