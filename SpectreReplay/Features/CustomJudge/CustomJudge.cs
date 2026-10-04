using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SpectreReplay.Features.CustomJudge;

// 自定义判定核心：档位区间计算、按角度分档、默认档案与 JSON 导入导出。
// 每档是一个显式环形区间 [起始角, 结束角]，角度在标准判定条坐标（Counted=60°）下定义：
// 写定角度即条上位置，不随判定窗口漂移、不乘 60/counted 归一系数（乘了会把整张
// 角度表向中心压缩，档位全跑错位）；毫秒下限按真实时间定位（t(ms)×60/counted
// 换算到条坐标，随 BPM×变速×音调移动），结束角取两者较大；外沿写 60° 的档触边钉死。
// 档位按序级联铺放（内沿不低于上一档结束，毫秒扩张把后续档往外推）；
// 结束角也可设为跟随模式（游戏 Pure/Perfect/Counted 窗口实时边界）；
// 档位数量完全动态；区间允许留空，未覆盖处由判定条底层游戏配色兜底。
internal static class CustomJudge
{
    internal const double DegPerRad = 57.29577951308232;

    // 结束角"跟随"模式哨兵：档位边界 = 游戏对应窗口的实时值（条坐标），
    // 默认档案用它们复刻原版三档——静态角度表在任何难度滑条/速度试炼下都无法
    // 精确等于原版动态窗口（写死 [30,45] 会把 Perfect 随 BPM 的扩张整个盖住）
    internal const float OuterFollowPure = -1f;
    internal const float OuterFollowPerfect = -2f;
    internal const float OuterFollowCounted = -3f;

    // 起始角"接上一档"哨兵：内沿 = 上一档的结束角（动态）。
    // 窗口收缩到写定起始角之下时，静态起始角会让档位悬空成空区间
    //（标准模式高 BPM：Perfect 收到 27.7° < 写定 30°，档3 判不出任何命中）
    internal const float InnerFollowPrev = -1f;

    internal static int Count => Settings.CustomTierOn.Count;

    // 原版窗口边界的本地复刻（scrMisc.GetAdjustedAngleBoundaryInDeg 同式）。
    // 不直接调游戏函数：它被真实判定改写 Postfix 挂着，档位哨兵再用改写值解析
    // 会自引用/递归；这里永远取原版语义——"跟随"的含义就是跟随游戏本来的窗口
    internal static double BoundaryRaw(HitMarginGeneral marginType,
        double bpmTimesSpeed, double conductorPitch, double marginMult)
    {
        float num = GCS.difficulty == Difficulty.Lenient ? 0.091f
            : GCS.difficulty == Difficulty.Strict ? 0.04f : 0.065f;
        bool isMobile = ADOBase.isMobile;
        // 除数钳到小正数：谱面 bpm=0（手改谱面或外部工具导入）时 x/0 = Infinity，
        // 而下面三行 Mathf.Max(x, 0.025f) 拦不住 Infinity（实测 Max(∞, 0.025) = ∞），
        // 于是判定窗口变成无穷大 —— 每次命中都算最高档，判定条直接失去意义。
        // 钳除数而非钳结果：合法速度下结果逐位不变（除以 1 还是原值），
        // 且三处除法一次保护。游戏本体 scrLoader 也有同样的无保护除法，
        // 但那只会影响它的提示时长，这里影响的是判定本身。
        float speedTrial = Mathf.Max(GCS.currentSpeedTrial, 0.01f);
        num = isMobile ? 0.09f : num / speedTrial;
        float a = isMobile ? 0.07f : 0.03f / speedTrial;
        float a2 = isMobile ? 0.05f : 0.02f / speedTrial;
        num = Mathf.Max(num, 0.025f);
        a = Mathf.Max(a, 0.025f);
        a2 = Mathf.Max(a2, 0.025f);
        // 惰性求值：原来无条件算三个 TimeToAngleInRad，而下面的 switch 每个分支
        // 只用到其中一个——每次调用白算两个。这是"真实判定改写"最热的路径：
        // Patch_RealJudgment.Apply 每次判定调 BoundaryDeg 三次、每次最多两次
        // BoundaryRaw，即每次判定 18 次 TimeToAngleInRad，其中 12 次是废的。
        // TimeToAngleInRad 是纯函数（只依赖入参，无副作用），按分支只算需要的那
        // 一个，结果逐位相同。
        switch (marginType)
        {
            case HitMarginGeneral.Perfect:
                return Math.Max(45.0 * marginMult,
                    scrMisc.TimeToAngleInRad(a, bpmTimesSpeed, conductorPitch) * DegPerRad);
            case HitMarginGeneral.Pure:
                return Math.Max(30.0 * marginMult,
                    scrMisc.TimeToAngleInRad(a2, bpmTimesSpeed, conductorPitch) * DegPerRad);
            default:    // Counted 及其余一律落到 Counted 语义
                return Math.Max((double)GCS.HITMARGIN_COUNTED * marginMult,
                    scrMisc.TimeToAngleInRad(num, bpmTimesSpeed, conductorPitch) * DegPerRad);
        }
    }

