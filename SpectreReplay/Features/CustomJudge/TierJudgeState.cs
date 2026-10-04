using System.Collections.Generic;

namespace SpectreReplay.Features.CustomJudge;

// 自定义判定的运行时账本：每档 早/晚/Auto/中旋 计数 + 判定历史（检查点回滚用）。
// 与游戏一致的生命周期：重开清零（Start_Rewind checkpointNum==0）、
// 打检查点、回检查点按历史逐条回退——回放模式由真实引擎驱动，
// 同一条记账管道自然生效。
// 档位数动态：计数桶随档位表自动扩缩（Resize 清零，增删档时由调用方 Reset）
internal static class TierJudgeState
{
    private static int[] Early = new int[0];
    private static int[] Late = new int[0];
    private static int[] Auto = new int[0];
    private static int[] Midspin = new int[0];
    // 历史条目编码：tier << 2 | 0=早 1=晚 2=Auto 3=中旋；-1 = 越档占位（回滚对齐用，不计数）
    private static readonly List<int> History = new(256);
    private static int _checkpointSize;

    // GetHitMargin 记录、飘字/AddHit 消费的"最近一次判定"（档索引；-1 = 无）
    internal static int LastTier = -1;
    internal static int LastTierForText = -1;
    internal static int LastSign; // -1 早 / +1 晚 / 0 Auto
    // 最近一次落账的档与符号（对外 API 读取用；上面三个是命中处理期的瞬态值，AddHit 后即清）
    internal static int CommittedTier = -1;
    internal static int CommittedKind; // -1 早 / 0 Auto / 1 晚 / 2 中旋

    private static void EnsureSize()
    {
        int n = Settings.CustomTierOn.Count;
        if (Early.Length != n)
        {
            Early = new int[n];
            Late = new int[n];
            Auto = new int[n];
            Midspin = new int[n];
            History.Clear();
            _checkpointSize = 0;
        }
    }

    internal static void Reset()
    {
        int n = Settings.CustomTierOn.Count;
        Early = new int[n];
        Late = new int[n];
        Auto = new int[n];
        Midspin = new int[n];
        History.Clear();
        _checkpointSize = 0;
        LastTier = -1;
        LastTierForText = -1;
        LastSign = 0;
        CommittedTier = -1;
        CommittedKind = 0;
    }

    internal static void MarkCheckpoint()
    {
        EnsureSize();
        _checkpointSize = History.Count;
    }

    internal static void RevertToCheckpoint()
    {
        EnsureSize();
        while (History.Count > _checkpointSize)
        {
            int entry = History[History.Count - 1];
            History.RemoveAt(History.Count - 1);
            if (entry < 0)
                continue;
            int tier = entry >> 2;
            if ((uint)tier >= Early.Length)
                continue;
            switch (entry & 3)
            {
                case 1: Late[tier]--; break;
                case 2: Auto[tier]--; break;
                case 3: Midspin[tier]--; break;
                default: Early[tier]--; break;
            }
        }
        // 最近一次落账随回滚失效：Committed* 指向的命中已被回退，不再对外可见
        CommittedTier = -1;
        CommittedKind = 0;
    }

    // 落账（scrMarginTracker.AddHit 权威事件里调用）：把最近一次判定计入统计。
    // sign：-1 早 / +1 晚 / 0 Auto（Auto 不计入正负任一侧）；
    // midspin=true 走独立中旋桶（真实玩家输入但无限判定窗口，无早迟语义）
    internal static void Commit(int tier, int sign, bool midspin = false)
    {
        EnsureSize();
        if ((uint)tier >= Early.Length)
        {
            CommittedTier = -1;
            CommittedKind = 0;
            History.Add(-1);
            return;
        }
        int code = midspin ? 3 : sign > 0 ? 1 : sign < 0 ? 0 : 2;
        CommittedTier = tier;
        CommittedKind = midspin ? 2 : (sign > 0 ? 1 : sign < 0 ? -1 : 0);
        History.Add((tier << 2) | code);
        switch (code)
        {
            case 1: Late[tier]++; break;
            case 2: Auto[tier]++; break;
            case 3: Midspin[tier]++; break;
            default: Early[tier]++; break;
        }
    }

    // 取值器一律越界回 0：账本只在 Commit/打点/回滚/加权时扩容（EnsureSize），
    // 配置刚加载、一次命中都没记时数组还是旧尺寸，结算页直接读会越界炸掉追加
    private static bool InRange(int tier) => (uint)tier < Early.Length;

    internal static int Count(int tier) => InRange(tier) ? Early[tier] + Late[tier] + Auto[tier] + Midspin[tier] : 0;

    internal static int EarlyCount(int tier) => InRange(tier) ? Early[tier] : 0;

    internal static int LateCount(int tier) => InRange(tier) ? Late[tier] : 0;

    internal static int AutoCount(int tier) => InRange(tier) ? Auto[tier] : 0;

    internal static int MidspinCount(int tier) => InRange(tier) ? Midspin[tier] : 0;

    // 自定义 xacc 分子：Σ(档计数 × 档权重)；越档命中不计（权重 0），
    // 停用档不计（与结算列表的过滤口径一致）
    internal static double WeightedSum()
    {
        EnsureSize();
        double sum = 0.0;
        for (int i = 0; i < Early.Length && i < Settings.CustomTierWeight.Count; i++)
        {
            if (i < Settings.CustomTierOn.Count && !Settings.CustomTierOn[i])
                continue;
            sum += Count(i) * (double)Settings.CustomTierWeight[i];
        }
        return sum;
    }
}
