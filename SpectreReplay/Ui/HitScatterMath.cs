using System;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 命中散点图的**纯数学部分**：抽稀取样、尺度归一化、着色分档。
    ///
    /// <para><b>为什么把它从 <see cref="HitScatter"/> 里拆出来。</b>
    /// <c>HitScatter</c> 依赖 <c>Texture2D</c>，离屏套件跑不了它——于是那里的
    /// 逻辑（抽稀、着色）**一条判据都钉不住**。而它恰好出过两个真缺陷：
    /// ① 抽稀每段只取第一个，三分之二的样本不进图（统计写着 -166ms，图上找不到）；
    /// ② 黄色分支永远不可达（Perfect 窗口恒宽于 Pure，`abs &gt; pwin` 必然
    ///    推出 `abs &gt; qwin`）。两者都是「画出来看着正常、但信息是错的」。
    /// 把数学搬到这里之后，UiPreview 可以链接这个文件、用真源码打真判据。</para>
    ///
    /// <para><b>着色分档的口径</b>：<paramref name="absMs"/> 与该击的两个
    /// 窗口比——落进较紧的那个算「最好」，落进较宽的那个算「中等」，
    /// 都没进算「失误」。**用 min/max 而不是写死谁是紧的**：窗口公式在移动端
    /// 与速度试炼下各有分支，谁宽谁窄不该由这里再赌一次。</para>
    /// </summary>
    internal static class HitScatterMath
    {
        /// <summary>着色档位（与贴图上的三种语义色一一对应）。</summary>
        internal enum Band
        {
            /// <summary>落在较紧的窗口内（通常是 Perfect）。</summary>
            Best = 0,
            /// <summary>落在较宽的窗口内、但超出较紧的那个（通常是 Pure）。</summary>
            Middle = 1,
            /// <summary>两个窗口都没进。</summary>
            Worst = 2,
        }

        /// <summary>拿不到窗口数据时的兜底估算值（原版常识：25ms / 60ms）。</summary>
        internal const float FallbackTightMs = 25f;
        internal const float FallbackWideMs = 60f;

        /// <summary>
        /// 归一化到「较紧窗口的倍数」。1.0 = 刚好卡在边界。
        /// </summary>
        internal static float Ratio(float absMs, float tightMs)
        {
            if (tightMs <= 0f) tightMs = FallbackTightMs;
            return absMs / tightMs;
        }

        /// <summary>
        /// 柱高（像素）。满量程 = <paramref name="fullScale"/> 倍紧窗口。
        /// 永不为 0——0 高的柱子在图上等于不存在，而「偏差极小」和「这一击没画」
        /// 在玩家眼里是同一件事。
        /// </summary>
        internal static int BarHeight(float absMs, float tightMs, int halfHeight, float fullScale)
        {
            if (halfHeight <= 0) return 0;
            if (fullScale <= 0f) fullScale = 2f;
            float r = Ratio(absMs, tightMs) / fullScale;
            if (r < 0f) r = 0f;
            if (r > 1f) r = 1f;
            int h = (int)(halfHeight * r);
            return h < 1 ? 1 : h;
        }

        /// <summary>
        /// 两档窗口的紧/宽。**Perfect 恒宽于 Pure**（45° vs 30° 上限），
        /// 但这里按数值取 min/max，不依赖那个顺序假设。
        /// 任一为非正（缺数据）就整对退回兜底值——只补一半会让宽窄关系失真。
        /// </summary>
        internal static void Windows(float perfectMs, float pureMs,
            out float tightMs, out float wideMs)
        {
            if (perfectMs > 0f && pureMs > 0f)
            {
                tightMs = Math.Min(perfectMs, pureMs);
                wideMs = Math.Max(perfectMs, pureMs);
                return;
            }
            tightMs = FallbackTightMs;
            wideMs = FallbackWideMs;
        }

        /// <summary>
        /// 该偏差属于哪一档。**这三档必须都可达**——原实现里「中等」这一支
        /// 是死代码，整张图只剩绿和红，于是「偏移 160ms 还全是绿的」。
        /// </summary>
        internal static Band Classify(float absMs, float tightMs, float wideMs)
        {
            if (absMs <= tightMs) return Band.Best;
            if (absMs <= wideMs) return Band.Middle;
            return Band.Worst;
        }

        /// <summary>
        /// 一格里该画的样本下标：**取该格内 |ms| 最大的那个**。
        ///
        /// <para>抽稀的任务是「别把异常藏起来」，取第一个/取平均都正好相反。
        /// 原实现取第一个 ⇒ 1499 击只画 500 根，统计里的 -166.4ms 恰好不在
        /// 所在格的首位，图上就找不到它。</para>
        ///
        /// <returns>下标；<paramref name="from"/> 越界时返回 -1。</returns>
        /// </summary>
        internal static int PickExtreme(float[] samples, int from, int to)
        {
            if (samples == null || from < 0 || from >= samples.Length) return -1;
            if (to > samples.Length) to = samples.Length;
            if (to <= from) return -1;
            int best = from;
            float bestAbs = Math.Abs(samples[from]);
            for (int i = from + 1; i < to; i++)
            {
                float a = Math.Abs(samples[i]);
                if (a > bestAbs)
                {
                    bestAbs = a;
                    best = i;
                }
            }
            return best;
        }
    }
}
