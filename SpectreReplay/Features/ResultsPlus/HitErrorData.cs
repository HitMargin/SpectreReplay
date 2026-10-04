using System;
using System.Collections.Generic;
using System.Linq;
using SpectreReplay.Features.Compat;
using UnityEngine;

namespace SpectreReplay.Features.ResultsPlus;

// 判定误差记录器：以 scrMarginTracker.hitMargins 为权威序列，一一对应地记录每次判定的
// 毫秒误差（游戏只存档位不存误差值）。
// 对齐策略：每条记录带 TrackerIndex（AddHit 后的 hitMargins.Count），当 count 因检查点
// 回退 / R 键重开而变小时裁掉超出部分 —— 无需 hook 任何回退路径。
internal static class HitErrorData
{
    internal readonly struct Entry
    {
        public readonly int TrackerIndex;
        public readonly HitMargin Margin;
        public readonly float? Ms; // 无角度意义的判定（auto、midspin 无限判定等）为 null
        public Entry(int trackerIndex, HitMargin margin, float? ms)
        {
            TrackerIndex = trackerIndex;
            Margin = margin;
            Ms = ms;
        }
    }

    internal static readonly List<Entry> Entries = new();
    private const int MaxEntries = 10000; // 长时间练习不结算时的防泄漏上限

    /// <summary>
    /// 新一局开始时清空（换关 / 重开 / 回到主菜单后进新关）。
    ///
    /// 必须显式清：<see cref="Record"/> 的裁尾逻辑以 TrackerIndex（hitMargins 的序号）
    /// 识别"回退"，而换局后序号从 1 重新开始 —— 裁尾会把上一局裁到只剩 TrackerIndex==1
    /// 的那一条，**恰好留下上一局的第一个命中**。实测新局的偏移统计会混入上一个样本，
    /// 污染早/晚方向、均值与 σ；<c>CalibrationAdvisor</c> 读同一份数据，校准建议同样被污染。
    /// </summary>
    internal static void ResetForNewRun()
    {
        Entries.Clear();
        hasPending = false;
    }

    // SwitchChosen Prefix 缓存、AddHit Postfix 消费（只对真实按键判定有角度意义）
    private static double pendingAngleDiffRad; // 已乘 isCW 方向，正 = 晚按
    private static double pendingBpmTimesSpeed;
    private static double pendingPitch;
    private static bool hasPending;

    // 必须在 SwitchChosen 开头缓存：函数内部会更新 targetExitAngle / speed 等判定参数
    internal static void CapturePending(scrPlanet planet)
    {
        scrConductor conductor = scrConductor.instance;
        if (planet == null || planet.planetarySystem == null || conductor == null)
        {
            hasPending = false;
            return;
        }
        double dir = planet.planetarySystem.isCW ? 1.0 : -1.0;
        pendingAngleDiffRad = (planet.cachedAngle - planet.targetExitAngle) * dir;
        pendingBpmTimesSpeed = conductor.bpm * planet.planetarySystem.speed;
        pendingPitch = conductor.song != null ? conductor.song.pitch : 1.0;
        hasPending = true;
    }

    // AddHit 的 Postfix 调用（tracker 即 __instance）
    internal static void Record(scrMarginTracker tracker, HitMargin margin)
    {
        int count = tracker.hitMargins.Count; // AddHit 已执行，含本次
        // 回退/重开使 count 变小时先裁尾（R 键 Reset 清零 → 全清）
        while (Entries.Count > 0 && Entries[Entries.Count - 1].TrackerIndex > count)
            Entries.RemoveAt(Entries.Count - 1);
        if (count < Entries.Count)
            Entries.Clear(); // 罕见的极端回退，保守放弃历史

        float? ms = null;
        scrController controller = scrController.instance;
        scrPlayer player = controller != null ? controller.playerOne : null;
        if (hasPending
            && player != null && !player.auto && !player.midspinInfiniteMargin
            && Math.Abs(pendingBpmTimesSpeed) > 1e-6)
        {
            // TimeToAngleInRad 的逆运算：angle = t * pitch * π * bpmSpeed / 60
            double v = pendingAngleDiffRad * 60.0
                / (Math.PI * pendingPitch * pendingBpmTimesSpeed) * 1000.0;
            if (!double.IsNaN(v) && !double.IsInfinity(v))
                ms = (float)v;
        }
        hasPending = false;
        Entries.Add(new Entry(count, margin, ms));
        // 超限裁掉最旧的一半而非整体清零：长练习局触顶时统计保留近期历史。
        // TrackerIndex 存的是权威序列的绝对序号，裁头不错位，上方回退裁尾逻辑不受影响
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries / 2);
    }

    // 参与偏移统计的档位：VeryEarly..VeryLate 五档。
    // 旧版枚举连续可直接比大小，r150 起枚举重排（XPerfect=4/PerfectPlus=5 插进区间内），
    // 数值区间会把它们误收进来，必须改成按名集合判定
    // TooEarly/TooLate 超窗与 Fail* 档不代表系统偏移；Auto 与 auto 模式的伪 Perfect在记录时已置 null
    internal static bool IsValidSample(Entry e) =>
        e.Ms.HasValue && HitMarginCompat.IsTierCounted(e.Margin);

    internal readonly struct Stats
    {
        public readonly int Count;
        public readonly float Median;
        public readonly float Mean;
        public readonly float Sigma;
        public Stats(int count, float median, float mean, float sigma)
        {
            Count = count; Median = median; Mean = mean; Sigma = sigma;
        }
    }

    internal static Stats ComputeStats()
    {
        var samples = Entries.Where(IsValidSample).Select(e => e.Ms.Value).ToList();
        if (samples.Count == 0)
            return new Stats(0, 0f, 0f, 0f);
        var sorted = samples.OrderBy(x => x).ToList();
        float median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) * 0.5f;
        float mean = samples.Average();
        float sigma = Mathf.Sqrt(samples.Average(x => (x - mean) * (x - mean)));
        return new Stats(samples.Count, median, mean, sigma);
    }

    // 最大全 P 连击：权威序列上连续 EarlyPerfect/Perfect/LatePerfect/Auto 的最长段
    // （逐版本按名解析：r150 的 Perfect 槽由 PerfectMinus 承接，且刻意不含 Multipress）
    internal static int MaxCombo(List<HitMargin> margins)
    {
        if (margins == null)
            return 0;
        int best = 0, cur = 0;
        foreach (HitMargin m in margins)
        {
            cur = HitMarginCompat.IsComboPerfect(m) ? cur + 1 : 0;
            if (cur > best)
                best = cur;
        }
        return best;
    }
}
