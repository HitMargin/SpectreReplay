using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SpectreReplay.Features.ResultsPlus;

// 校准建议：把多局的有效判定误差做滚动中位数，给出对 scrConductor.currentPreset.inputOffset
// 的修正量。方向与游戏自带校准器一致（scnCalibration 把 averageTimeOffset 同号直写 inputOffset）：
// 整体晚按（正 ms）→ 建议增大 inputOffset。currentPreset 是实时读取的属性，改完立即生效。
internal static class CalibrationAdvisor
{
    internal const int MaxSamples = 500;   // 滚动窗口容量
    internal const int MinSamples = 40;    // 样本门槛
    internal const float MaxSigmaMs = 25f; // 高于此值说明误差不稳定（手抖而非延迟）

    internal static readonly List<float> Samples = new();
    internal static int Games { get; private set; }
    private static int lastCommitTrackerCount = -1; // 同一次结算重复触发只计一次

    /// <summary>
    /// 新一局开始时调用：重置"本次结算"去重哨兵。
    ///
    /// 不重置会出问题：去重只比较 <c>trackerMargins.Count</c>，而反复练习同一关时
    /// **每局命中数必然相同**（打完就结算，命中数不会变），于是第 2 局起被判成
    /// "同一次结算的重复触发"而静默丢弃 —— 校准建议只累积了第 1 局的数据，
    /// 用户再怎么练，建议值都不再更新。实测练 5 局只计入 1 局。
    ///
    /// 同一局内结算被反复触发（结算界面被再次打开/刷新）仍然会被去重 —— 局内
    /// 命中数不变，哨兵不会被其他路径改写。
    /// </summary>
    internal static void OnNewRun() => lastCommitTrackerCount = -1;

    // 结算时调用（无论通关/失败）：并入本局有效样本
    internal static void CommitRun(List<HitMargin> trackerMargins)
    {
        if (trackerMargins == null || trackerMargins.Count == 0)
            return;
        if (trackerMargins.Count == lastCommitTrackerCount)
            return;
        lastCommitTrackerCount = trackerMargins.Count;
        foreach (HitErrorData.Entry e in HitErrorData.Entries)
        {
            if (!HitErrorData.IsValidSample(e))
                continue;
            Samples.Add(e.Ms.Value);
        }
        // 超容量一次裁尾：循环里逐条 RemoveAt(0) 每次都整体左移，长序列下是 O(n×容量)
        if (Samples.Count > MaxSamples)
            Samples.RemoveRange(0, Samples.Count - MaxSamples);
        Games++;
        SaveState();
    }

    // 建议值（毫秒，正 = 校准值应增大）；不满足样本量/稳定性门槛时为 null。
    // 不设最小偏移门槛：再小的稳定偏移也如实提示（用户要求）
    internal static float? Advice()
    {
        if (Samples.Count < MinSamples)
            return null;
        var s = Compute();
        if (s.sigma > MaxSigmaMs)
            return null;
        return s.median;
    }

    internal static (int count, float median, float mean, float sigma) Compute()
    {
        if (Samples.Count == 0)
            return (0, 0f, 0f, 0f);
        var sorted = Samples.OrderBy(x => x).ToList();
        float median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) * 0.5f;
        float mean = Samples.Average();
        float sigma = Mathf.Sqrt(Samples.Average(x => (x - mean) * (x - mean)));
        return (Samples.Count, median, mean, sigma);
    }

    /// <summary>
    /// 应用建议的偏移量。返回 false 表示**没有做任何改动**（无建议，或建议值不足 1 毫秒）。
    ///
    /// 不足 1 毫秒必须走这条失败路径、且**不能清样本**：CalibrationPreset.inputOffset 是
    /// 整型毫秒（游戏内部 calibration_i = inputOffset / 1000f），所以 0.4ms 这类稳定偏移
    /// 取整后就是 0。若照旧返回成功并清空滚动窗口，用户会看到"应用成功"却毫无变化，
    /// 而且要重新攒够 MinSamples 才能再看到建议 —— 白白丢掉整段积累。
    /// </summary>
    internal static bool Apply(out int deltaMs)
    {
        float? advice = Advice();
        if (!advice.HasValue)
        {
            deltaMs = 0;
            return false;
        }
        deltaMs = Mathf.RoundToInt(advice.Value);
        if (deltaMs == 0)
        {
            // 偏移量不足 1 毫秒，游戏侧无从表达（整型毫秒），保持原值与样本
            return false;
        }
        scrConductor.currentPreset.inputOffset += deltaMs;
        // 与游戏校准 UI 一致的持久化路径（改 preset 后必须显式落盘，否则重启丢失）
        scrConductor.SaveCurrentPreset();
        Persistence.WriteSaveToDisk();
        // 建议已采纳：清空样本重新积累，避免同一建议被重复应用
        Clear();
        return true;
    }

    internal static void Clear()
    {
        Samples.Clear();
        Games = 0;
        lastCommitTrackerCount = -1;
        SaveState();
    }

    // ── 持久化：Settings 字段为载体，由 ConfigManager 统一落盘 ──

    internal static void SaveState()
    {
        Settings.CalibSamples = string.Join(";",
            Samples.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));
        Settings.CalibGames = Games;
        if (ConfigManager.configPath != null)
            ConfigManager.SaveConfigs(ConfigManager.configPath);
    }

    internal static void LoadState()
    {
        Samples.Clear();
        if (!string.IsNullOrEmpty(Settings.CalibSamples))
        {
            foreach (string part in Settings.CalibSamples.Split(';'))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                    && !float.IsNaN(v) && !float.IsInfinity(v))
                    Samples.Add(v);
            }
            if (Samples.Count > MaxSamples)
                Samples.RemoveRange(0, Samples.Count - MaxSamples);
        }
        Games = Settings.CalibGames;
        lastCommitTrackerCount = -1;
    }
}
