// 命中散点图的纯数学判据（2026-10-18，用户拍板 B 方案后加的）。
//
// 这块出过两个「画出来看着正常、但信息是错的」缺陷，两者都不会报错、也不会让
// 页面消失，只让读图的人得出错误结论——正是最需要判据钉住的那一类：
//   ① 抽稀每格只取第一个样本：1499 击 / 512 像素 = step 3，**三分之二不进图**。
//      统计里写着范围 -166.4ms ~ +67.2ms，而图上找不到那根 -166ms 的柱子。
//   ② 着色 `abs <= pwin ? 绿 : abs <= qwin ? 黄 : 红` —— Perfect 窗口恒宽于
//      Pure，所以 `abs > pwin` 必然推出 `abs > qwin`，**黄色分支永远不可达**。
//      整张图只剩绿和红，于是「偏移 160 多还全是绿的」。
using System;
using SpectreReplay.Ui;

namespace UiPreview
{
    internal static class HitScatterMathCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 命中散点图数学 ──");
            int fail = 0;

            // ① 三档必须都可达。这条直接钉「黄色是死代码」那个缺陷：
            //    给定紧/宽两档，三个区间各取一个点，必须得到三个不同档位。
            HitScatterMath.Windows(45f, 30f, out float tight, out float wide);
            if (!(tight < wide))
            {
                Console.WriteLine("    ✗ ① 窗口紧/宽关系算错：tight=" + tight + " wide=" + wide);
                fail++;
            }
            else
            {
                var b1 = HitScatterMath.Classify(tight * 0.5f, tight, wide);
                var b2 = HitScatterMath.Classify((tight + wide) * 0.5f, tight, wide);
                var b3 = HitScatterMath.Classify(wide * 2f, tight, wide);
                bool ok = b1 == HitScatterMath.Band.Best
                        && b2 == HitScatterMath.Band.Middle
                        && b3 == HitScatterMath.Band.Worst;
                if (!ok)
                {
                    Console.WriteLine("    ✗ ① 三档不可达（中间那档是死代码）: " + b1 + "/" + b2 + "/" + b3);
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ① 三档都可达（紧窗口内 / 只进宽窗口 / 都没进）");
                }
            }

            // ② 抽稀必须取该格 |ms| 最大的那个，不能取第一个。
            //    构造一格数据，把极值放在**非首位**——取第一个的实现会漏掉它。
            var samples = new float[] { 1f, 2f, -166.4f, 3f };
            int pick = HitScatterMath.PickExtreme(samples, 0, samples.Length);
            if (pick != 2)
            {
                Console.WriteLine("    ✗ ② 抽稀没取到极值：pick=" + pick + "（应为 2，值 -166.4）");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ② 抽稀取该格 |ms| 最大（极值不在首位也能画出来）");
            }

            // ③ 柱高与颜色必须同源：|ms| 越大，柱高不得变小；越出紧窗口必须变红。
            //    这条钉「两把尺子」——原实现柱高按固定 ±150ms、颜色按逐击窗口。
            HitScatterMath.Windows(45f, 30f, out float t2, out float w2);
            int hSmall = HitScatterMath.BarHeight(10f, t2, 60, 2f);
            int hBig = HitScatterMath.BarHeight(40f, t2, 60, 2f);
            var bandBig = HitScatterMath.Classify(40f, t2, w2);
            if (hBig < hSmall)
            {
                Console.WriteLine("    ✗ ③ 柱高与偏差不同向：10ms→" + hSmall + "px，40ms→" + hBig + "px");
                fail++;
            }
            else if (bandBig == HitScatterMath.Band.Best)
            {
                Console.WriteLine("    ✗ ③ 越出紧窗口却仍算最好档（颜色与高度不同源）");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ③ 柱高与颜色同源（越大越高、越界不再算最好档）");
            }

            // ④ 柱高永不为 0：0 高的柱子在图上等于不存在，而「偏差极小」与
            //    「这一击没画」在玩家眼里是同一件事。
            int hZero = HitScatterMath.BarHeight(0f, t2, 60, 2f);
            if (hZero < 1)
            {
                Console.WriteLine("    ✗ ④ 零偏差的柱子高度为 0（会被当成没画）");
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ④ 零偏差仍有 1px 柱（不会被误认为这一击缺失）");
            }

            // ⑤ 缺窗口数据时整对退回兜底值，不能只补一半（那会让宽窄关系失真）。
            HitScatterMath.Windows(0f, 30f, out float t3, out float w3);
            if (t3 != HitScatterMath.FallbackTightMs || w3 != HitScatterMath.FallbackWideMs)
            {
                Console.WriteLine("    ✗ ⑤ 缺一半窗口数据时没退回兜底整对: " + t3 + "/" + w3);
                fail++;
            }
            else
            {
                Console.WriteLine("    ✓ ⑤ 缺窗口数据时整对退回兜底（25ms/60ms）");
            }

            return fail;
        }
    }
}