    // 哨兵 → 游戏窗口类型（其他负值按 Counted 兜底）
    private static HitMarginGeneral FollowTarget(float outerDeg) => (int)outerDeg switch
    {
        -1 => HitMarginGeneral.Pure,
        -2 => HitMarginGeneral.Perfect,
        _ => HitMarginGeneral.Counted
    };

    // XPerfect 同款默认档案：内档 = X完美（0~15°，16.67ms 下限），
    // 外三档内沿接上一档、结束角跟随游戏 Pure/Perfect/Counted 窗口——
    // 任何难度/速度试炼/BPM 下都精确贴合原版三段（无空隙、无悬空、扩张收缩全程动画），
    // xacc 权重复刻原版（0.75/0.4/0.2）
    internal static void ApplyDefaults()
    {
        Settings.CustomTierOn = new List<bool> { true, true, true, true };
        Settings.CustomTierInner = new List<float> { 0f, InnerFollowPrev, InnerFollowPrev, InnerFollowPrev };
        Settings.CustomTierOuter = new List<float> { 15f, OuterFollowPure, OuterFollowPerfect, OuterFollowCounted };
        Settings.CustomTierMs = new List<float> { 16.67f, 0f, 0f, 0f };
        Settings.CustomTierName = new List<string> { "", "", "", "" };
        Settings.CustomTierPrefix = new List<string> { "X", "", "", "" };
        Settings.CustomTierColor = new List<Color>
        {
            new(0.3f, 0.8f, 1f, 1f),
            new(0.37255f, 1f, 0.30588f, 1f),
            new(0.98824f, 1f, 0.30196f, 1f),
            new(1f, 0.43529f, 0.30196f, 1f)
        };
        Settings.CustomTierWeight = new List<float> { 1f, 0.75f, 0.4f, 0.2f };
        Settings.CustomTierSplit = new List<bool> { false, false, false, false };
        Settings.CustomTierHide = new List<bool> { false, false, false, false };
        // 默认全不描边（alpha 0 = 关）；用户可逐档在设置页开
        Settings.CustomTierOutlineColor = new List<Color> { default, default, default, default };
        Settings.CustomTierTickOutlineColor = new List<Color> { default, default, default, default };
        Settings.CustomRealJudgeOn = false;
        Settings.CustomRealPure = 0;
        Settings.CustomRealPerfect = 2;
        Settings.CustomRealCounted = 3;
        Settings.CustomXaccOn = false;
        Settings.CustomOnlyOn = false;
        TierJudgeState.Reset();
    }

    // 追加一档（追加在最外）：起始角继承上一档结束角（写定值或哨兵原样传），
    // 结束角 = 写定 +15°；上一档结束角是跟随哨兵时改随 Counted（到边）——
    // 拿负哨兵 +15° 会得到 12~14° 的零宽隐形档（"加档没反应"）
    internal static void AddTier()
    {
        int n = Settings.CustomTierOn.Count;
        float inner = n > 0 ? Settings.CustomTierOuter[n - 1] : 0f;
        Settings.CustomTierOn.Add(true);
        Settings.CustomTierInner.Add(inner);
        Settings.CustomTierOuter.Add(inner < 0f ? OuterFollowCounted : Mathf.Min(inner + 15f, 90f));
        Settings.CustomTierMs.Add(0f);
        Settings.CustomTierName.Add("");
        Settings.CustomTierPrefix.Add("");
        Settings.CustomTierColor.Add(Color.white);
        Settings.CustomTierWeight.Add(1f);
        Settings.CustomTierSplit.Add(false);
        Settings.CustomTierHide.Add(false);
        Settings.CustomTierOutlineColor.Add(default); // 透明 = 不描边
        Settings.CustomTierTickOutlineColor.Add(default);
    }

