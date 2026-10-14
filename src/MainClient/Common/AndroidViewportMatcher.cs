namespace TrafficVista.Common
{
    using System;
    using System.Diagnostics;
    using System.Linq;
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

        public bool IsSuspiciousInput { get; set; }
        public bool IsFallback { get; set; }

        public override string ToString()
        {
            return $"Physical={PhysicalWidth}x{PhysicalHeight}, CSS={CssWidth}x{CssHeight}, " +
                   $"DPR={DeviceScaleFactor:F3}, DprX={DprX:F4}, DprY={DprY:F4}, " +
                   $"Score={Score:F6}, Suspicious={IsSuspiciousInput}, Fallback={IsFallback}";
        }
    }

    public static class AndroidViewportMatcher
    {
        /*
         * 设计思路：
         *
         * 1. 以 1080 宽物理屏 -> DPR 2.625 为核心基准。
         * 2. 按物理宽度分大档位，每个档位只允许小幅微调：
         *
         *      720 ~ 899    -> Base DPR 2.000
         *      900 ~ 999    -> Base DPR 2.250
         *      1000 ~ 1149  -> Base DPR 2.625
         *      1150 ~ 1249  -> Base DPR 2.875
         *      1250 ~ 1379  -> Base DPR 3.125
         *      1380 ~ 1500  -> Base DPR 3.500
         *
         * 3. 每个档位围绕 Base DPR 做 {-0.125, 0, +0.125} 微调。
         * 4. make + model 用于生成稳定种子：
         *      同样的 make/model + 物理分辨率
         *      -> 永远得到相同 DPR 候选优先级
         *      -> 不依赖 Random，不受进程重启影响。
         * 5. 最终仍会校验 CSS width/height、宽高比和 DPR 一致性，
         *    避免稳定但不合理的组合。
         */

        private const float DprStep = 0.125f;
        private const float MinDpr = 1.75f;
        private const float MaxDpr = 3.75f;

        private const int MinCssWidth = 320;
        private const int MaxCssWidth = 560;

        private const int MinCssHeight = 600;
        private const int MaxCssHeight = 1350;

        private const double MinPhoneRatio = 1.70;
        private const double MaxPhoneRatio = 2.60;

        private static readonly int[] CommonCssWidths =
        {
            320,
            360,
            384,
            390,
            393,
            400,
            411,
            412,
            420,
            427,
            432,
            448,
            450,
            480,
            512,
            540
        };

        public static DeviceProfileResult Match(
            int width,
            int height,
            string? make,
            string? model)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "分辨率必须大于 0");

            int physicalWidth = Math.Min(width, height);
            int physicalHeight = Math.Max(width, height);

            double physicalRatio =
                (double)physicalHeight / physicalWidth;

            bool suspiciousInput =
                physicalRatio < MinPhoneRatio ||
                physicalRatio > MaxPhoneRatio;

            float baseDpr =
                GetBaseDprByPhysicalWidth(physicalWidth);

            int stableVariant =
                GetStableVariant(
                    make,
                    model,
                    physicalWidth,
                    physicalHeight);

            float[] candidateDprs =
                BuildStableCandidates(baseDpr, stableVariant);

            DeviceProfileResult? best = null;

            for (int i = 0; i < candidateDprs.Length; i++)
            {
                float dpr = candidateDprs[i];

                if (dpr < MinDpr || dpr > MaxDpr)
                    continue;

                var candidate = BuildCandidate(
                    physicalWidth,
                    physicalHeight,
                    dpr,
                    suspiciousInput,
                    baseDpr,
                    i);

                if (candidate == null)
                    continue;

                if (best == null ||
                    candidate.Score < best.Score)
                {
                    best = candidate;
                }
            }

            if (best != null)
            {
                Debug.WriteLine(
                    $"[AndroidViewportMatcher] make={make}, model={model}, " +
                    $"BaseDpr={baseDpr:F3}, StableVariant={stableVariant}, Matched={best}");

                return best;
            }

            var fallback = BuildFallback(
                physicalWidth,
                physicalHeight,
                suspiciousInput,
                baseDpr);

            Debug.WriteLine(
                $"[AndroidViewportMatcher] make={make}, model={model}, " +
                $"Fallback={fallback}");

            return fallback;
        }

        // 兼容旧调用方式。
        public static DeviceProfileResult Match(
            int width,
            int height)
        {
            return Match(
                width,
                height,
                null,
                null);
        }

        private static float GetBaseDprByPhysicalWidth(
            int physicalWidth)
        {
            if (physicalWidth < 900)
                return 2.000f;

            if (physicalWidth < 1000)
                return 2.250f;

            if (physicalWidth < 1150)
                return 2.625f;

            if (physicalWidth < 1250)
                return 2.875f;

            if (physicalWidth < 1380)
                return 3.125f;

            return 3.500f;
        }

        /*
         * stableVariant：
         *
         * 0 -> 偏向 baseDpr
         * 1 -> 偏向 baseDpr - 0.125
         * 2 -> 偏向 baseDpr + 0.125
         *
         * 不是随机数，而是 make/model/分辨率 的稳定 hash。
         */
        private static int GetStableVariant(
            string? make,
            string? model,
            int physicalWidth,
            int physicalHeight)
        {
            string key =
                NormalizeKeyPart(make) + "|" +
                NormalizeKeyPart(model) + "|" +
                physicalWidth + "x" +
                physicalHeight;

            uint hash = StableHash32(key);

            // 权重设计：
            // 0~5  -> Base DPR，约 60%
            // 6~7  -> -0.125，约 20%
            // 8~9  -> +0.125，约 20%
            int bucket = (int)(hash % 10);

            if (bucket <= 5)
                return 0;

            if (bucket <= 7)
                return 1;

            return 2;
        }

        private static float[] BuildStableCandidates(
            float baseDpr,
            int stableVariant)
        {
            float lower = RoundDpr(baseDpr - DprStep);
            float center = RoundDpr(baseDpr);
            float upper = RoundDpr(baseDpr + DprStep);

            /*
             * 稳定种子只改变候选优先级，
             * 不是直接强行锁死结果。
             *
             * 如果首选 DPR 会产生明显不合理 CSS viewport，
             * matcher 仍然可以选择第二/第三候选。
             */
            return stableVariant switch
            {
                1 => new[] { lower, center, upper },
                2 => new[] { upper, center, lower },
                _ => new[] { center, lower, upper }
            };
        }

        private static DeviceProfileResult? BuildCandidate(
            int physicalWidth,
            int physicalHeight,
            float dpr,
            bool suspiciousInput,
            float baseDpr,
            int candidateOrder)
        {
            double rawCssWidth =
                physicalWidth / (double)dpr;

            double rawCssHeight =
                physicalHeight / (double)dpr;

            int cssWidth = (int)Math.Round(
                rawCssWidth,
                MidpointRounding.AwayFromZero);

            int cssHeight = (int)Math.Round(
                rawCssHeight,
                MidpointRounding.AwayFromZero);

            cssWidth =
                SnapCssWidthIfVeryClose(cssWidth);

            if (cssWidth < MinCssWidth ||
                cssWidth > MaxCssWidth ||
                cssHeight < MinCssHeight ||
                cssHeight > MaxCssHeight)
            {
                return null;
            }

            double cssRatio =
                (double)cssHeight / cssWidth;

            if (cssRatio < MinPhoneRatio - 0.05 ||
                cssRatio > MaxPhoneRatio + 0.05)
            {
                return null;
            }

            double dprX =
                (double)physicalWidth / cssWidth;

            double dprY =
                (double)physicalHeight / cssHeight;

            double score = 0;

            // X / Y 推导 DPR 应尽量一致。
            score +=
                Math.Abs(dprX - dprY) * 35.0;

            // 最终 CSS 反推结果应尽量接近候选 DPR。
            score +=
                Math.Abs(dprX - dpr) * 8.0;

            score +=
                Math.Abs(dprY - dpr) * 8.0;

            // 强调“档位稳定”，不要轻易离开当前 Base DPR。
            score +=
                Math.Abs(dpr - baseDpr) * 1.5;

            /*
             * candidateOrder 是 make/model 稳定种子决定的顺序。
             * 第一候选有轻微优势。
             *
             * 权重不能太大，否则会为了稳定而选择明显不合理 viewport。
             */
            score += candidateOrder * 0.12;

            // 常见 CSS width 只做非常弱的加权。
            score +=
                GetCssWidthPenalty(cssWidth);

            if (suspiciousInput)
                score += 25.0;

            return new DeviceProfileResult
            {
                PhysicalWidth = physicalWidth,
                PhysicalHeight = physicalHeight,

                CssWidth = cssWidth,
                CssHeight = cssHeight,

                DeviceScaleFactor = dpr,

                DprX = dprX,
                DprY = dprY,

                Score = score,

                IsSuspiciousInput = suspiciousInput,
                IsFallback = false
            };
        }

        private static int SnapCssWidthIfVeryClose(
            int cssWidth)
        {
            int nearest =
                CommonCssWidths
                    .OrderBy(x => Math.Abs(x - cssWidth))
                    .First();

            // 最多只允许 2px 微调。
            if (Math.Abs(nearest - cssWidth) <= 2)
                return nearest;

            return cssWidth;
        }

        private static double GetCssWidthPenalty(
            int cssWidth)
        {
            int nearestDiff =
                CommonCssWidths.Min(
                    x => Math.Abs(x - cssWidth));

            return nearestDiff * 0.002;
        }

        private static DeviceProfileResult BuildFallback(
            int physicalWidth,
            int physicalHeight,
            bool suspiciousInput,
            float baseDpr)
        {
            float dpr =
                Math.Clamp(
                    baseDpr,
                    MinDpr,
                    MaxDpr);

            int cssWidth =
                CalculateCss(physicalWidth, dpr);

            int cssHeight =
                CalculateCss(physicalHeight, dpr);

            while (cssWidth < MinCssWidth &&
                   dpr > MinDpr)
            {
                dpr =
                    RoundDpr(
                        Math.Max(
                            MinDpr,
                            dpr - DprStep));

                cssWidth =
                    CalculateCss(
                        physicalWidth,
                        dpr);

                cssHeight =
                    CalculateCss(
                        physicalHeight,
                        dpr);
            }

            while (cssWidth > MaxCssWidth &&
                   dpr < MaxDpr)
            {
                dpr =
                    RoundDpr(
                        Math.Min(
                            MaxDpr,
                            dpr + DprStep));

                cssWidth =
                    CalculateCss(
                        physicalWidth,
                        dpr);

                cssHeight =
                    CalculateCss(
                        physicalHeight,
                        dpr);
            }

            cssWidth =
                SnapCssWidthIfVeryClose(cssWidth);

            return new DeviceProfileResult
            {
                PhysicalWidth = physicalWidth,
                PhysicalHeight = physicalHeight,

                CssWidth = cssWidth,
                CssHeight = cssHeight,

                DeviceScaleFactor = dpr,

                DprX =
                    (double)physicalWidth / cssWidth,

                DprY =
                    (double)physicalHeight / cssHeight,

                Score = 9999.0,

                IsSuspiciousInput = suspiciousInput,
                IsFallback = true
            };
        }

        private static int CalculateCss(
            int physical,
            float dpr)
        {
            return (int)Math.Round(
                physical / (double)dpr,
                MidpointRounding.AwayFromZero);
        }

        private static float RoundDpr(float dpr)
        {
            return (float)Math.Round(
                dpr,
                3,
                MidpointRounding.AwayFromZero);
        }

        private static string NormalizeKeyPart(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Trim()
                .ToLowerInvariant();
        }

        /*
         * 固定 FNV-1a 32-bit。
         *
         * 不使用 string.GetHashCode()：
         * .NET 的 string hash 不应拿来做跨进程/跨运行稳定映射。
         */
        private static uint StableHash32(
            string value)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;

            byte[] bytes =
                Encoding.UTF8.GetBytes(value);

            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= prime;
            }

            return hash;
        }
    }
}
