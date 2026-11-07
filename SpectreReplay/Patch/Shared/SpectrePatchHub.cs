using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using StackFrame = System.Diagnostics.StackFrame;
using StackTrace = System.Diagnostics.StackTrace;

namespace SpectreReplay.PatchShared;

// SpectrePatch 应用器：扫描程序集 → 解析目标（候选链 + 版本门控）→ 按补丁隔离应用/卸载。
// 运行时目标经 PatchManual 走同一套应用/卸载/开关/兜底管道。
// 引擎就是原生 Harmony（无任何内部手术）：不引入 Replace/Override 等需要改写
// Harmony 内部的扩展类型，TryingCatch 用原生 finalizer 实现。
//
// 性能设计（与裸 Harmony 补丁对齐）：
//   - 目标解析/反射只在 Initialize 时做一次，运行期零反射；
//   - 每补丁一个 Harmony 实例——实例只是 owner ID 字符串，无运行时成本，
//     换来的是按补丁精确卸载（Unpatch 只动自己，不误伤同目标的旧路径补丁）；
//   - 共享 finalizer 是同一个 MethodInfo：无异常时 Harmony 根本不调用 finalizer
//     （try/catch 块空开销），常驻补丁链不多任何方法；
//   - 探针和应用走同一个解析入口，不存在"探针说好、应用时挂"的分裂。
//
// 刷新节拍：宿主在选项变化时调用 Refresh()，补丁随谓词结果即时挂/卸。
internal static class SpectrePatchHub
{
    private sealed class Spec
    {
        public string Id;           // "宿主名:SpectrePatch:类.方法#序号"，同时是 Harmony owner id
        public string Feature;      // 原始声明串（展示用）；null = 常开
        public string[] FeatureParts; // 解析后的功能名（"A,B" → [A,B]），任一开启即挂载；null = 常开
        public bool FeatureAnd;     // Feature 用 & 分隔 = 全部开启才挂载（AND），默认 ,/| = 任一（OR）
        public MethodInfo PatchMethod;
        public SpectrePatchAttribute Attr;
        public List<MethodBase> Targets; // 解析出的全部目标（AllOverloads 可多个）；null = 解析失败
        public string TargetName;   // 探针展示：命中候选的最终目标
        public string Error;        // 解析/应用失败原因；null = 正常
        public bool VersionSkipped; // 版本门控跳过（MinVersion/MaxVersion 不匹配）：不算失败，IsAvailable 忽略
        public Harmony Owner;
        public bool Applied;
        public bool AutoDisabled;   // 异常达阈值自动卸载后置位：Refresh 不再重挂，重新 Initialize 才恢复
    }

    private static readonly List<Spec> _specs = new();
    private static readonly Dictionary<string, Func<bool>> _featureToggles = new();

    // TryingCatch 补丁方法集合：Guard 比对异常栈帧，只吞我们自己补丁体抛出的异常。
    // 只增不删（卸载后的残留条目无害，重新 Initialize 时整体清空）
    private static readonly HashSet<MethodBase> _guardedMethods = new();
    private static readonly HarmonyMethod _guardMethod
        = new(AccessTools.Method(typeof(SpectrePatchHub), nameof(Guard)));
    // Guard 异常去重限流：key = 补丁@目标。同一异常窗口期内详报一次、重复只计数，
    // 窗口过后仍在犯则合并成一行摘要；异常类型/消息一变立即重新详报——
    // 每帧狂抛的补丁不会再刷爆日志，但持续故障和性质变化都看得见
    private sealed class GuardStat
    {
        public DateTime WindowStart; public int Suppressed; public string Sig;
    }
    private static readonly Dictionary<string, GuardStat> _guardStats = new();
    private const int GuardWindowSeconds = 30;
    // 未注册开关的功能名：只告警一次（未注册的功能按关闭处理，补丁不应用）
    private static readonly HashSet<string> _warnedUnregistered = new();
    private static readonly object _lock = new();
    // Initialize 后的第一次 Refresh 汇总行用"首次应用"措辞（之后批次用"+n -n"），
    // 挂/卸明细两种情况都逐条打印
    private static bool _initialRefresh = true;

    // —— 宿主上下文（源码链接共享库）——————————————————————
    // 本文件被各宿主 mod 以 <Compile Include> 编进各自程序集，静态状态随宿主隔离；
    // 游戏程序集与版本政策由宿主启动时注入（先于 Initialize 调用 Configure）
    internal static System.Reflection.Assembly GameAssembly;
    // 宿主实测游戏 build；-1 = 未知（版本门控放行，只降级为日志警告）
    internal static int Release = -1;
    // 宿主政策：支持的最低游戏 build——所有补丁的隐式 MinVersion 地板
    internal static int MinSupportedBuild;
    // 宿主已验证的游戏 build
    internal static int VerifiedBuild;
    internal static bool Verified => Release == VerifiedBuild;
    // **版本政策：正式承认 fail-open（2026-11-02 用户裁定，审计 A-F3 的结论）。**
    // 游戏出新版本（r151+）时不做整体停用——模组继续尝试挂载、继续工作。
    // 安全性不靠「版本号一刀切」，靠两层真实防线：
    //   ① 每个 spec 的解析/挂载失败都会记 Error（签名变了挂不上就是 Error，
    //      不是静默），IsAvailable(feature) 据此变假；
    //   ② 录制/回放入口按 IsAvailable 拦截——补丁真挂不上时拒绝录/播并给
    //      玩家明确提示（note.patches_broken），而不是产出坏数据。
    // 即：fail-open 于「新版本」，fail-closed 于「实际挂载失败」。两者分开，
    // 版本升级不再误伤仍能工作的功能，真正的破坏也不会漏过。
    // 日志前缀（宿主可改成自己的 mod 名）
    internal static string LogTag = "SpectrePatch";
    // **宿主命名空间——patch owner id 的前缀**。
    // 由 Initialize(assembly) 用宿主程序集名填入。
    // 为什么用程序集名而不是 LogTag：LogTag 是可变的日志前缀，拿它当 id 会把
    // "日志标签"与"身份标识"两件事耦合起来；程序集名编译期固定、天然跨 mod
    // 唯一，也不依赖宿主记得额外传参。
    private static string _ns = "SpectrePatch";

    internal static void Configure(System.Reflection.Assembly gameAssembly, int release,
        int minSupportedBuild, int verifiedBuild)
    {
        GameAssembly = gameAssembly;
        Release = release;
        MinSupportedBuild = minSupportedBuild;
        VerifiedBuild = verifiedBuild;
    }

    // —— 生命周期 ——————————————————————————————————————