    // 删除第 i 档（十个平行 List 同步删）；真实判定映射索引越界时回夹
    internal static void RemoveTier(int i)
    {
        if ((uint)i >= Count)
            return;
        Settings.CustomTierOn.RemoveAt(i);
        Settings.CustomTierInner.RemoveAt(i);
        Settings.CustomTierOuter.RemoveAt(i);
        Settings.CustomTierMs.RemoveAt(i);
        Settings.CustomTierName.RemoveAt(i);
        Settings.CustomTierPrefix.RemoveAt(i);
        Settings.CustomTierColor.RemoveAt(i);
        Settings.CustomTierWeight.RemoveAt(i);
        Settings.CustomTierSplit.RemoveAt(i);
        Settings.CustomTierHide.RemoveAt(i);
        if (i < Settings.CustomTierOutlineColor.Count)
            Settings.CustomTierOutlineColor.RemoveAt(i);
        if (i < Settings.CustomTierTickOutlineColor.Count)
            Settings.CustomTierTickOutlineColor.RemoveAt(i);
        int last = Mathf.Max(Count - 1, 0);
        Settings.CustomRealPure = Mathf.Clamp(Settings.CustomRealPure, 0, last);
        Settings.CustomRealPerfect = Mathf.Clamp(Settings.CustomRealPerfect, 0, last);
        Settings.CustomRealCounted = Mathf.Clamp(Settings.CustomRealCounted, 0, last);
        TierJudgeState.Reset();
    }

    // 原始角度域的区间（真实判定改写用：GetAdjustedAngleBoundaryInDeg 是 raw 语义，
    // 毫秒项按 raw 换算、角度项即写定值 × marginScale）
    internal static (float Inner, float Outer) TierRangeDeg(int tier,
        double bpmTimesSpeed, double conductorPitch, double marginScale)
    {
        if ((uint)tier >= Count || !Settings.CustomTierOn[tier])
            return (0f, 0f);
        float inner = Settings.CustomTierInner[tier] > 0f
            ? Settings.CustomTierInner[tier] * (float)marginScale : 0f;
        float outerDeg = Settings.CustomTierOuter[tier];
        double outer;
        if (outerDeg < 0f)
            outer = BoundaryRaw(FollowTarget(outerDeg), bpmTimesSpeed, conductorPitch, marginScale); // 跟随 = 原版 raw 边界
        else
            outer = outerDeg > 0f ? outerDeg * marginScale : 0.0;
        float ms = Settings.CustomTierMs[tier];
        if (ms > 0f)
            outer = Math.Max(outer, scrMisc.TimeToAngleInRad(ms / 1000.0, bpmTimesSpeed, conductorPitch) * DegPerRad);
        if (outer <= inner)
            return (0f, 0f);
        return (inner, (float)outer);
    }

    // 判定条坐标域的区间（分类、条形、tick 配色统一用它）——级联铺排模型：
    //   档位按序铺放，内沿 = max(写定起始×mm, 上一档结束)，结束 = max(写定结束×mm, 毫秒换算, 内沿)。
    // 写定角度就是条上位置，不乘 60/counted——乘归一系数会把整张角度表向中心压缩
    //（用户写 [15,30] 就该显示在 15~30；判定窗口被拉大时 k<1，档位全跑错位）。
    // 毫秒项是真实时间，按 t(ms)×k 换算到条坐标：下限原样生效（XP 的 16.67ms
    // 在任何设置下都是真实大小），后续档被向外推而不是被吞掉，空间不够时零宽消失。
    // 结束角可设跟随模式（哨兵负值）：结束 = 游戏对应窗口实时边界，替代写定角度项；
    // 外沿写 60° 触边钉死。
    internal static (float Inner, float Outer) TierRangeBar(int tier,
        double bpmTimesSpeed, double conductorPitch, double marginScale, double countedRaw,
        bool staticZones = false)
    {
        EnsureAllRanges(bpmTimesSpeed, conductorPitch, marginScale, countedRaw, staticZones);
        return CachedTierRangeIn(staticZones, tier);
    }

