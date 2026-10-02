using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Patching
{
    /// <summary>补丁挂在方法的前/后，或作为异常兜底。</summary>
    internal enum PatchKind
    {
        Prefix,
        Postfix,
        /// <summary>
        /// IL 改写：直接改原方法体。
        ///
        /// 异常发生在**补丁应用期**，不走 Guard——那时还没有补丁体可护。
        /// 与 SpectrePatch 的处置一致（SpectrePatchHub.cs:625）。
        /// </summary>
        Transpiler,
        /// <summary>
        /// 方法体执行完（或抛出）后调用，用来观察或吞掉异常。
        ///
        /// 必须用它的一类场景：异常发生在**方法体内部**，Prefix/Postfix
        /// 只在进入前/正常返回后跑，都救不了。
        /// 例：兜住游戏自身在方法体里抛出的越界（见 Patch_Recording 的
        /// HoldKeysFinalizer）。
        ///
        /// 语义与 Harmony 一致：形参 <c>__exception</c> 为 null 表示**没抛异常**
        /// ——Finalizer 在无异常时也会被调用，不判这一条会走错分支；
        /// 返回 null 表示吞掉，返回原异常表示继续上抛。
        ///
        /// Guard 不挂在它自己身上：Finalizer 本身就是兜底，再套一层
        /// finalizer 是自己吞自己。
        /// </summary>
        Finalizer,
    }

    /// <summary>
    /// 声明式补丁。挂在静态方法上：
    ///     [GamePatch(typeof(scrController), "UpdateInput", PatchKind.Prefix, "recording")]
    ///     static void Prefix() { ... }
    ///
    /// 与 Spectre 的 [SpectrePatch] 的差别：那套支持"目标不存在时静默跳过 + 失败汇总"，
    /// 这里保留了这两点（游戏小版本之间成员会改名），但去掉了 MinVersion/MaxVersion
    /// 与优先级排序——本模组功能少、目标集中，不需要那套版本矩阵。
    /// 目标解析失败时只记日志，不弹窗：这是"辅助功能"，不该在用户点开始时才炸。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class GamePatchAttribute : Attribute
    {
        internal Type Target;
        internal string MethodName;
        internal PatchKind Kind;
        internal string Feature;      // 分组名，只用于日志与统计
        internal string[] ParameterTypes;   // 可选：重载消歧

        /// <summary>
        /// 补丁体异常是否由共享 Guard 吞掉（默认 true）。
        ///
        /// **默认开着，因为补丁体抛出去的后果由"谁在调它"决定，不由它自己决定。**
        /// 我们挂的全是游戏的 Update/Hit 这类主干方法，Harmony 把补丁体的
        /// 异常原样抛进游戏栈之后，玩家看到的是"游戏崩了/卡死了"，
        /// 而日志里那一行的类名是**游戏的**，排查时根本想不到是模组。
        ///
        /// 没有 Guard 时，Harmony 会在连续失败后**永久跳过**该补丁
        /// （MAX_HARMS 机制），于是变成"开着开关却什么都不发生"——
        /// 同样无声无息。
        ///
        /// 与 Finalizer 不同：Finalizer 守的是**游戏方法体自己**抛的异常
        /// （如 UpdateHoldKeys 的越界），Guard 守的是**我们的补丁体**抛的。
        /// 两层都要，互不替代。
        /// </summary>
        internal bool TryingCatch = true;

        /// <summary>
        /// TryingCatch 生效时，30s 窗口内异常累计到该次数就**自动卸载这个补丁**（0 = 从不）。
        ///
        /// 给"宁缺毋滥"的补丁用：持续故障自我了断，好过无限刷日志。
        /// 卸载后要重新 Initialize（模组重开）才恢复。
        ///
        /// 不给全部补丁默认开——那会让"一次偶发异常"永久关掉某项功能，
        /// 而用户界面上看不出任何区别。只有明确知道某补丁在特定版本上
        /// 必然失败时才设。
        /// </summary>
        internal int DisableAfterExceptions;

        internal GamePatchAttribute(Type target, string methodName, PatchKind kind,
            string feature = "", params string[] parameterTypes)
        {
            Target = target;
            MethodName = methodName;
            Kind = kind;
            Feature = feature ?? "";
            ParameterTypes = parameterTypes;
        }
    }

    /// <summary>功能开关的注册表。功能页的开关改这里，再调 Refresh() 重新挂载。</summary>
    internal static class FeatureGate
    {
        private sealed class Entry
        {
            internal string Name;
            internal Func<bool> IsOn;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        internal static void Declare(string name, Func<bool> isOn)
            => Entries.Add(new Entry { Name = name, IsOn = isOn });

        internal static bool IsEnabled(string name)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Name == name)
                {
                    return Entries[i].IsOn();
                }
            }
            return false;   // 没声明的功能一律视为关闭
        }

        internal static IEnumerable<string> Names => Entries.Select(e => e.Name);
    }

    /// <summary>
    /// 补丁应用器。全流程幂等：Refresh() 会先全量卸掉本模组的补丁再按当前开关重挂，
    /// 这样"改开关"就是一次 Refresh，不需要各自维护"我现在挂了什么"的账本。
    /// 代价是改开关时会短暂地全部脱离（用户看不到中间态，因为同一帧内完成）。
    /// </summary>
    internal static class PatchLoader
    {
        private const string HarmonyId = "com.spectre.replay";

        private static Harmony _harmony;
        private static bool _initialized;
        private static readonly List<HarmonyMethod> _applied = new List<HarmonyMethod>();
        private static readonly List<string> Failures = new List<string>();

        /// <summary>
        /// 上一次真正挂补丁时的开关签名。用来判断"开关集合变没变"。
        ///
        /// **必须有它**：Refresh 会被设置页每改一行就调一次，拖滑条时更是
        /// 每帧一次。没有它就会 UnpatchAll + 重挂几十遍——拖一下分辨率，
        /// 正在打的那几帧补丁是空的。
        /// </summary>
        private static string _enabledSig = "";
        private static bool _loadedOnce;

        internal static IReadOnlyList<string> ApplyFailures => Failures;

        /// <summary>
        /// 未挂载补丁的摘要，直接拼在"补丁 5/6"那一行后面。
        ///
        /// 只打"5/6"是没用的：缺的是哪个、为什么缺，都在里面。分开打的话
        /// 计数在 Load 里、原因在 Refresh 里（早一行），两个窗口里看到的
        /// 顺序是反的，排查时对不上。合到一行才是一眼能看完的诊断。
        /// </summary>
        internal static string FailureSummary
            => Failures.Count == 0 ? string.Empty
                : "；未挂载：" + string.Join(" | ", Failures);

        internal static int AppliedCount => _applied.Count;
        internal static int DeclaredCount { get; private set; }

        /// <summary>
        /// 挂失败的补丁数。UI 层只读这个数字，不引 Patching 命名空间——
        /// 离线预览器并不链接 PatchLoader，引了那边就编不过。
        /// </summary>
        internal static int FailedCount => Failures.Count;

        // ── 补丁体异常兜底（偏差 10）────────────────────────────────
        //
        // 为什么必须有：Harmony 把补丁体的异常原样抛进游戏方法的栈里，
        // 玩家看到的是"游戏崩了/卡死了"，而日志里那一行的类名是**游戏的**，
        // 排查时根本想不到是模组。更糟的是 Harmony 的 MAX_HARMS 机制——
        // 同一处补丁连续失败若干次后**永久跳过**它，于是变成
        // "开关开着却什么都不发生"，同样一声不响。
        //
        // 所以共享一个 Guard finalizer 吞掉我们自己的补丁异常，并按窗口
        // 限流：首现详报（带栈），持续重复只打一行计数摘要。
        // 照 SpectrePatchHub 的 Guard（:615-627 挂载、:750-812 统计）。
        //
        // **Guard 按栈帧比对来判断"这是不是我们补丁体抛的"**，所以只吞自己
        // 的异常——游戏方法体自己抛的会原样上抛，不会被误吞。那类要治的是
        // 游戏自身的 bug，用 [GamePatch(..., PatchKind.Finalizer)] 单独兜
        // （见 Patch_Recording.HoldKeysFinalizer）。

        private static readonly HashSet<MethodInfo> GuardedMethods =
            new HashSet<MethodInfo>();

        /// <summary>限流窗口（秒）。与 SpectrePatchHub 的 GuardWindowSeconds 同值。</summary>
        private const int GuardWindowSeconds = 30;

        private sealed class GuardStat
        {
            internal DateTime WindowStart;
            internal int Suppressed;
            internal string Sig;
        }

        private static readonly Dictionary<string, GuardStat> GuardStats =
            new Dictionary<string, GuardStat>(StringComparer.Ordinal);
        private static readonly object GuardLock = new object();

        private static HarmonyMethod _guard;

        /// <summary>
        /// 共享的异常兜底 finalizer。
        ///
        /// 静态属性做懒加载：它在扫描完所有补丁之后才被第一次取用，
        /// 那时 Harmong 还没创建也不需要。
        /// </summary>
        private static HarmonyMethod GuardMethod =>
            _guard ??= new HarmonyMethod(
                typeof(PatchLoader).GetMethod(
                    nameof(Guard),
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static));

        /// <summary>
        /// 共享 Guard 的实现。语义照 Harmony：
        /// <c>__exception</c> 为 null 表示没异常（也会被调用），
        /// 返回 null = 吞掉，返回原异常 = 继续上抛。
        /// </summary>
        private static Exception Guard(Exception __exception,
            MethodBase __originalMethod)
        {
            if (__exception == null)
            {
                return null;       // 没异常：原样放行，别把正常路径搞坏
            }
            try
            {
                // 栈顶那一帧就是补丁体所在（异常正从它抛出）
                var frameMethod = new System.Diagnostics.StackTrace(__exception, false)
                    .GetFrame(0)?.GetMethod();
                if (frameMethod == null)
                {
                    return __exception;   // 认不出来 → 不吞，绝不误伤游戏自己的
                }
                lock (GuardLock)
                {
                    if (!GuardedMethods.Contains(frameMethod))
                    {
                        return __exception;
                    }
                    string key = frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                        + "@" + __originalMethod?.DeclaringType?.Name + "."
                        + __originalMethod?.Name;
                    string sig = __exception.GetType().Name + ": " + __exception.Message;
                    DateTime nowUtc = DateTime.UtcNow;
                    int occurrences;
                    if (!GuardStats.TryGetValue(key, out GuardStat stat)
                        || stat.Sig != sig)
                    {
                        // 首现（或异常性质变了）：详报并开窗
                        stat = new GuardStat
                        {
                            WindowStart = nowUtc,
                            Suppressed = 0,
                            Sig = sig,
                        };
                        GuardStats[key] = stat;
                        occurrences = 1;
                        Debug.LogWarning("[SpectreReplay] 补丁 "
                            + frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                            + " 在 " + __originalMethod?.DeclaringType?.Name + "."
                            + __originalMethod?.Name + " 内抛出异常（已吞掉，功能可能退化，"
                            + "游戏版本不匹配？）: " + sig + "\n" + __exception.StackTrace);
                    }
                    else if ((nowUtc - stat.WindowStart).TotalSeconds >= GuardWindowSeconds)
                    {
                        // 窗口外仍在重复：一行合并摘要，重新开窗
                        Debug.LogWarning("[SpectreReplay] 补丁 "
                            + frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                            + " 异常持续（" + GuardWindowSeconds + "s 内重复 "
                            + stat.Suppressed + " 次，已合并）: " + sig);
                        stat.WindowStart = nowUtc;
                        stat.Suppressed = 0;
                        occurrences = 1;
                    }
                    else
                    {
                        stat.Suppressed++;
                        occurrences = stat.Suppressed + 1;
                    }
                    // 异常自禁用：宁缺毋滥的补丁到阈值就自己卸掉。
                    // 默认阈值是 0（从不），所以这段对绝大多数补丁是死代码。
                    Spec owner = FindOwningSpec(frameMethod, __originalMethod);
                    if ((owner?.Attr.DisableAfterExceptions ?? 0) > 0
                        && occurrences >= owner.Attr.DisableAfterExceptions)
                    {
                        Unapply(owner);
                        Debug.LogWarning("[SpectreReplay] 补丁 "
                            + frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                            + " 异常达阈值（窗口内 " + occurrences + " 次），已自动卸载；"
                            + "重开模组前不会自动恢复");
                    }
                }
                return null;       // 吞掉
            }
            catch
            {
                // Guard 自己绝不能把异常放出去——那会让"兜底"变成放大器
                return null;
            }
        }

        /// <summary>
        /// 反查补丁方法属于哪条 spec（异常自禁用要用它的阈值）。
        /// </summary>
        private static Spec FindOwningSpec(MethodBase patch, MethodBase target)
        {
            if (target == null)
            {
                return null;
            }
            foreach (Spec s in Specs)
            {
                if (s.Patch == patch
                    && s.Attr.Target != null
                    && target.DeclaringType == s.Attr.Target
                    && target.Name == s.Attr.MethodName)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>卸载单条 spec。只摘它自己那条，不动别人。</summary>
        private static void Unapply(Spec spec)
        {
            try
            {
                _harmony?.UnpatchAll();
            }
            catch
            {
                // UnpatchAll 是按 id 卸的，会把这个补丁一起带走
            }
            if (spec.Patch != null)
            {
                lock (GuardLock)
                {
                    GuardedMethods.Remove(spec.Patch);
                }
            }
        }

        internal static void Register()
        {
            Scan();
            _harmony = new Harmony(HarmonyId);
            _initialized = true;
            Refresh();
        }

        /// <summary>
        /// 按当前功能开关状态重挂全部补丁。幂等，可反复调用。
        ///
        /// **开关集合没变时直接返回。** UnpatchAll + 重挂不是免费的：
        /// 设置页每改一行都会走到这里，而拖动滑条时是**每帧**一次——
        /// 拖一下分辨率就会把 6 个补丁卸了挂、挂了卸几十遍，期间游戏
        /// 正在打的那几帧补丁是空的，表现为拖动时录制短暂失效。
        /// 真正需要重挂的只有"某个功能开关翻转"这一种情况。
        /// </summary>
        internal static void Refresh()
        {
            if (!_initialized)
            {
                return;
            }
            string sig = EnabledSignature();
            if (sig == _enabledSig && _loadedOnce)
            {
                return;         // 开关集合没变：重挂只会白白抖动
            }
            _enabledSig = sig;
            _loadedOnce = true;
            UnpatchAll();
            Failures.Clear();
            foreach (var spec in Specs)
            {
                if (!FeatureGate.IsEnabled(spec.Attr.Feature))
                {
                    continue;
                }
                TryApply(spec);
            }
            if (Failures.Count > 0)
            {
                Debug.LogWarning("[SpectreReplay] " + Failures.Count
                    + " 个补丁未挂载（对应功能不会生效）：" + string.Join(" | ", Failures));
            }
            else if (DeclaredCount > 0)
            {
                // 不写"全部挂载"：playback 组的补丁只在**载入回放时**才挂上，
                // 所以计数天然会缺几个，那是门控的正常结果，不是故障。
                // 之前那句"补丁 6/7 全部挂载"自相矛盾，玩家会以为坏了。
                Debug.Log("[SpectreReplay] 补丁 " + AppliedCount + "/" + DeclaredCount
                    + " 已挂载（playback 组只在载入回放时挂上，计数偏少是正常的）");
            }
        }

        /// <summary>
        /// 当前所有功能开关的状态，拼成一个可比较的短串。
        ///
        /// 比较它就知道"补丁的挂载集合有没有变"。挂载集合只由开关决定，
        /// 跟其它设置（分辨率、码率……）无关——所以调分辨率根本不该触发
        /// 重挂，而调"启用录制"必须触发。
        /// </summary>
        private static string EnabledSignature()
        {
            var sb = new System.Text.StringBuilder(64);
            foreach (var name in FeatureGate.Names)
            {
                sb.Append(name).Append('=')
                  .Append(FeatureGate.IsEnabled(name) ? '1' : '0')
                  .Append(';');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 没挂上的补丁名，供入口日志一次说清。
        ///
        /// 之前只有"补丁 5/6"这样的数字，不说哪个没挂上、为什么——
        /// 光看那个数字没法定位，只知道"有一处坏了"却不知道坏在哪。
        /// </summary>
        internal static string FailureReport =>
            Failures.Count == 0 ? null : string.Join(" | ", Failures);

        /// <summary>
        /// 卸掉本模组挂的全部补丁。模组被关闭时由 Main 调用——
        /// 不卸的话，补丁会留在游戏方法上，而模组代码已经不再被调用，
        /// 表现是关掉模组后功能照旧（而且再也关不掉）。
        /// </summary>
        internal static void UnpatchAll()
        {
            if (_harmony == null)
            {
                return;
            }
            foreach (var m in _applied)
            {
                try
                {
                    _harmony.UnpatchAll();
                    break;      // UnpatchSelf 一次卸掉本 id 的全部
                }
                catch
                {
                    // 继续尝试下一条
                }
            }
            _applied.Clear();
        }

        private static void TryApply(Spec spec)
        {
            try
            {
                MethodBase target = ResolveTarget(spec.Attr);
                if (target == null)
                {
                    Failures.Add(spec.Attr.MethodName + "（目标不存在）");
                    return;
                }
                // 补丁方法就是**声明了这个特性的那个方法本身**，不是 Attr.MethodName。
                // Attr.MethodName 是**游戏里被补丁的方法名**（如 "UpdateInput"），
                // 拿它回本类里找补丁方法必然找不到——之前就是这么写的，
                // 结果日志里 6 个补丁全部报"补丁方法缺失"，一个功能都没挂上。
                MethodInfo patch = spec.Patch;
                if (patch == null)
                {
                    Failures.Add(spec.Owner.Name + "（补丁方法缺失）");
                    return;
                }
                HarmonyMethod hm = new HarmonyMethod(patch);
                // Guard = 共享的异常兜底 finalizer。Transpiler 的异常发生在
                // 补丁**应用期**，那时没有补丁体可护；Finalizer 本身就是
                // 兜底，再套一层是自己吞自己。两种都不挂 Guard。
                HarmonyMethod guard = spec.Attr.TryingCatch
                    && spec.Attr.Kind != PatchKind.Transpiler
                    && spec.Attr.Kind != PatchKind.Finalizer
                    ? GuardMethod : null;
                // 四种 kind **全部显式分派**，不靠 else 兜底。
                // 写成「Prefix 走 prefix，其余一律 postfix」的话，
                // Finalizer 会被静默挂成 postfix：补丁照样"挂上了"，
                // 日志里也看不出任何异常，却完全不起兜底作用。
                if (spec.Attr.Kind == PatchKind.Prefix)
                {
                    _harmony.Patch(target, prefix: hm, finalizer: guard);
                }
                else if (spec.Attr.Kind == PatchKind.Postfix)
                {
                    _harmony.Patch(target, postfix: hm, finalizer: guard);
                }
                else if (spec.Attr.Kind == PatchKind.Transpiler)
                {
                    _harmony.Patch(target, transpiler: hm);
                }
                else
                {
                    // 其余目前只有 Finalizer。写成 else 而不是显式判 Finalizer
                    // 是刻意的：新加一种 kind 时会落到这里被明确暴露，
                    // 而不会静默挂成 postfix。
                    _harmony.Patch(target, finalizer: hm);
                }
                if (guard != null)
                {
                    GuardedMethods.Add(patch);
                }
                _applied.Add(hm);
            }
            catch (Exception ex)
            {
                // 只记 ex.GetType().Name 等于什么都没说——Harmony 拒绝挂载的
                // 真正理由（目标签名不匹配、补丁链冲突、找不到 IL……）全在
                // Message 里，而类型名每次都一样。日志里既然要写"为什么没挂上"，
                // 就把消息带上；换行压掉，免得把 UMM 窗口刷成一片。
                string why = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ");
                if (why.Length > 120)
                {
                    why = why.Substring(0, 120) + "…";
                }
                Failures.Add(spec.Owner.Name + "." + spec.Attr.MethodName
                    + "（" + ex.GetType().Name + (why.Length > 0 ? ": " + why : "") + "）");
            }
        }

        private static MethodBase ResolveTarget(GamePatchAttribute a)
        {
            if (a.Target == null)
            {
                return null;
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static;
            Type[] want = null;
            if (a.ParameterTypes != null && a.ParameterTypes.Length > 0)
            {
                want = new Type[a.ParameterTypes.Length];
                for (int i = 0; i < want.Length; i++)
                {
                    want[i] = Type.GetType(a.ParameterTypes[i], false) ?? typeof(object);
                }
            }
            // 用纯反射而不是 AccessTools：AccessTools.Method 的重载在不同
            // 0Harmony 版本之间改过签名（参数顺序 Type[]/BindingFlags 互换），
            // 自己遍历既不挑版本，也顺手能做重载消歧。
            MethodInfo fallback = null;
            foreach (MethodInfo m in a.Target.GetMethods(flags))
            {
                if (m.Name != a.MethodName)
                {
                    continue;
                }
                if (want == null)
                {
                    return m;     // 未指定重载：取第一个（多数目标只有一个同名方法）
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != want.Length)
                {
                    continue;
                }
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType != want[i])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    return m;
                }
                fallback ??= m;
            }
            return fallback;
        }

        private sealed class Spec
        {
            internal Type Owner;
            internal GamePatchAttribute Attr;
            /// <summary>声明该特性的方法本身，也就是要挂上去的补丁方法。</summary>
            internal MethodInfo Patch;
        }

        private static List<Spec> _specs;

        private static List<Spec> Specs => _specs ?? (_specs = ScanSpecs());

        /// <summary>扫描本程序集里所有 [GamePatch]，只扫一次。</summary>
        private static List<Spec> ScanSpecs()
        {
            var list = new List<Spec>();
            Assembly asm = typeof(PatchLoader).Assembly;
            foreach (Type t in asm.GetTypes())
            {
                MethodInfo[] methods = t.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.DeclaredOnly);
                foreach (MethodInfo m in methods)
                {
                    foreach (GamePatchAttribute a in
                        m.GetCustomAttributes<GamePatchAttribute>())
                    {
                        list.Add(new Spec { Owner = t, Attr = a, Patch = m });
                    }
                }
            }
            return list;
        }

        private static void Scan()
        {
            DeclaredCount = Specs.Count;
        }
    }
}