    // mod 开启时调用一次（可重复调用，重扫前先卸载）
    public static void Initialize(Assembly assembly)
    {

        // 用宿主程序集名作 id 前缀。
        // 放在这里（UnpatchAll 之前）而不是 Configure 里：Initialize 才是唯一
        // 拿得到宿主程序集、且"先卸后装"的分界点；顺序本身不影响 UnpatchAll
        // ——Unapply 用的是 spec.Id（当初挂载时已存好的完整 id），不重新推导。
        _ns = assembly.GetName().Name;
        UnpatchAll();
        AsyncLog.Start();
        lock (_lock)
        {
            _featureToggles.Clear();
            _guardedMethods.Clear();
            _guardStats.Clear();
            _warnedUnregistered.Clear();
            _initialRefresh = true;
            int index = 0;
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    foreach (SpectrePatchAttribute attr in method.GetCustomAttributes<SpectrePatchAttribute>(false))
                    {
                        var expr = ParseFeatures(attr.Feature);
                        var spec = new Spec
                        {
                            Id = _ns + ":SpectrePatch:" + type.Name + "." + method.Name + "#" + (index++),
                            Feature = attr.Feature,
                            FeatureParts = expr?.Parts,
                            FeatureAnd = expr?.And ?? false,
                            PatchMethod = method,
                            Attr = attr
                        };
                        attr.PatchMethod = method;
                        if (expr != null && expr.Invalid)
                            spec.Error = "Feature 语法错误（& 与 ,/| 不可混用）: " + attr.Feature;
                        else
                            Resolve(spec);
                        _specs.Add(spec);
                    }
                }
            }
        }
        LogProbe();
    }

    // 功能开关注册：Feature 名 → 开关谓词（宿主侧与自己的选项绑定）
    public static void SetFeatureToggle(string feature, Func<bool> toggle)
    {
        lock (_lock)
        {
            _featureToggles[feature] = toggle;
        }
    }

    // —— 运行时手动挂载 ————————————————————————————
    // 目标在编译期不可描述（运行时反射才发现的类型/方法，如其他 mod 的）时用；
    // 与 attribute 补丁共用同一套应用/卸载/开关语义/异常兜底与执行链视图。
    // id 需宿主保证唯一（日志与 Harmony owner 显示为 "宿主名:SpectrePatch:manual:"+id），重复 id 幂等覆盖；
    // feature 同 attribute 语义（null = 常开，","/"|" OR，"&" AND）。
    // 注意 UnpatchAll/Initialize 会清掉手动补丁，宿主需在 Initialize 后重新注册
    public static void PatchManual(MethodBase target, MethodInfo patchMethod, PatchType patchType, string id,
        string feature = null, PatchPriority priority = PatchPriority.None, bool tryingCatch = true,
        int disableAfterExceptions = 0)
    {
        if (target == null || patchMethod == null || string.IsNullOrEmpty(id))
        {
            AsyncLog.Warning("[" + LogTag + "] PatchManual 参数不完整（target/patchMethod/id 均不可为空），已忽略");
            return;
        }
        var attr = new SpectrePatchAttribute
        {
            PatchType = patchType,
            Priority = priority,
            TryingCatch = tryingCatch,
            DisableAfterExceptions = disableAfterExceptions,
        };
        string fullId = _ns + ":SpectrePatch:manual:" + id;
        lock (_lock)
        {
            int existing = _specs.FindIndex(s => s.Id == fullId);
            if (existing >= 0)
            {
                Spec old = _specs[existing];
                if (old.Applied) Unapply(old);
                _specs.RemoveAt(existing);
            }
            var expr = ParseFeatures(feature);
            var spec = new Spec
            {
                Id = fullId,
                Feature = feature,
                FeatureParts = expr?.Parts,
                FeatureAnd = expr?.And ?? false,
                PatchMethod = patchMethod,
                Attr = attr,
                Targets = new List<MethodBase> { target },
                TargetName = TargetLabel(target),
            };
            attr.PatchMethod = patchMethod;
            if (expr != null && expr.Invalid)
                spec.Error = "Feature 语法错误（& 与 ,/| 不可混用）: " + feature;
            _specs.Add(spec);
        }
        Refresh();
    }

    // 按当前开关状态应用/卸载（宿主在选项变化时调用）；挂/卸动作逐条记录 + 批次摘要
    public static void Refresh()
    {
        lock (_lock)
        {
            int applied = 0, unapplied = 0;
            // 开关谓词按功能只求值一次（同功能的多个补丁共享结果，49 个 spec 只跑
            // 12 个谓词）；状态未翻转的补丁不触碰 Harmony——GUI 拨单个开关引发的
            // 全量刷新，实际作用于状态翻转的功能
            var toggleResults = new Dictionary<string, bool>();
            // 本批发生过挂/卸的目标 → 执行链视图（Refresh 末尾统一打印）
            var changedTargets = new Dictionary<MethodBase, string>();
            foreach (Spec spec in _specs)
            {
                // 未注册开关的功能：补丁不会应用（按关闭处理），这里逐名显式告警一次，
                // 否则错位的 Feature 字符串会表现为"探针就绪但功能没生效"的静默失败
                if (spec.Error == null && spec.FeatureParts != null)
                    foreach (string part in spec.FeatureParts)
                        if (!_featureToggles.ContainsKey(part) && _warnedUnregistered.Add(part))
                            AsyncLog.Warning("[" + LogTag + "] 功能 \"" + part
                                + "\" 未注册开关（宿主未调 SetFeatureToggle），相关补丁不会应用");
                bool want = spec.Error == null && !spec.AutoDisabled
                    && ToggleOnCached(spec.Feature, spec.FeatureParts, spec.FeatureAnd, toggleResults);
                if (want && !spec.Applied)
                {
                    Apply(spec);
                    if (spec.Applied)
                    {
                        applied++;
                        AsyncLog.Info("[" + LogTag + "] + 挂载 " + spec.Id + " → " + spec.TargetName
                            + " (" + spec.Attr.PatchType
                            + (spec.Feature != null ? " · " + spec.Feature : "") + ")");
                        foreach (MethodBase t in spec.Targets)
                            changedTargets[t] = TargetLabel(t);
                    }
                }
                else if (!want && spec.Applied)
                {
                    Unapply(spec);
                    unapplied++;
                    AsyncLog.Info("[" + LogTag + "] - 卸载 " + spec.Id + " → " + spec.TargetName
                        + (spec.Feature != null ? " · " + spec.Feature : ""));
                    foreach (MethodBase t in spec.Targets)
                        changedTargets[t] = TargetLabel(t);
                }
            }
            // 执行链视图：本批变过的目标打印最终补丁链；首刷全体都算"变化"，
            // 只报外部 mod 介入的目标（见 LogChain），之后批次全量打
            bool firstBatch = _initialRefresh;
            foreach (KeyValuePair<MethodBase, string> kv in changedTargets)
                LogChain(kv.Key, kv.Value, firstBatch);

            int now = 0;
            foreach (Spec spec in _specs)
                if (spec.Applied)
                    now++;
            if (_initialRefresh)
            {
                AsyncLog.Info("[" + LogTag + "] 首次应用 " + now + "/" + _specs.Count
                    + " 个补丁（其余为功能关闭或解析失败，失败明细见上方探针）");
                _initialRefresh = false;
            }
            else if (applied > 0 || unapplied > 0)
            {
                AsyncLog.Info("[" + LogTag + "] 本批 +" + applied + " -" + unapplied
                    + "，当前在挂 " + now + "/" + _specs.Count);
            }
        }
    }

    // 开关求值的按次缓存版本：同一次 Refresh 内每个功能表达式只求值一次；
    // parts 多值时按 and 分隔语义求值——false = 任一开启（OR，默认），true = 全部开启（AND）
    private static bool ToggleOnCached(string raw, string[] parts, bool and, Dictionary<string, bool> cache)
    {
        if (parts == null || parts.Length == 0) return true;
        if (!cache.TryGetValue(raw, out bool on))
        {
            on = and
                ? parts.All(p => _featureToggles.TryGetValue(p, out Func<bool> t) && t())
                : parts.Any(p => _featureToggles.TryGetValue(p, out Func<bool> t) && t());
            cache[raw] = on;
        }
        return on;
    }

    public static void UnpatchAll()
    {
        // 停日志泵并同步清空队列：mod 关闭/重扫时已入队记录不丢
        AsyncLog.Stop();
        lock (_lock)
        {
            foreach (Spec spec in _specs)
                if (spec.Applied)
                    Unapply(spec);
            _specs.Clear();
        }
    }

    // 某功能当前是否可用（所有相关补丁解析/应用正常）。UI 可用来显示功能可用性；
    // 多值 Feature 的 spec 只要含有该功能名即算相关
    public static bool IsAvailable(string feature)
    {
        lock (_lock)
        {
            return _specs.Where(s => s.FeatureParts == null || s.FeatureParts.Length == 0
                    || s.FeatureParts.Contains(feature))
                .All(s => s.Error == null || s.VersionSkipped);
        }
    }

    /// <summary>
    /// 按补丁类查询挂载状态：该类下每一条 [SpectrePatch] 声明都已应用才算真。
    /// 运行时门控用（镜像回声抑制的退路，见 Patch_MirrorEchoSuppressor）。
    ///
    /// **为什么不在 PatchManager 上查**：`_appliedPatches` 只登记
    /// <c>RegisterPatch</c> 的手工补丁类；属性补丁走本 Hub 自己的
    /// <see cref="Spec.Applied"/>，两本账互不相通——2026-10-30 实测，
    /// 查错了账本会让镜像门永远退回互斥（日志 1250 行 vs 156 行的矛盾）。
    /// 类下一条声明都收集不到时按「未挂载」处理，门控退回保守路径。
    /// </summary>
    public static bool IsApplied(Type patchClass)
    {
        lock (_lock)
        {
            bool any = false;
            foreach (Spec s in _specs)
            {
                if (s.PatchMethod == null || s.PatchMethod.DeclaringType != patchClass)
                    continue;
                any = true;
                if (!s.Applied) return false;
            }
            return any;
        }
    }

    // —— 解析 ————————————————————————————————————————————

    private static void Resolve(Spec spec)
    {
        SpectrePatchAttribute a = spec.Attr;
        if (Release > 0)
        {
            // 宿主注入的最低支持版本作隐式地板，attribute 只在需要更高下限时才设 MinVersion
            int min = Math.Max(a.MinVersion, MinSupportedBuild);
            if (Release < min)
            {
                spec.VersionSkipped = true;
                spec.Error = "游戏 r" + Release + " 低于 MinVersion " + min;
                return;
            }
            if (a.MaxVersion > 0 && Release > a.MaxVersion)
            {
                spec.VersionSkipped = true;
                spec.Error = "游戏 r" + Release + " 高于 MaxVersion " + a.MaxVersion + "（未验证）";
                return;
            }
        }
        else if (a.MinVersion > 0 || a.MaxVersion > 0)
        {
            // 版本探测失败（Release == -1）时不能放行带版本分叉的补丁：
            // 分叉对（MaxVersion = 149 / MinVersion = 150）会同时注册，两条目标名一存在一不存在，
            // 不存在的那条会以 Error（而非 VersionSkipped）污染 IsAvailable。宁可整体不挂并显式告警
            spec.VersionSkipped = true;
            spec.Error = "游戏版本探测失败（Release == -1），已跳过带版本约束的补丁：r141–r149 与 r150+ 无法区分";
            return;
        }
        Type type = a.ClassType;
        if (type == null && !string.IsNullOrEmpty(a.ClassName))
            type = GameAssembly?.GetType(a.ClassName) ?? AccessTools.TypeByName(a.ClassName);
        // 类型候选链：ClassName 未命中依次尝试 ClassNames（类型改名/搬命名空间的兜底，新名在前）
        if (type == null && a.ClassNames != null)
            foreach (string cn in a.ClassNames)
            {
                if (string.IsNullOrEmpty(cn)) continue;
                type = GameAssembly?.GetType(cn) ?? AccessTools.TypeByName(cn);
                if (type != null) break;
            }
        if (type == null)
        {
            spec.Error = "目标类型未找到: " + (a.ClassType != null ? a.ClassType.FullName
                : !string.IsNullOrEmpty(a.ClassName) ? a.ClassName
                : string.Join(", ", a.ClassNames ?? Array.Empty<string>()));
            return;
        }

        var names = new List<string>();
        if (!string.IsNullOrEmpty(a.MethodName)) names.Add(a.MethodName);
        if (a.MethodNames != null)
            names.AddRange(a.MethodNames.Where(n => !string.IsNullOrEmpty(n) && !names.Contains(n)));
        if (names.Count == 0)
        {
            spec.Error = "未指定目标方法名";
            return;
        }

        foreach (string name in names)
        {
            if (a.AllOverloads)
            {
                List<MethodBase> all = FindAllOverloads(type, name, a);
                if (all != null && all.Count > 0)
                {
                    spec.Targets = all;
                    spec.TargetName = type.Name + "." + name + (all.Count > 1 ? " ×" + all.Count : "");
                    return;
                }
                continue;
            }
            MethodBase target = FindMethod(type, name, a);
            if (target != null)
            {
                spec.Targets = new List<MethodBase> { target };
                spec.TargetName = type.Name + "." + name;
                return;
            }
        }
        spec.Error = "目标未找到或重载无法消歧（候选: " + string.Join(", ", names)
            + "）；可用 ParameterTypes 精确限定参数签名";
    }

    // 目标解析：访问器（属性/事件/状态机）直取；方法则枚举全部同名重载（含基类）再消歧——
    // ParameterTypes 精确签名优先，其次用补丁方法声明的普通参数名消歧
    // （Prefix 里写了 bool isAuto，就选含 isAuto 的重载），仍不唯一返回 null 进探针，
    // 绝不静默绑到任意重载上（要全挂见 FindAllOverloads）
    private static MethodBase FindMethod(Type type, string name, SpectrePatchAttribute a)
    {
        try
        {
            if (a.Accessor == PropertyAccessor.Get || a.Accessor == PropertyAccessor.Set)
            {
                PropertyInfo pi = AccessTools.Property(type, name);
                if (pi == null) return null;
                return a.Accessor == PropertyAccessor.Get ? pi.GetGetMethod(true) : pi.GetSetMethod(true);
            }
            if (a.Accessor == PropertyAccessor.EventAdd || a.Accessor == PropertyAccessor.EventRemove)
            {
                // 本环境魔改 Harmony 没有 AccessTools.Event，沿类型层级自找（含基类声明）
                EventInfo ei = null;
                for (Type t = type; t != null && ei == null; t = t.BaseType)
                    ei = t.GetEvent(name, BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (ei == null) return null;
                return a.Accessor == PropertyAccessor.EventAdd ? ei.GetAddMethod(true) : ei.GetRemoveMethod(true);
            }
            if (a.Accessor == PropertyAccessor.MoveNext)
                return FindStateMachineMoveNext(type, name, a);

            Type[] args = a.ParameterTypes;
            if (args == null && a.ParameterTypeNames != null)
                args = a.ParameterTypeNames
                    .Select(n => Type.GetType(n, false) ?? AccessTools.TypeByName(n))
                    .ToArray();

            if (name == ".ctor" || name == ".cctor")
            {
                // 构造函数：.ctor = 实例（多个时用 ParameterTypes 消歧，唯一则免写）；
                // .cctor = 静态构造器（至多一个、恒无参）
                bool isStatic = name == ".cctor";
                ConstructorInfo[] ctors = type.GetConstructors(
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                    | BindingFlags.Public | BindingFlags.NonPublic);
                if (isStatic)
                    return ctors.Length == 1 ? ctors[0] : null;
                if (args != null)
                    return ctors.FirstOrDefault(c => SignatureMatches(c, args));
                return ctors.Length == 1 ? ctors[0] : null;
            }

            List<MethodInfo> candidates = CollectMethods(type, name);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            if (args != null)
                return candidates.FirstOrDefault(m => SignatureMatches(m, args));
            return DisambiguateByPatchParams(candidates, a.PatchMethod);
        }
        catch
        {
            return null;
        }
    }

    // 同名方法候选收集（含基类）：
    // 去重——override 方法连同基类（abstract/virtual）声明会被一起收进候选，
    // 两者 GetBaseDefinition 相同——只保留最派生的实现（前向遍历，派生类先入列，
    // 基类声明被视为重复剔除）；不同重载的 BaseDefinition 各不相同，不受影响
    private static List<MethodInfo> CollectMethods(Type type, string name)
    {
        var candidates = new List<MethodInfo>();
        for (Type t = type; t != null; t = t.BaseType)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name == name) candidates.Add(m);
            }
        }
        var deduped = new List<MethodInfo>();
        var seenBaseDefs = new HashSet<MethodInfo>();
        foreach (MethodInfo m in candidates)
            if (seenBaseDefs.Add(m.GetBaseDefinition()))
                deduped.Add(m);
        return deduped;
    }

    // AllOverloads 解析：同名全部重载都返回（.ctor = 全部构造器）；给了 ParameterTypes 时
    // 仍按签名过滤。空结果返回 null，走候选链下一名
    private static List<MethodBase> FindAllOverloads(Type type, string name, SpectrePatchAttribute a)
    {
        try
        {
            if (name == ".ctor")
            {
                return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Cast<MethodBase>().ToList();
            }
            if (name == ".cctor")
                return null; // 静态构造器至多一个，AllOverloads 无意义
            List<MethodInfo> candidates = CollectMethods(type, name);
            Type[] args = a.ParameterTypes;
            if (args == null && a.ParameterTypeNames != null)
                args = a.ParameterTypeNames
                    .Select(n => Type.GetType(n, false) ?? AccessTools.TypeByName(n))
                    .ToArray();
            if (args != null)
                candidates = candidates.Where(m => SignatureMatches(m, args)).ToList();
            return candidates.Cast<MethodBase>().ToList();
        }
        catch
        {
            return null;
        }
    }

    // async/迭代器方法体在编译器生成的状态机里，原方法只负责建状态机；打 MoveNext
    // 才能干预每次推进的逻辑。优先读方法上的 StateMachineAttribute（Async/Iterator 两个
    // 子类都直接指向状态机类型，最可靠）；老编译器没打时退回嵌套类型名 <方法名>d 模式
    //（唯一命中才用，多个放弃进探针）
    private static MethodBase FindStateMachineMoveNext(Type type, string name, SpectrePatchAttribute a)
    {
        List<MethodInfo> candidates = CollectMethods(type, name);
        MethodInfo holder = null;
        if (candidates.Count == 1)
            holder = candidates[0];
        else if (candidates.Count > 1 && a.ParameterTypes != null)
            holder = candidates.FirstOrDefault(m => SignatureMatches(m, a.ParameterTypes));
        if (holder == null) return null;
        foreach (var sma in holder.GetCustomAttributes(
                    typeof(System.Runtime.CompilerServices.StateMachineAttribute), false)
                    .Cast<System.Runtime.CompilerServices.StateMachineAttribute>())
        {
            MethodBase moveNext = sma.StateMachineType?.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (moveNext != null) return moveNext;
        }
        var nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => t.Name.StartsWith("<" + name + ">d", StringComparison.Ordinal)).ToList();
        return nested.Count == 1
            ? nested[0].GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            : null;
    }

    private static bool SignatureMatches(MethodBase method, Type[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != args.Length) return false;
        for (int i = 0; i < args.Length; i++)
            if (parameters[i].ParameterType != args[i]) return false;
        return true;
    }

    // 重载消歧：补丁方法的普通参数（__ 开头的注入项除外）按名落在哪个重载上。
    // 候选必须包含全部普通参数名才幸存；多个幸存时选"多出来的目标参数最少"者，
    // 并列取声明序靠前者（确定性），全部淘汰则返回 null（进探针，不猜）
    private static MethodInfo DisambiguateByPatchParams(List<MethodInfo> candidates, MethodInfo patch)
    {
        if (patch == null) return null;
        ParameterInfo[] ordinary = patch.GetParameters()
            .Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
            .ToArray();
        if (ordinary.Length == 0) return null;
        MethodInfo best = null;
        int bestExtra = int.MaxValue;
        foreach (MethodInfo candidate in candidates)
        {
            ParameterInfo[] target = candidate.GetParameters();
            int matched = 0;
            foreach (ParameterInfo p in ordinary)
                if (target.Any(tp => tp.Name == p.Name)) matched++;
            if (matched < ordinary.Length) continue;
            int extra = target.Length - matched;
            if (extra < bestExtra)
            {
                bestExtra = extra;
                best = candidate;
            }
        }
        return best;
    }

    // —— 应用 ————————————————————————————————————————————

    // Priority 解析：Normal/Last/First 的枚举值与当前魔改版及标准 Harmony 2.x 常量域一致，
    // 直接透传；Final 语义是"比一切常量都晚"——UMM 注入的 0Harmony 若换成 ±int.MaxValue
    // 常量域（Last=int.MinValue），硬编码 -1000000 不再垫底，外部 Last 补丁会排到我们之后
    // 改写结果。运行时反射 HarmonyLib.Priority.Last 动态下探一位（const 会被编译期内联，必须反射）
    private static int? _runtimeLast;

    private static int ResolvePriority(PatchPriority p)
    {
        if (p != PatchPriority.Final)
            return (int)p;
        int last = _runtimeLast ??= ReadRuntimeLast();
        long final = Math.Min(-1000001L, (long)last - 1);
        return final < int.MinValue ? int.MinValue : (int)final;
    }

    private static int ReadRuntimeLast()
    {
        try
        {
            Type t = AccessTools.TypeByName("HarmonyLib.Priority");
            System.Reflection.FieldInfo f = t?.GetField("Last",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f != null && f.FieldType == typeof(int))
                return (int)f.GetValue(null);
        }
        catch { }
        return 0;
    }

    private static void Apply(Spec spec)
    {
        try
        {
            spec.Owner = spec.Owner ?? new Harmony(spec.Id);
            int done = 0;
            List<string> failures = null;
            foreach (MethodBase target in spec.Targets)
            {
                if (PatchOneTarget(spec, target))
                {
                    // 多目标（AllOverloads）时逐目标置位：后面的目标应用失败，
                    // 已挂上的也要能被卸载路径清到
                    spec.Applied = true;
                    done++;
                }
                else
                {
                    (failures ??= new List<string>()).Add(TargetLabel(target) + "（原因见上方「目标挂载失败」行）");
                }
            }
            if (failures != null)
            {
                spec.Error = "应用失败: " + string.Join("; ", failures)
                    + (done > 0 ? "（其余 " + done + " 个目标已挂）" : "");
                AsyncLog.Warning("[" + LogTag + "] " + spec.Id + " → " + spec.Error);
                RecordApplyFailure(spec.Id, spec.Error);
            }
        }
        catch (Exception ex)
        {
            spec.Error = "应用失败: " + ex.GetType().Name + ": " + ex.Message;
            AsyncLog.Warning("[" + LogTag + "] " + spec.Id + " → " + spec.Error);
            RecordApplyFailure(spec.Id, spec.Error);
        }
    }

    // 单目标挂载：Apply 与 EnsureApplied 共用一条路径，保证重新挂上的补丁
    // 与首发挂载的优先级/守卫配置完全一致。返回 false 时已记日志。
    private static bool PatchOneTarget(Spec spec, MethodBase target)
    {
        try
        {
            HarmonyMethod patch = new(spec.PatchMethod);
            // 本库 HarmonyMethod 的优先级字段拼写正常（老版本是 prioriy 拼错版）
            if (spec.Attr.Priority != PatchPriority.None)
                patch.priority = ResolvePriority(spec.Attr.Priority);
            patch.before = spec.Attr.Before;
            patch.after = spec.Attr.After;
            // finalizer 只对 Prefix/Postfix 有意义（Transpiler 异常发生在补丁期，Finalizer 自身即兜底）
            HarmonyMethod guard = spec.Attr.TryingCatch && spec.Attr.PatchType != PatchType.Transpiler
                && spec.Attr.PatchType != PatchType.Finalizer
                ? _guardMethod : null;
            MethodInfo replacement;
            switch (spec.Attr.PatchType)
            {
                case PatchType.Prefix:
                    replacement = spec.Owner.Patch(target, patch, null, null, guard);
                    break;
                case PatchType.Postfix:
                    replacement = spec.Owner.Patch(target, null, patch, null, guard);
                    break;
                case PatchType.Transpiler:
                    replacement = spec.Owner.Patch(target, null, null, patch, null);
                    break;
                case PatchType.Finalizer:
                    replacement = spec.Owner.Patch(target, null, null, null, patch);
                    break;
                default:
                    return false;
            }
            // 桥接世界的落点 ProxyPatch 失败时**返回 null 而不抛**（HarmonyX 拒绝、
            // 转译桥失败等），原生世界正常挂载也总有 replacement。null 必须当失败，
            // 否则 Applied 翻真、下次核对永远等不到补上。
            if (replacement == null)
            {
                AsyncLog.Warning("[" + LogTag + "] " + spec.Id + " → 挂载返回空（HarmonyX 拒绝或桥接降级失败）");
                return false;
            }
            if (guard != null)
                _guardedMethods.Add(spec.PatchMethod);
            return true;
        }
        catch (Exception ex)
        {
            AsyncLog.Warning("[" + LogTag + "] " + spec.Id + " → 目标挂载失败 " + TargetLabel(target) + ": "
                + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 运行时核对一组补丁类的每条已应用声明：补丁方法是否仍在目标的**现行**
    /// 补丁集里；不在就当场补挂，返回补挂条数（0 = 账面齐全）。每次核对打
    /// 一行摘要（全绿也打）——「核对真的跑了」本身就是判据；缺失时另落目标
    /// 的现行补丁名单，是「谁还压在这个方法上」的现场证据。
    ///
    /// <para>为什么需要它：JALib（Jipper 全家桶的公共库，启动时自动联网更新）
    /// 的 JAPatcher 在给自家 mod 挂补丁时会按捕获的快照**重写目标方法的整套
    /// 补丁集**（PatchUpdateWrapper / UpdatePatchInfo 三件套）。JipperResourcePack
    /// 的 Bpm.OnHit 就压在 scrPlayer.Hit 上（两个 JAPatch）；快照取早于我们的
    /// 挂载时，我们的前缀从方法上直接消失。2026-10-31 实测：探针显示挂载
    /// 成功、整局一次没执行，HitSnapshots 录成 0 条（回放报「数据为空」），
    /// 诊断四行一行都没有——前缀不在了。</para>
    ///
    /// <para>补挂走 Harmony 正常 <c>Patch</c>：0Harmony 的 Add 只按 owner 追加、
    /// **不按方法去重**（Remove 只认 owner），所以「账面缺失」时补挂不会产生
    /// 重复条目；「账面在而 detour 陈旧」的场景不能盲目重挂（会翻倍），留给
    /// 日志名单定性后再处理。</para>
    /// </summary>
    public static int EnsureApplied(string context, params Type[] patchClasses)
    {
        int remounted = EnsureAppliedCore(patchClasses, out int specsChecked);
        bool bridge = TryGetHarmonyXReader(out _);
        // 一行核对结论，全绿也打——这是「核对真的跑了」的长期判据
        // （2026-10-31 HitSnapshots=0 的教训：无声 = 没跑，排查方向整个带偏）。
        // context 用 ASCII token 传（调用点在 Patch_Lifecycle，硬编码中文会被
        // 界面文案扫描误伤），日志里翻成中文。
        string label = context switch
        {
            "before-record" => "录制前",
            "before-play" => "回放前",
            "after-empty-record" => "空壳复核",
            _ => context,
        };
        AsyncLog.Warning("[" + LogTag + "] 补丁核对（" + label + (bridge ? " · 桥接" : "") + "）："
            + patchClasses.Length + " 类 / " + specsChecked + " 条已应用声明 —— "
            + (remounted > 0 ? "补挂 " + remounted + " 条" : "全部在位"));
        return remounted;
    }

    /// <summary>
    /// 录制中的看门狗复核：与 <see cref="EnsureApplied"/> 同一套核对/补挂，
    /// 但不打摘要行——泵前缀每 30 帧调一次，全绿也打会淹没日志。缺失时的
    /// 逐条警告照常落（那才是信号）。2026-11-07 编辑器试玩实测：Jipper 系
    /// 惰性初始化经 JAPatcher 重写 scrPlayer.Hit 补丁集的时机可以晚于
    /// 「录制前核对」，核对之后的窗口只有逐帧复核守得住。
    /// </summary>
    public static int EnsureAppliedQuiet(params Type[] patchClasses)
        => EnsureAppliedCore(patchClasses, out _);

    private static int EnsureAppliedCore(Type[] patchClasses, out int specsChecked)
    {
        int remounted = 0;
        specsChecked = 0;
        bool bridge = TryGetHarmonyXReader(out _);
        lock (_lock)
        {
            foreach (Type cls in patchClasses)
            {
                foreach (Spec s in _specs)
                {
                    if (s.PatchMethod == null || s.PatchMethod.DeclaringType != cls)
                        continue;
                    if (!s.Applied || s.Targets == null)
                        continue;
                    foreach (MethodBase target in s.Targets)
                    {
                        specsChecked++;
                        // 桥接世界优先：UMMBridge 把 Harmony.Patch 重写到 HarmonyX，
                        // 那里的账 GetPatchInfo（0Harmony_UMM）看不见
                        bool present = BridgeContains(target, s.PatchMethod, s.Attr.PatchType);
                        if (!present)
                        {
                            // 0Harmony_UMM 账本兜底：转译补丁在桥接禁用转译时走
                            // InvokeNativePatch 落回本世界，只有这里查得到
                            Patches info;
                            try { info = Harmony.GetPatchInfo(target); }
                            catch { continue; }
                            present = InfoContains(info, s.Attr.PatchType, s.PatchMethod);
                            if (!present && !bridge)
                            {
                                // 非桥接环境的现场证据：谁还压在这个方法上。
                                // 桥接环境下这本账里没有我们的补丁是常态，打了只会误导
                                AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 现行补丁集 " + TargetLabel(target)
                                    + ": " + PatchList(info) + "（**本补丁缺失**）");
                            }
                        }
                        if (present)
                            continue;
                        // 桥接世界的现场证据：补挂前把 HarmonyX 账上的现行名单
                        // 落日志——「谁还压在这个方法上」才能点名，不用再猜。
                        string witness = bridge ? BridgePatchList(target) : null;
                        if (PatchOneTarget(s, target))
                        {
                            remounted++;
                            AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 本补丁在核对账本中缺失，已重新挂载");
                            if (witness != null)
                                AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 补挂前 " + TargetLabel(target)
                                    + " 现行补丁集（HarmonyX 账）：" + witness);
                        }
                    }
                }
            }
        }
        ReportJalibPresence();
        return remounted;
    }

    // ── UMMBridge 双 Harmony 世界 ────────────────────────────────
    // 本机 MelonLoader 与 UMM 双加载器共存时，Plugins\UMMBridge.dll 在装载
    // mod 程序集时做 IL 重写：所有 Harmony.Patch 调用 → Bridge.ProxyPatch →
    // 落在 MelonLoader 的 HarmonyX（0Harmony 2.10.2，存储 = PatchManager.
    // PatchInfos 活对象）。而本 Hub 的 Harmony.GetPatchInfo 读的是 UMM 改名
    // 内嵌的 0Harmony_UMM（2.3.6，HarmonySharedState 序列化字典）——**两本
    // 互不相通的账**（2026-11-01 取证实锤：写→读 roundtrip ★分裂，
    // GetAllPatchedMethods 28 条全在 0Harmony_UMM 账本，HarmonyX 账本查不到）。
    //
    // 后果与修法：核对「补丁在不在」必须查 HarmonyX 的账——否则每次录制/
    // 回放启动都误判缺失并经 ProxyPatch 重复挂载：同一前缀在同一 owner
    // （UMMBridge.Proxy.<类型名>）下按次追加、无去重，N 次启动 = 每帧执行
    // N 遍（快照消费加倍 = 回放快进；隔离/落账翻倍）。转译补丁例外：桥接
    // 禁用转译桥时 ProxyPatch 走 InvokeNativePatch 落回 0Harmony_UMM 世界，
    // 所以 presence 判定两本账都查、命中任一即可。
    private static MethodInfo _harmonyXGetPatchInfo;
    private static bool _harmonyXProbed;

    private static bool TryGetHarmonyXReader(out MethodInfo getPatchInfo)
    {
        if (!_harmonyXProbed)
        {
            _harmonyXProbed = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                // 只考虑**别的世界**的 0Harmony：桥接环境里我们的世界叫
                // 0Harmony_UMM（UMM 改名内嵌），名字恰好是 "0Harmony" 的就是
                // MelonLoader 的 HarmonyX；vanilla UMM 环境唯一的 0Harmony 就是
                // 我们的绑定目标——行为验证通过也只是重复检查，无害。
                // **不按类型名猜布局**：同一版本号的不同构建（net35/net472）里
                // PatchManager 的命名空间都不一样（HarmonyLib vs Public.Patching），
                // 猜名字必翻车（2026-11-01 实测：探测静默落空，心跳退化回误判）。
                // 唯一可靠的判据是**行为**：拿自己的空方法经正常 Patch 写一圈，
                // 候选读者读得到才认——写落在哪个世界，哪个世界就是该查的账。
                if (a.GetName().Name != "0Harmony") continue;
                MethodInfo m = a.GetType("HarmonyLib.Harmony")
                    ?.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) });
                if (m == null) continue;
                if (!ReaderCanSeeOurWrite(a, m)) continue;
                _harmonyXGetPatchInfo = m;
                AsyncLog.Warning("[" + LogTag + "] 检测到 UMMBridge 双 Harmony 桥（MelonLoader HarmonyX 与 0Harmony_UMM 各一本账）——补丁核对改查 HarmonyX 账本");
                break;
            }
        }
        getPatchInfo = _harmonyXGetPatchInfo;
        return getPatchInfo != null;
    }

    // 行为验证：经我们正常的 Patch 路径写一个一次性探针（桥接环境下这条写
    // 会被 UMMBridge 重写进 HarmonyX），候选读者若能读到，即认定它就是
    // 「写落点」的账本。读完立即拆（HarmonyX 世界的拆除走反射，失败也无害——
    // 靶是我们自己的空方法，永远不会被调用）。
    private static bool ReaderCanSeeOurWrite(Assembly hxAsm, MethodInfo getPatchInfo)
    {
        const string owner = "SpectreReplay:bridge-probe";
        MethodInfo target = typeof(SpectrePatchHub).GetMethod(
            nameof(ProbeTarget), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo prefix = typeof(SpectrePatchHub).GetMethod(
            nameof(ProbePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null) return false;
        var h = new Harmony(owner);
        try
        {
            h.Patch(target, prefix: new HarmonyMethod(prefix));
            object patches = getPatchInfo.Invoke(null, new object[] { target });
            if (patches == null) return false;
            FieldInfo list = patches.GetType().GetField("Prefixes",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (list?.GetValue(patches) is not IEnumerable coll) return false;
            foreach (object p in coll)
            {
                object m = p.GetType().GetProperty("PatchMethod")?.GetValue(p)
                    ?? p.GetType().GetField("PatchMethod")?.GetValue(p);
                if (ReferenceEquals(m, prefix)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            // 拆除：桥接世界要走 HarmonyX 自己的 UnpatchAll（我们的 Unpatch 调用
            // 不会被重写，直接调只会拆 0Harmony_UMM 的账）。失败忽略——靶永远
            // 不会被调用，遗留探针无行为影响。
            try
            {
                object hx = hxAsm.CreateInstance("HarmonyLib.Harmony", false,
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new object[] { owner }, null, null);
                hxAsm.GetType("HarmonyLib.Harmony")
                    ?.GetMethod("UnpatchAll", new[] { typeof(string) })
                    ?.Invoke(hx, new object[] { owner });
            }
            catch
            {
                // 探针遗留不影响任何行为
            }
        }
    }

    private static void ProbeTarget() { }

    private static bool ProbePrefix() => true;

    // HarmonyX 账本里某目标的某补丁方法在不在。经反射调 2.10 的
    // Harmony.GetPatchInfo（与 Bridge 自身读账同一条路），再比对 PatchMethod。
    private static bool BridgeContains(MethodBase target, MethodInfo patchMethod, PatchType type)
    {
        if (!TryGetHarmonyXReader(out MethodInfo getPatchInfo)) return false;
        object patches;
        try { patches = getPatchInfo.Invoke(null, new object[] { target }); }
        catch { return false; }
        if (patches == null) return false;
        string prop = type switch
        {
            PatchType.Prefix => "Prefixes",
            PatchType.Postfix => "Postfixes",
            PatchType.Transpiler => "Transpilers",
            PatchType.Finalizer => "Finalizers",
            _ => null,
        };
        if (prop == null) return false;
        // 两代 0Harmony 的 Patches.* 都是 readonly 字段（不是属性），一律按字段取
        FieldInfo list = patches.GetType().GetField(prop,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (list?.GetValue(patches) is not IEnumerable coll) return false;
        foreach (object p in coll)
        {
            // Patch.PatchMethod：2.x 两代同名（2.10 为属性，2.3.6 为属性），字段兜底
            object m = p.GetType().GetProperty("PatchMethod")?.GetValue(p)
                ?? p.GetType().GetField("PatchMethod")?.GetValue(p);
            if (ReferenceEquals(m, patchMethod)) return true;
        }
        return false;
    }

    // HarmonyX 账的现行补丁名单：那边的 Patches 是另一个 0Harmony 世界
    // （2.10）的类型，PatchList 的强类型走不通，按字段反射读，格式对齐。
    // 只在「账面缺失、准备补挂」这个罕见时刻调用，反射开销可以不计。
    private static string BridgePatchList(MethodBase target)
    {
        if (!TryGetHarmonyXReader(out MethodInfo getPatchInfo)) return "（读不到 HarmonyX 账）";
        object patches;
        try { patches = getPatchInfo.Invoke(null, new object[] { target }); }
        catch (Exception ex) { return "（读取失败 " + ex.GetType().Name + "）"; }
        if (patches == null) return "（无任何补丁）";
        string s = BridgeOneList(patches, "前缀", "Prefixes")
            + BridgeOneList(patches, "后缀", "Postfixes")
            + BridgeOneList(patches, "转译", "Transpilers")
            + BridgeOneList(patches, "收尾", "Finalizers");
        return s.Length == 0 ? "（无任何补丁）" : s.TrimEnd();
    }

    private static string BridgeOneList(object patches, string kind, string field)
    {
        FieldInfo list = patches.GetType().GetField(field,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (list?.GetValue(patches) is not IEnumerable coll) return "";
        var names = new List<string>();
        foreach (object p in coll)
        {
            object m = p.GetType().GetProperty("PatchMethod")?.GetValue(p)
                ?? p.GetType().GetField("PatchMethod")?.GetValue(p);
            var mb = m as MethodBase;
            names.Add((mb?.DeclaringType?.Name ?? "?") + "." + (mb?.Name ?? "?"));
        }
        return names.Count == 0 ? "" : kind + "[" + string.Join(", ", names) + "] ";
    }

    // ── 强制重建（账面在而 detour 陈旧的自愈）─────────────────
    // 2026-11-07 编辑器试玩实测：JAPatcher 重写目标方法后，HarmonyX 账仍
    // 显示我们的前缀在位、方法上却整局不再执行——账面核对（EnsureApplied）
    // 对这种状态天然失明。唯一自愈是把我们的条目从账上拆掉再重挂，逼
    // Harmony 按现行补丁集重写 detour。先拆后挂是硬序：Add 只按 owner 追加
    // 不去重，不拆就重挂会让前缀跑两遍（快照翻倍）。桥接世界的 Unpatch
    // 调用不会被 UMMBridge 转写（只转写 Patch），必须反射走 HarmonyX 自己
    // 的 UnpatchAll；拆不干净就不重挂——宁可这局继续空，也不能翻倍。
    public static int ForceRebuild(Type patchClass)
    {
        int rebuilt = 0;
        bool bridge = TryGetHarmonyXReader(out _);
        lock (_lock)
        {
            foreach (Spec s in _specs)
            {
                if (s.PatchMethod == null || s.PatchMethod.DeclaringType != patchClass) continue;
                if (!s.Applied || s.Targets == null) continue;
                foreach (MethodBase target in s.Targets)
                {
                    string witness = bridge ? BridgePatchList(target) : null;
                    bool removed = bridge ? UnpatchViaHarmonyX(target, s) : UnpatchNative(target, s);
                    if (!removed)
                    {
                        AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 账面条目拆不掉，放弃重建（防翻倍）");
                        continue;
                    }
                    if (PatchOneTarget(s, target))
                    {
                        rebuilt++;
                        AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 账面在而前缀不执行，已强拆重挂重建 detour");
                        if (witness != null)
                            AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → 重建前 " + TargetLabel(target)
                                + " 现行补丁集（HarmonyX 账）：" + witness);
                    }
                }
            }
        }
        return rebuilt;
    }

    private static bool UnpatchNative(MethodBase target, Spec s)
    {
        try
        {
            s.Owner.Unpatch(target, HarmonyPatchTypeOf(s.Attr.PatchType), s.Owner.Id);
            return true;
        }
        catch (Exception ex)
        {
            AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → Unpatch 失败: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }

    private static bool UnpatchViaHarmonyX(MethodBase target, Spec s)
    {
        if (!TryGetHarmonyXReader(out MethodInfo getPatchInfo)) return false;
        try
        {
            Assembly hxAsm = getPatchInfo.DeclaringType.Assembly;
            object patches = getPatchInfo.Invoke(null, new object[] { target });
            if (patches == null) return true;
            FieldInfo list = patches.GetType().GetField(PatchListFieldName(s.Attr.PatchType),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (list?.GetValue(patches) is not IEnumerable coll) return true;
            string owner = null;
            foreach (object p in coll)
            {
                object m = p.GetType().GetProperty("PatchMethod")?.GetValue(p)
                    ?? p.GetType().GetField("PatchMethod")?.GetValue(p);
                if (!ReferenceEquals(m, s.PatchMethod)) continue;
                owner = (string)(p.GetType().GetProperty("Owner")?.GetValue(p)
                    ?? p.GetType().GetField("Owner")?.GetValue(p));
                break;
            }
            if (owner == null) return true;
            object hx = hxAsm.CreateInstance("HarmonyLib.Harmony", false,
                BindingFlags.Public | BindingFlags.Instance, null,
                new object[] { LogTag + ":force-rebuild" }, null, null);
            hxAsm.GetType("HarmonyLib.Harmony")
                ?.GetMethod("UnpatchAll", new[] { typeof(string) })
                ?.Invoke(hx, new object[] { owner });
            return !BridgeContains(target, s.PatchMethod, s.Attr.PatchType);
        }
        catch (Exception ex)
        {
            AsyncLog.Warning("[" + LogTag + "] " + s.Id + " → HarmonyX Unpatch 失败: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }

    private static string PatchListFieldName(PatchType type) => type switch
    {
        PatchType.Prefix => "Prefixes",
        PatchType.Postfix => "Postfixes",
        PatchType.Transpiler => "Transpilers",
        PatchType.Finalizer => "Finalizers",
        _ => null,
    };

    private static HarmonyPatchType HarmonyPatchTypeOf(PatchType type) => type switch
    {
        PatchType.Prefix => HarmonyPatchType.Prefix,
        PatchType.Postfix => HarmonyPatchType.Postfix,
        PatchType.Transpiler => HarmonyPatchType.Transpiler,
        PatchType.Finalizer => HarmonyPatchType.Finalizer,
        _ => HarmonyPatchType.All,
    };

    // ── JALib 在场探测（每次会话首次核对时一次）─────────────────
    // JALib（Jipper 全家桶公共库，自动联网更新）的 JAPatcher 会给自家 mod 挂
    // 补丁时按捕获快照**重写目标方法的整套补丁集**——2026-10-31 实测它摘掉
    // 过 scrPlayer.Hit 的录制前缀（HitSnapshots 录成 0）。在场与否都要落日志：
    // 「在场」= 本会话的补丁核对确有对手；「不在场」= 探测跑过且没对手。
    // 缺任何一条，日志里「没有在场行」都无法区分「没探测」与「不在场」。
    //
    // 为什么不在 Load() 探测：UMM 的 mod 加载顺序不保证，JALib 可能晚于我们
    // 装载；首次录制/回放启动时所有 mod 必然已加载，那时才是可靠时点。
    private static bool _jalibProbed;
    private static void ReportJalibPresence()
    {
        if (_jalibProbed) return;
        _jalibProbed = true;
        bool present = false;
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n.StartsWith("JALib", StringComparison.Ordinal)
                    || n.StartsWith("JAMod", StringComparison.Ordinal))
                {
                    present = true;
                    break;
                }
            }
        }
        catch
        {
            // 探测失败不阻断核对本身
        }
        AsyncLog.Warning("[" + LogTag + "] "
            + (present
                ? "JALib（Jipper 全家桶公共库）在场——其 JAPatcher 会按捕获快照重写目标方法的整套补丁集"
                  + "（2026-10-31 曾摘掉 scrPlayer.Hit 前缀致 HitSnapshots=0）；"
                  + "补丁核对已启用，缺失当场补挂"
                : "未检测到 JALib（Jipper 公共库）——补丁集被外部重写的风险低；补丁核对仍每次执行"));
    }

    // 现行补丁集的紧凑名单：每类一行内联，给「录制前核对」日志用。
    private static string PatchList(Patches info)
    {
        if (info == null) return "（无任何补丁）";
        static string One(string kind, IReadOnlyList<Patch> arr)
        {
            if (arr == null || arr.Count == 0) return "";
            return kind + "[" + string.Join(", ", arr.Select(p =>
                (p.PatchMethod != null ? p.PatchMethod.DeclaringType.Name : "?") + "." + (p.PatchMethod?.Name ?? "?"))) + "] ";
        }
        string s = One("前缀", info.Prefixes) + One("后缀", info.Postfixes)
            + One("转译", info.Transpilers) + One("收尾", info.Finalizers);
        return s.Length == 0 ? "（无任何补丁）" : s.TrimEnd();
    }

    private static bool InfoContains(Patches info, PatchType type, MethodInfo method)
    {
        // 这版 0Harmony 的 Harmony.GetPatchInfo 返回 Patches（只读集合、Pascal 命名）
        if (info == null) return false;
        IReadOnlyList<Patch> arr = type switch
        {
            PatchType.Prefix => info.Prefixes,
            PatchType.Postfix => info.Postfixes,
            PatchType.Transpiler => info.Transpilers,
            PatchType.Finalizer => info.Finalizers,
            _ => null,
        };
        if (arr == null) return false;
        foreach (Patch p in arr)
            if (p.PatchMethod == method) return true;
        return false;
    }

    // ── 挂载失败汇总（游戏内可见） ──────────────────────────
    // 补丁挂不上 = 对应功能**静默消失**：用户改了设置却毫无反应，原因只在日志里。
    // 键为 spec.Id：同一补丁反复失败（每次切设置都重试挂载）只记一次，
    // 集合以补丁总数为上界，有界。
    private static readonly List<string> ApplyFailures = new();
    private static bool failureSummaryShown;

    /// <summary>
    /// 未挂载的补丁数。给设置页顶部那行警示用——挂不上 = 对应功能静默消失，
    /// 玩家只会觉得"这个开关点了没反应"，而原因只在日志里。
    /// 通知（ShowApplyFailureSummary）瞬时弹一次，关了就没；这一行常驻，
    /// 玩家什么时候打开设置页都能看见。
    /// </summary>
    public static int ApplyFailureCount
    {
        get { lock (_lock) { return ApplyFailures.Count; } }
    }

    private static void RecordApplyFailure(string specId, string error)
    {
        lock (_lock)
        {
            if (ApplyFailures.Exists(x => x.StartsWith(specId + "|", StringComparison.Ordinal)))
            {
                return;
            }
            // Harmony 异常常带整段 IL，截断避免通知框爆版
            string shortError = error != null && error.Length > 120 ? error.Substring(0, 120) + "…" : error;
            ApplyFailures.Add(specId + "|" + shortError);
        }
    }

    /// <summary>
    /// 由宿主（Main）在启动完成后调用：若有补丁未能挂载，弹一次汇总提示。
    /// 无失败时什么都不做（有失败也只提示一次，避免每次开关设置都打扰）。
    /// </summary>
    public static void ShowApplyFailureSummary()
    {
        List<string> snapshot;
        lock (_lock)
        {
            if (failureSummaryShown || ApplyFailures.Count == 0) return;
            failureSummaryShown = true;
            snapshot = new List<string>(ApplyFailures);
        }
        AsyncLog.Warning("[" + LogTag + "] 合计 " + snapshot.Count + " 个补丁未能挂载（对应功能不会生效）："
            + string.Join(" | ", snapshot));
        SpectreReplay.SpectreState.TriggerMessage(
            "SpectreReplay 有 " + snapshot.Count + " 个功能未能加载（游戏版本可能不兼容），详见日志",
            extra: 5f, SpectreReplay.SpectreState.NotifType.Warning);
    }

    private static void Unapply(Spec spec)
    {
        try
        {
            if (spec.Owner != null && spec.Targets != null)
                foreach (MethodBase target in spec.Targets)
                    spec.Owner.Unpatch(target, HarmonyPatchType.All, spec.Id);
        }
        catch (Exception ex)
        {
            AsyncLog.Warning("[" + LogTag + "] 卸载失败 " + spec.Id + ": " + ex.Message);
        }
        spec.Applied = false;
    }

    // —— 异常兜底（共享 finalizer）———————————————————————

    // Guard 反查：补丁方法 + 实际目标 → 所属 Spec（自动卸载阈值挂在 Spec 的 attribute 上）。
    // 只在异常路径调用，O(n) 遍历可接受
    private static Spec FindOwningSpec(MethodBase patchMethod, MethodBase target)
    {
        foreach (Spec s in _specs)
            if (s.PatchMethod == patchMethod && s.Targets != null && s.Targets.Contains(target))
                return s;
        return null;
    }

    // 只在异常路径执行（无异常时 Harmony 不会调用 finalizer）。
    // 只吞"我们自己补丁体"抛出的异常（栈帧比对），游戏本体的异常原样放行——
    // 否则会把游戏自身的 bug 也吞掉，静默劣化比报错更糟。
    // 注意：Prefix 中途抛出被吞后原方法不会执行（Harmony 语义），该功能可能退化，
    // 属"有日志的可控降级"，仍优于旧二进制在新游戏上反复打断游戏流程。
    // 返回 null = 吞掉；返回原异常 = 继续抛。Guard 自身绝不允许再抛
    // （finalizer 内的异常 Harmony 不再保护，会直接终止游戏）。
    private static Exception Guard(Exception __exception, MethodBase __originalMethod)
    {
        if (__exception == null) return null;
        try
        {
            var stack = new StackTrace(__exception, false);
            foreach (StackFrame frame in stack.GetFrames())
            {
                MethodBase frameMethod = frame?.GetMethod();
                if (frameMethod == null) continue;
                lock (_lock)
                {
                    if (!_guardedMethods.Contains(frameMethod)) continue;
                    string key = frameMethod.DeclaringType?.FullName + "." + frameMethod.Name
                        + "@" + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
                    string sig = __exception.GetType().Name + ": " + __exception.Message;
                    DateTime nowUtc = DateTime.UtcNow;
                    // 本窗口内累计异常次数（含本次）：首现/换性质 = 1，窗口内重复 = 已计数 + 1
                    int occurrences;
                    if (!_guardStats.TryGetValue(key, out GuardStat stat) || stat.Sig != sig)
                    {
                        // 首次出现（或异常性质变了）：详报并开窗
                        stat = new GuardStat { WindowStart = nowUtc, Suppressed = 0, Sig = sig };
                        _guardStats[key] = stat;
                        occurrences = 1;
                        AsyncLog.Warning("[" + LogTag + "] 补丁 " + frameMethod.DeclaringType?.FullName + "."
                            + frameMethod.Name + " 在 " + __originalMethod.DeclaringType?.Name + "."
                            + __originalMethod.Name + " 内抛出异常（已吞掉，功能可能退化，游戏版本不匹配？）: "
                            + __exception.GetType().Name + ": " + __exception.Message + "\n" + __exception.StackTrace);
                    }
                    else if ((nowUtc - stat.WindowStart).TotalSeconds >= GuardWindowSeconds)
                    {
                        // 窗口外仍在重复：一行合并摘要（含窗口内次数），重新开窗
                        AsyncLog.Warning("[" + LogTag + "] 补丁 " + frameMethod.DeclaringType?.Name + "."
                            + frameMethod.Name + " 在 " + __originalMethod.DeclaringType?.Name + "."
                            + __originalMethod.Name + " 内异常持续（" + GuardWindowSeconds + "s 内重复 "
                            + stat.Suppressed + " 次，已合并）: " + sig);
                        stat.WindowStart = nowUtc; stat.Suppressed = 0;
                        occurrences = 1;
                    }
                    else
                    {
                        stat.Suppressed++;
                        occurrences = stat.Suppressed + 1;
                    }
                    // 异常自动卸载：窗口内累计达到补丁声明的阈值，把这个补丁卸了（宁缺毋滥）。
                    // finalizer 可能在任意线程触发，Unpatch 换回原代码指针对执行中的旧帧无碍，
                    // Unapply 自带异常兜底
                    Spec owner = FindOwningSpec(frameMethod, __originalMethod);
                    if ((owner?.Attr.DisableAfterExceptions ?? 0) > 0
                        && occurrences >= owner.Attr.DisableAfterExceptions)
                    {
                        owner.AutoDisabled = true;
                        Unapply(owner);
                        AsyncLog.Warning("[" + LogTag + "] 补丁 " + frameMethod.DeclaringType?.FullName + "."
                            + frameMethod.Name + " 异常达阈值（窗口内 " + occurrences + " 次），已自动卸载；"
                            + "重新 Initialize（mod 重启）前不会自动恢复");
                    }
                    return null;
                }
            }
        }
        catch
        {
            // 比对失败时保守放行原异常
        }
        return __exception;
    }

    // —— 执行链视图 ————————————————————————————————————————

    // Feature 串解析：',' 或 '|' 分隔 = 任一开启即挂载（OR），'&' 分隔 = 全部开启才挂载（AND）；
    // 两种分隔符不可混用（混用置 Invalid，该补丁进探针）。null/空串/全空 → null（常开）
    private sealed class FeatureExpr
    {
        public string[] Parts; public bool And; public bool Invalid;
    }

    private static FeatureExpr ParseFeatures(string feature)
    {
        if (string.IsNullOrEmpty(feature)) return null;
        bool and = feature.Contains('&');
        bool or = feature.Contains(',') || feature.Contains('|');
        var expr = new FeatureExpr { And = and };
        if (and && or)
        {
            expr.Invalid = true;
            return expr;
        }
        char[] seps = and ? new[] { '&' } : new[] { ',', '|' };
        string[] parts = feature.Split(seps)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return null;
        expr.Parts = parts;
        return expr;
    }

    private static string TargetLabel(MethodBase m)
        => (m.DeclaringType?.Name ?? "?") + "." + m.Name;

    // 打印目标方法最终生效的补丁链——Harmony 排序后的真实顺序（Before/After 跨 mod
    // 约束的结果就是它），其他 mod 打在同一方法上的补丁也在列，跨 mod 排序问题
    // 一镜到底。owner 即 Harmony 实例 ID：本框架的形如 "宿主名:SpectrePatch:类.方法#序号"，
    // 别的 mod 是它们自己的 ID。
    // foreignOnly=true 时只报存在外部补丁的目标：启动首刷全体 spec 都算"变化"，
    // 全打太吵，跨 mod 介入才是重点；之后的批次（拨一次开关就几行）全量打
    private static void LogChain(MethodBase target, string label, bool foreignOnly)
    {
        try
        {
            Patches info = Harmony.GetPatchInfo(target);
            // 本环境 Harmony 的 Patches.* 是 Patch 列表（owner/priority 为公开字段）
            List<Patch> pre = info?.Prefixes.ToList();
            List<Patch> post = info?.Postfixes.ToList();
            List<Patch> trans = info?.Transpilers.ToList();
            List<Patch> fin = info?.Finalizers.ToList();
            if (info == null || (pre.Count == 0 && post.Count == 0
                && trans.Count == 0 && fin.Count == 0))
            {
                if (!foreignOnly)
                    AsyncLog.Info("[" + LogTag + "] 链 " + label + ": 已无任何补丁");
                return;
            }
            bool foreign = pre.Concat(post).Concat(trans).Concat(fin)
                .Any(p => !p.owner.StartsWith(_ns + ":SpectrePatch:", StringComparison.Ordinal));
            if (foreignOnly && !foreign) return;
            string Desc(List<Patch> list)
                => string.Join(" > ", list.Select(p => p.owner + "(" + p.priority + ")"));
            string chain = (pre.Count > 0 ? Desc(pre) + " → " : "")
                + "原方法"
                + (post.Count > 0 ? " → " + Desc(post) : "")
                + (trans.Count > 0 ? " · Transpiler[" + Desc(trans) + "]" : "")
                + (fin.Count > 0 ? " · Finalizer[" + Desc(fin) + "]" : "");
            AsyncLog.Info("[" + LogTag + "] 链 " + label + ": " + chain
                + (foreign ? " ⚡外部mod介入" : ""));
        }
        catch
        {
            // 执行链视图只是诊断，失败不影响刷新本身
        }
    }

    // —— 探针 ————————————————————————————————————————————

    private static void LogProbe()
    {
        int total, ok;
        List<Spec> failed;
        lock (_lock)
        {
            total = _specs.Count;
            failed = _specs.Where(s => s.Error != null).ToList();
            ok = total - failed.Count;
        }
        AsyncLog.Info("[" + LogTag + "] SpectrePatch 探针: 游戏 r" + Release
            + "，已验证 r" + VerifiedBuild + "，" + ok + "/" + total + " 就绪");
        if (!Verified)
            AsyncLog.Warning("[" + LogTag + "] 当前游戏版本未验证（r" + Release
                + " ≠ r" + VerifiedBuild + "）：fail-open 继续挂载，个别补丁挂不上会各自报错，"
                + "录制/回放入口按可用性拦截");
        foreach (Spec s in failed)
            AsyncLog.Warning("[" + LogTag + "]   ✗ " + s.Id + " → " + s.Error);
    }
}