    private static (float Inner, float Outer) CachedTierRangeIn(bool s, int tier)
    {
        var inArr = InArr(s);
        if ((uint)tier >= Count || tier >= inArr.Length)
            return (0f, 0f);
        return (inArr[tier], OutArr(s)[tier]);
    }

    // ── 批量区间：把级联前缀从 O(n²) 降到每帧一次 O(n) ──────────────
    // TierRangeBar(t) 内部是 for (j = 0; j <= t; j++) 从头累加，所以查第 t 档要重算
    // 前 t 档。而热路径上：
    //   TryTierColor        对 0..n-1 每档各调一次 → 1+2+…+n 次内层迭代
    //   ApplyTierTickOutline 自己又跑一遍同样的循环 → 再来一轮
    // 两者都是**每帧每个 tick**，屏幕上 20 个 tick × 8 档 ≈ 每帧 1400+ 次内层迭代，
    // 且每次都可能调 BoundaryRaw / TimeToAngleInRad。这是"开自定义判定打判定掉帧"
    // 的主因。
    //
    // 改成：参数与设置都没变时，一次算完所有档位写进数组，之后都是查表。
    // 算术与原实现逐行一致，只是把"重复的前缀累加"变成"算一次"——数值不变。
    // 按 staticZones 拆成两份：判定条用 staticZones=!DynamicOn，tick 着色用 false。
    // 两者在同一帧内交替调用，共用一份缓存会互相冲掉——等于没缓存。拆开后各自命中。
    private static readonly float[] _barInner = new float[64];
    private static readonly float[] _barOuter = new float[64];
    private static readonly float[] _sBarInner = new float[64];
    private static readonly float[] _sBarOuter = new float[64];
    private static bool _rangesValid, _sRangesValid;
    private static double _rbBpm, _rbPitch, _rbScale, _rbCounted;
    private static double _sbBpm, _sbPitch, _sbScale, _sbCounted;
    private static int _rbSettingsSig, _sbSettingsSig;

    private static float[] InArr(bool s) => s ? _sBarInner : _barInner;
    private static float[] OutArr(bool s) => s ? _sBarOuter : _barOuter;

    /// <summary>设置指纹。用户增删档位或改写定值/毫秒后签名变化 → 缓存失效重算。
    /// 只做纯数组读取（无反射、无游戏调用），远便宜于重算整条级联。</summary>
    private static int SettingsSignature()
    {
        int sig = Settings.CustomTierOn.Count * 397;
        for (int i = 0; i < Settings.CustomTierOn.Count; i++)
        {
            if (i >= _barInner.Length) break;
            sig = unchecked(sig * 31 + (Settings.CustomTierOn[i] ? 1 : 0));
            sig = unchecked(sig * 31 + Settings.CustomTierInner[i].GetHashCode());
            sig = unchecked(sig * 31 + Settings.CustomTierOuter[i].GetHashCode());
            sig = unchecked(sig * 31 + Settings.CustomTierMs[i].GetHashCode());
        }
        return sig;
    }

    /// <summary>热路径批量取全档区间：算一次、填满 _barInner/_barOuter，之后自己查数组。
    /// 供每帧每 tick 都跑的调用方使用（TryTierColor / ApplyTierTickOutline），
    /// 避免它们在档位循环里反复触发 EnsureAllRanges 的设置指纹计算。</summary>
    internal static void EnsureRangesForFrame(double bpmTimesSpeed, double conductorPitch,
        double marginScale, double countedRaw, bool staticZones = false)
        => EnsureAllRanges(bpmTimesSpeed, conductorPitch, marginScale, countedRaw, staticZones);

    /// <summary>已由 EnsureRangesForFrame 填好的第 tier 档区间（无再计算）。</summary>
    internal static (float Inner, float Outer) CachedTierRange(int tier)
        => CachedTierRangeIn(false, tier);

    /// <summary>staticZones 变体，供判定条窗口构建使用。</summary>
    internal static (float Inner, float Outer) CachedTierRangeStatic(int tier)
        => CachedTierRangeIn(true, tier);

