using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Patching
{
    /// <summary>补丁挂在方法的前/后。</summary>
    internal enum PatchKind
    {
        Prefix,
        Postfix,
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

        internal static void Register()
        {
            Scan();
            _harmony = new Harmony(HarmonyId);
            _initialized = true;
            Refresh();
        }

        /// <summary>按当前功能开关状态重挂全部补丁。幂等，可反复调用。</summary>
        internal static void Refresh()
        {
            if (!_initialized)
            {
                return;
            }
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
                Debug.Log("[SpectreReplay] 补丁 " + AppliedCount + "/" + DeclaredCount
                    + " 全部挂载");
            }
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
                if (spec.Attr.Kind == PatchKind.Prefix)
                {
                    _harmony.Patch(target, prefix: hm);
                }
                else
                {
                    _harmony.Patch(target, postfix: hm);
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
