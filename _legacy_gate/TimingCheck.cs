// 命中误差换算的验证（照 Spectre 的 ReplayAnalyzer）。
//
// 这里链接**真实的** HitTimingAnalyzer.cs——不是复刻。
// 复刻的算法和被测算法写错时会一起错，断言全绿而实现是坏的。
// 那个文件是纯计算、不碰引擎，所以链接得进来。
//
// 为什么值得单独立一条：那个逆运算式里有 **π 与 60 的换算**，
// 两处弄反都会得到一个"看起来合理但完全错误"的毫秒数——
// 而错误的毫秒数不会报错，只会让玩家以为自己偏手 20ms。
using System;
using System.Collections.Generic;
using SpectreReplay.Features.Playback;

namespace UiPreview
{
    internal static class TimingCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 命中误差换算 ──");
            int fail = 0;
            fail += ZeroAngleIsZeroMs();
            fail += SignMeansEarlyLate();
            fail += KnownFixture();
            fail += DegToMsConsistency();
            fail += SkipsAreCountedNotHidden();
            return fail;
        }

        /// <summary>角差为 0 → 误差为 0 ms（正中命中的定义）。</summary>
        private static int ZeroAngleIsZeroMs()
        {
            double ms = HitTimingAnalyzer.MsFromAngle(0.0, 120.0, 1.0);
            bool ok = ms == 0.0;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ① 角差 0 → 0 ms  → 实得 {ms:0.###}");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 符号方向：**正 = 晚按**、负 = 偏早。
        ///
        /// 这个符号要是反了，整个统计就会给玩家相反的结论
        /// （"你总是按早了"其实是一律按晚了），而且完全看不出来。
        /// </summary>
        private static int SignMeansEarlyLate()
        {
            // 正角差（转过了）→ 晚按
            double late = HitTimingAnalyzer.MsFromAngle(0.05, 120.0, 1.0);
            double early = HitTimingAnalyzer.MsFromAngle(-0.05, 120.0, 1.0);
            bool ok = late > 0 && early < 0 && Math.Abs(late + early) < 1e-9;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ② 正角差=晚按、负角差=偏早"
                + $"  → +0.05 rad → {late:0.###} ms，-0.05 rad → {early:0.###} ms");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 手算基准：0.05 rad、bpm×speed=120、pitch=1。
        ///
        ///     0.05 × 60 / (π × 1 × 120) × 1000 = 3000 / (120π) ≈ 7.9577 ms
        ///
        /// 这条钉住换算式里的 **π 与 60** 两处。任一处写错
        /// （用 180 代替 π、漏掉 1000）都会得到量级完全不同但"看着像回事"的数。
        /// </summary>
        private static int KnownFixture()
        {
            const double rad = 0.05, bts = 120.0, pitch = 1.0;
            double expect = rad * 60.0 / (Math.PI * pitch * bts) * 1000.0;
            double got = HitTimingAnalyzer.MsFromAngle(rad, bts, pitch);
            bool ok = Math.Abs(got - expect) < 1e-9;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ③ 手算基准"
                + $"  → 期望 {expect:0.####} ms，实得 {got:0.####} ms");
            if (!ok)
            {
                Console.WriteLine("      ← π 或 60/1000 弄反了：算出的毫秒数"
                    + "仍是有限的数，不会报错，但玩家会看到完全错误的结论");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 角度→毫秒 与 度→毫秒 必须共用同一个比例系数。
        ///
        /// Spectre 在 ReplayAnalyzer.cs:103-104 踩过这个坑：窗口宽度是从
        /// `BoundaryRaw` 拿到的**度**，要转回毫秒才能和误差同轴，
        /// 漏了 `π/180` 会被放大 180/π ≈ 57 倍——散点图全部误绿。
        ///
        /// 我们暂时不算窗口（没有自定义判定），但比例系数必须是对的，
        /// 否则将来接上窗口时错误会以同样的形式重演。判据：把同一个角
        /// 先换成度再换回来，毫秒数必须一致。
        /// </summary>
        private static int DegToMsConsistency()
        {
            const double bts = 120.0, pitch = 1.0;
            double rad = 0.05;
            double deg = rad * 180.0 / Math.PI;
            double direct = HitTimingAnalyzer.MsFromAngle(rad, bts, pitch);
            double viaDeg = HitTimingAnalyzer.MsFromAngle(deg * Math.PI / 180.0, bts, pitch);
            bool ok = Math.Abs(direct - viaDeg) < 1e-9;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ④ 角度↔弧度往返一致"
                + $"  → 直接 {direct:0.####} ms，经度换算 {viaDeg:0.####} ms");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 被跳过的命中要**计入总数**、不计入统计，不能悄悄消失。
        ///
        /// 跳过规则照 Spectre（:84-98）：自动命中、midspin 无限判定、
        /// bpm×speed 趋零、结果非有限。
        ///
        /// 这里把「被跳过」做成显式计数：玩家看到"共 100 击，统计了 82 击"
        /// 时知道有 18 击不适用；看不到的话会以为分析漏了数据，
        /// 或者更糟——以为那 18 击误差是 0。
        /// </summary>
        private static int SkipsAreCountedNotHidden()
        {
            // 第 0、2 号是 bpm×speed = 0（趋零），应被跳过
            var angles = new List<double> { 0.05, 0.0, -0.05, 0.10, -0.10 };
            var bts = new List<int> { 120, 0, 120, 120, 120 };
            var rep = HitTimingAnalyzer.Analyze(angles, bts, 1.0);
            bool ok = rep.TotalHits == 5 && rep.AnalyzedHits == 4
                && rep.EarlyCount == 2 && rep.LateCount == 2
                && rep.SamplesMs != null && rep.SamplesMs.Count == 4;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ⑤ 跳过的命中计入总数、不进统计"
                + $"  → 共 {rep.TotalHits} 击，统计 {rep.AnalyzedHits} 击"
                + $"（早 {rep.EarlyCount} / 晚 {rep.LateCount}）");
            if (!ok)
            {
                Console.WriteLine("      ← 跳过规则失效：那几击会以 NaN 或"
                    + "天大的毫秒值混进统计，玩家看到的结论毫无意义");
            }
            return ok ? 0 : 1;
        }
    }
}