    private static void EnsureAllRanges(double bpmTimesSpeed, double conductorPitch,
        double marginScale, double countedRaw, bool staticZones)
    {
        int sig = SettingsSignature();
        if (staticZones)
        {
            if (_sRangesValid && sig == _sbSettingsSig
                && bpmTimesSpeed == _sbBpm && conductorPitch == _sbPitch
                && marginScale == _sbScale && countedRaw == _sbCounted)
                return;
        }
        else if (_rangesValid && sig == _rbSettingsSig
            && bpmTimesSpeed == _rbBpm && conductorPitch == _rbPitch
            && marginScale == _rbScale && countedRaw == _rbCounted)
        {
            return;
        }

        var inArr = InArr(staticZones);
        var outArr = OutArr(staticZones);
        double k = countedRaw > 0.0 ? 60.0 / countedRaw : 1.0;
        float prevOuter = 0f;
        int n = Count;
        if (n > inArr.Length) n = inArr.Length;
        for (int j = 0; j < n; j++)
        {
            if (!Settings.CustomTierOn[j])
            {
                inArr[j] = 0f;
                outArr[j] = 0f;
                continue;
            }
            float inner = Settings.CustomTierInner[j] < 0f
                ? prevOuter
                : Mathf.Max(Settings.CustomTierInner[j] * (float)marginScale, prevOuter);
            float outerDeg = Settings.CustomTierOuter[j];
            double outer = outerDeg < 0f
                ? (staticZones
                    ? (double)StaticFollowDeg(FollowTarget(outerDeg)) * k
                    : BoundaryRaw(FollowTarget(outerDeg), bpmTimesSpeed, conductorPitch, marginScale) * k)
                : outerDeg > 0f ? outerDeg * marginScale : 0.0;
            float ms = Settings.CustomTierMs[j];
            if (ms > 0f)
                outer = Math.Max(outer,
                    scrMisc.TimeToAngleInRad(ms / 1000.0, bpmTimesSpeed, conductorPitch)
                        * DegPerRad * k);
            float o = Mathf.Max(Mathf.Clamp((float)outer, 0f, 60f), inner);
            if (outerDeg >= 60f)
                o = 60f;
            inArr[j] = inner;
            outArr[j] = o;
            prevOuter = o; // 无效档不占位
        }

        if (staticZones)
        {
            _sbBpm = bpmTimesSpeed; _sbPitch = conductorPitch;
            _sbScale = marginScale; _sbCounted = countedRaw;
            _sbSettingsSig = sig; _sRangesValid = true;
        }
        else
        {
            _rbBpm = bpmTimesSpeed; _rbPitch = conductorPitch;
            _rbScale = marginScale; _rbCounted = countedRaw;
            _rbSettingsSig = sig; _rangesValid = true;
        }
    }

    // 静态显示模式下跟随档的锚点：原版布局角，与底层三段的静态值严格一致
    private static float StaticFollowDeg(HitMarginGeneral g) => g switch
    {
        HitMarginGeneral.Pure => 30f,
        HitMarginGeneral.Perfect => 45f,
        _ => 60f
    };

    // |Δ角度|（raw）→ 档索引：先归一化到条坐标再比对区间；无档覆盖 = -1
    internal static int Classify(float absDeltaDeg, double bpmTimesSpeed, double conductorPitch,
        double marginScale, double countedRaw)
    {
        if (countedRaw <= 0.0)
            return -1;
        float bar = absDeltaDeg * (float)(60.0 / countedRaw);
        // 一次算全档、循环内查表。这里是**每次命中**都会走的分类入口
        // （GetHitMargin 一次命中可能被调多次），原来逐档调 TierRangeBar，
        // 每次都会触发 EnsureAllRanges 的设置指纹扫描（O(n) 数组读）→ 每命中 O(n²)。
        EnsureRangesForFrame(bpmTimesSpeed, conductorPitch, marginScale, countedRaw);
        for (int i = 0; i < Count; i++)
        {
            var (inner, outer) = CachedTierRange(i);
            if (outer > 0f && bar >= inner && bar <= outer)
                return i;
        }
        return -1;
    }

    // 最内档索引（Only 模式的死亡线；Auto 判定也归它）
    internal static int InnermostTier()
    {
        for (int i = 0; i < Count; i++)
            if (Settings.CustomTierOn[i] && EffectiveBoundary(i))
                return i;
        return -1;
    }

    // 不依赖运行时上下文的粗略有效性（区间配置合法）
    internal static bool EffectiveBoundary(int tier)
    {
        if ((uint)tier >= Count || !Settings.CustomTierOn[tier])
            return false;
        float inner = Settings.CustomTierInner[tier];
        float outer = Settings.CustomTierOuter[tier];
        float ms = Settings.CustomTierMs[tier];
        bool hasOuter = outer > 0f || outer < 0f || ms > 0f; // 负值 = 跟随模式，恒有效
        return hasOuter && (outer <= 0f || outer > inner);
    }

    // 统计/飘字显示名：自定义名 > 前缀 > "档 i"
    internal static string DisplayName(int tier)
    {
        if ((uint)tier >= Count) return "";
        string name = Settings.CustomTierName[tier];
        if (!string.IsNullOrEmpty(name)) return name;
        string prefix = Settings.CustomTierPrefix[tier];
        if (!string.IsNullOrEmpty(prefix)) return prefix;
        return string.Format(LocalizationManager.GetLocalizedText("customjudge.tier"), tier + 1);
    }

    // ── 档案 JSON：mod 目录 judgments/*.json，可分享 / 一键切换 ──

    private class ProfileData
    {
        public bool[] on { get; set; }
        public float[] inner { get; set; }
        public float[] outer { get; set; }
        public float[] ms { get; set; }
        public string[] name { get; set; }
        public string[] prefix { get; set; }
        public string[] color { get; set; }
        public float[] weight { get; set; }
        public bool[] split { get; set; }
        public bool[] hide { get; set; }
        public string[] outline_color { get; set; }
        public string[] tick_outline_color { get; set; }
        public float outline_width { get; set; } = 3f;
        public bool realjudge { get; set; }
        public int real_pure { get; set; }
        public int real_perfect { get; set; }
        public int real_counted { get; set; }
        public bool xacc { get; set; }
        public bool only { get; set; }
    }

    internal static string ProfileDir => Path.Combine(Main.mod.Path, "judgments");

    internal static bool SaveProfile(string name)
    {
        try
        {
            int n = Count;
            var color = new string[n];
            var outlineColor = new string[n];
            var tickOutlineColor = new string[n];
            for (int i = 0; i < n; i++)
            {
                color[i] = "#" + ColorUtility.ToHtmlStringRGBA(Settings.CustomTierColor[i]);
                outlineColor[i] = "#" + ColorUtility.ToHtmlStringRGBA(
                    i < Settings.CustomTierOutlineColor.Count ? Settings.CustomTierOutlineColor[i] : default);
                tickOutlineColor[i] = "#" + ColorUtility.ToHtmlStringRGBA(
                    i < Settings.CustomTierTickOutlineColor.Count ? Settings.CustomTierTickOutlineColor[i] : default);
            }
            var data = new ProfileData
            {
                on = Settings.CustomTierOn.ToArray(),
                inner = Settings.CustomTierInner.ToArray(),
                outer = Settings.CustomTierOuter.ToArray(),
                ms = Settings.CustomTierMs.ToArray(),
                name = Settings.CustomTierName.ToArray(),
                prefix = Settings.CustomTierPrefix.ToArray(),
                color = color,
                weight = Settings.CustomTierWeight.ToArray(),
                split = Settings.CustomTierSplit.ToArray(),
                hide = Settings.CustomTierHide.ToArray(),
                outline_color = outlineColor,
                tick_outline_color = tickOutlineColor,
                outline_width = Settings.CustomTierOutlineWidth,
                realjudge = Settings.CustomRealJudgeOn,
                real_pure = Settings.CustomRealPure,
                real_perfect = Settings.CustomRealPerfect,
                real_counted = Settings.CustomRealCounted,
                xacc = Settings.CustomXaccOn,
                only = Settings.CustomOnlyOn
            };
            Directory.CreateDirectory(ProfileDir);
            string file = Path.Combine(ProfileDir, Sanitize(name) + ".json");
            File.WriteAllText(file, JsonConvert.SerializeObject(data, Formatting.Indented));
            return true;
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] custom judge profile save failed: " + ex.Message);
            return false;
        }
    }

    internal static bool LoadProfile(string name)
    {
        try
        {
            string file = Path.Combine(ProfileDir, Sanitize(name) + ".json");
            if (!File.Exists(file))
                return false;
            var data = JsonConvert.DeserializeObject<ProfileData>(File.ReadAllText(file));
            if (data?.on == null || data.on.Length == 0)
                return false;
            // 档数以 on 为准（其余列表缺项用中性默认补齐）；
            // 旧档案无 inner（累计边界模型）→ inner[0]=0、inner[i]=outer[i-1] 平滑迁移
            int n = data.on.Length;
            Settings.CustomTierOn = new List<bool>(data.on);
            Settings.CustomTierOuter = FitFloat(data.outer, n);
            Settings.CustomTierMs = FitFloat(data.ms, n);
            Settings.CustomTierInner = new List<float>(n);
            for (int i = 0; i < n; i++)
            {
                if (data.inner != null && i < data.inner.Length)
                    Settings.CustomTierInner.Add(data.inner[i]);
                else
                    Settings.CustomTierInner.Add(i > 0 ? Settings.CustomTierOuter[i - 1] : 0f);
            }
            Settings.CustomTierName = FitString(data.name, n);
            Settings.CustomTierPrefix = FitString(data.prefix, n);
            Settings.CustomTierWeight = FitFloat(data.weight, n, 1f);
            Settings.CustomTierSplit = FitBool(data.split, n);
            Settings.CustomTierHide = FitBool(data.hide, n);
            Settings.CustomTierColor = new List<Color>();
            for (int i = 0; i < n; i++)
                Settings.CustomTierColor.Add(Color.white);
            if (data.color != null)
                for (int i = 0; i < n && i < data.color.Length; i++)
                    if (ColorUtility.TryParseHtmlString(
                            data.color[i].StartsWith("#") ? data.color[i] : "#" + data.color[i], out Color c))
                        Settings.CustomTierColor[i] = c;
            // 描边色：旧档案无此键 → 全部透明（不描边），与"新增字段默认关闭"一致
            Settings.CustomTierOutlineColor = FitColor(data.outline_color, n);
            Settings.CustomTierTickOutlineColor = FitColor(data.tick_outline_color, n);
            if (data.outline_width > 0f)
                Settings.CustomTierOutlineWidth = Mathf.Clamp(data.outline_width, 0.5f, 12f);
            int last = n - 1;
            Settings.CustomRealJudgeOn = data.realjudge;
            Settings.CustomRealPure = Mathf.Clamp(data.real_pure, 0, last);
            Settings.CustomRealPerfect = Mathf.Clamp(data.real_perfect, 0, last);
            Settings.CustomRealCounted = Mathf.Clamp(data.real_counted, 0, last);
            Settings.CustomXaccOn = data.xacc;
            Settings.CustomOnlyOn = data.only;
            TierJudgeState.Reset();
            PatchManager.RefreshPatches();
            return true;
        }
        catch (Exception ex)
        {
            Debug.Log("[Spectre] custom judge profile load failed: " + ex.Message);
            return false;
        }
    }

    private static List<float> FitFloat(float[] src, int n, float fill = 0f)
    {
        var list = new List<float>(n);
        for (int i = 0; i < n; i++)
            list.Add(src != null && i < src.Length ? src[i] : fill);
        return list;
    }

    private static List<string> FitString(string[] src, int n)
    {
        var list = new List<string>(n);
        for (int i = 0; i < n; i++)
            list.Add(src != null && i < src.Length ? src[i] ?? "" : "");
        return list;
    }

    private static List<bool> FitBool(bool[] src, int n)
    {
        var list = new List<bool>(n);
        for (int i = 0; i < n; i++)
            list.Add(src != null && i < src.Length ? src[i] : false);
        return list;
    }

    // 描边色数组：缺失/短项一律补全透明（alpha 0 = 不描边）
    private static List<Color> FitColor(string[] src, int n)
    {
        var list = new List<Color>(n);
        for (int i = 0; i < n; i++)
        {
            Color c = default;
            if (src != null && i < src.Length && !string.IsNullOrEmpty(src[i])
                && ColorUtility.TryParseHtmlString(
                    src[i].StartsWith("#") ? src[i] : "#" + src[i], out Color parsed))
                c = parsed;
            list.Add(c);
        }
        return list;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "default" : name.Trim();
    }
}
