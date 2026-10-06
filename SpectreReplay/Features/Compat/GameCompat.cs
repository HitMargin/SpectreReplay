using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.Compat;

/// <summary>
/// 游戏 API 形状差异的运行期桥。
///
/// Spectre 编译时只引用一份 Assembly-CSharp（r150），所以凡是 r150 改了签名的成员，
/// 直接调用会编译通过、在旧版本运行期抛 MissingMethodException。这类成员不能靠
/// [SpectrePatch] 的 MinVersion/MaxVersion 分叉（那只管"补丁挂不挂"，管不了"我怎么调"），
/// 只能在运行期按名字+参数形状挑重载。
///
/// 本类不做任何版本号判断：一律"先找新版形状，找不到退旧版形状"，
/// 因此游戏再改一次形状时降级路径依然可用（最坏退回本地复刻值）。
/// </summary>
internal static class GameCompat
{
    private const BindingFlags AnyMethod =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    private static void WarnOnce(string key, Exception e)
    {
        if (Warned.Add(key))
            Debug.LogWarning($"[SpectreReplay] 兼容层 {key} 失败，已降级：{e.GetType().Name} {e.Message}");
    }

    private static MethodInfo Find(Type type, string name, params Type[] paramTypes)
    {
        try
        {
            return type.GetMethod(name, AnyMethod, null, paramTypes, null);
        }
        catch (Exception e)
        {
            WarnOnce(name + "@" + type.Name, e);
            return null;
        }
    }

    private static MethodInfo FindByArity(Type type, string name, int arity, Func<MethodInfo, bool> accept)
    {
        try
        {
            foreach (var m in type.GetMethods(AnyMethod))
            {
                if (m.Name != name)
                    continue;
                var ps = m.GetParameters();
                if (ps.Length != arity)
                    continue;
                if (accept != null && !accept(m))
                    continue;
                return m;
            }
        }
        catch (Exception e)
        {
            WarnOnce(name + "@" + type.Name, e);
        }

        return null;
    }

    // ── 窗口边界：旧 = double + HitMarginGeneral；新 = 结构体 + Difficulty ──
    // 刻意走游戏函数而非本地复刻：旧版本上 Spectre 的真实判定改写正是挂在这个函数上，
    // 绕过它会让"跟随游戏窗口"的档位哨兵拿不到改写后的值。新版本不再改写此函数
    // （改在判定决策点，见 Patch_RealJudge），这里自然拿到原版值。

    private static MethodInfo _boundaryStruct;
    private static MethodInfo _boundaryScalar;
    private static bool _boundaryProbed;

    // ── 每帧热路径缓存 ─────────────────────────────────────────────
    // Boundary() 的真实调用量远大于"判定条每帧 3 次"：开着自定义判定时，
    // Patch_TierTickColor.ShowPrefix 对**每个 tick、每帧**调用它，而且一次调用里
    // 还会经 TryTierColor / ApplyTierTickOutline / CustomJudge.TierRangeBar 的
    // 档位循环反复取窗口（每档至少 1 次）。屏幕上 20 个 tick × 8 档 ≈ 每帧 200+ 次。
    //
    // 原实现每次都做：
    //     kind.ToString()                        → 分配字符串
    //     new object[] { ... }                  → 分配数组
    //     _boundaryStruct.Invoke(...)            → 反射调用
    //     ReturnType.GetField(kind.ToString())   → 反射字段查找（最贵）
    //     field.GetValue(boxed)                  → 反射取值
    // 结果是"开自定义判定后打判定会掉帧"。下面三样都改成一次解析、反复复用：
    //   ① 字段名按枚举值缓存（用 HitMarginCompat.CachedName，避免 ToString 分配）
    //   ② FieldInfo 按枚举值缓存
    //   ③ 调用参数数组复用（每次调用写回 4 个槽位，不再新建）
    // 这些值只由游戏版本决定，进程内不会变，缓存无失效风险。
    private static readonly Dictionary<HitMarginGeneral, string> _boundaryNames = new();
    private static readonly Dictionary<HitMarginGeneral, FieldInfo> _boundaryFields = new();
    private static object[] _boundaryArgs;

    private static string BoundaryName(HitMarginGeneral kind)
    {
        if (_boundaryNames.TryGetValue(kind, out var cached))
            return cached;
        // 注意：HitMarginCompat.CachedName 收的是 HitMargin，与这里的
        // HitMarginGeneral 不是同一个枚举，不能复用。枚举名是不可变映射，
        // 缓存语义完全等价，只省掉每次调用的字符串分配。
        string name = kind.ToString();
        _boundaryNames[kind] = name;
        return name;
    }

    private static FieldInfo BoundaryField(HitMarginGeneral kind)
    {
        if (_boundaryFields.TryGetValue(kind, out var cached))
            return cached;
        FieldInfo field = _boundaryStruct.ReturnType.GetField(BoundaryName(kind));
        _boundaryFields[kind] = field;
        return field;
    }

    private static void ProbeBoundary()
    {
        if (_boundaryProbed)
            return;
        _boundaryProbed = true;
        try
        {
            foreach (var m in typeof(scrMisc).GetMethods(AnyMethod))
            {
                if (m.Name != "GetAdjustedAngleBoundaryInDeg")
                    continue;
                var ps = m.GetParameters();
                if (ps.Length != 4)
                    continue;
                if (ps[0].ParameterType == typeof(Difficulty))
                    _boundaryStruct = m;
                else if (ps[0].ParameterType == typeof(HitMarginGeneral))
                    _boundaryScalar = m;
            }
        }
        catch (Exception e)
        {
            WarnOnce("ProbeBoundary", e);
        }
    }

    /// <summary>当前游戏版本的窗口边界（度）。找不到游戏函数时退回本地同式复刻。</summary>
    internal static double Boundary(HitMarginGeneral kind, double bpmTimesSpeed, double pitch, double marginScale)
    {
        ProbeBoundary();
        try
        {
            if (_boundaryStruct != null)
            {
                // 参数数组复用：每次调用写回 4 个槽位，不再 new object[4]。
                // 结构体返回的 MethodInfo.Invoke 仍每次分配返回值装箱（无法避免，
                // 但省掉了参数数组这一份）。
                _boundaryArgs ??= new object[4];
                _boundaryArgs[0] = GCS.difficulty;
                _boundaryArgs[1] = bpmTimesSpeed;
                _boundaryArgs[2] = pitch;
                _boundaryArgs[3] = marginScale;
                object boxed = _boundaryStruct.Invoke(null, _boundaryArgs);
                // 原注释写"每帧 3 次、约 0.15% 帧预算，故不缓存"——那个计数只覆盖了
                // 判定条。实测开着自定义判定打判定会掉帧，因为 Tick 着色这条路径
                // 对**每个 tick 每帧**都要取窗口（TryTierColor / ApplyTierTickOutline /
                // TierRangeBar 档位循环），量级是每帧数百次。字段查找改为按枚举值缓存。
                var field = BoundaryField(kind);
                if (field != null && boxed != null)
                    return Convert.ToDouble(field.GetValue(boxed));
            }
            else if (_boundaryScalar != null)
            {
                object value = _boundaryScalar.Invoke(null, new object[] { kind, bpmTimesSpeed, pitch, marginScale });
                if (value != null)
                    return Convert.ToDouble(value);
            }
        }
        catch (Exception e)
        {
            WarnOnce("Boundary", e);
        }

        return CustomJudge.CustomJudge.BoundaryRaw(kind, bpmTimesSpeed, pitch, marginScale);
    }

    // ── scrPlanet.SwitchChosen() → SwitchChosen(long? hitTick) ──

    private static MethodInfo _switchChosen;

    internal static scrPlanet SwitchChosen(scrPlanet planet)
    {
        if (planet == null)
            return null;
        _switchChosen ??= FindByArity(typeof(scrPlanet), "SwitchChosen", 0, null)
            ?? FindByArity(typeof(scrPlanet), "SwitchChosen", 1, m => m.ReturnType == typeof(scrPlanet));
        if (_switchChosen == null)
        {
            WarnOnce("SwitchChosen", new MissingMethodException("scrPlanet.SwitchChosen"));
            return planet;
        }

        try
        {
            object[] args = _switchChosen.GetParameters().Length == 0 ? null : new object[] { null };
            return (scrPlanet)_switchChosen.Invoke(planet, args);
        }
        catch (Exception e)
        {
            WarnOnce("SwitchChosen", e);
            return planet;
        }
    }

    // ── scrPlayer.Hit(bool) → Hit(long? hitTick, bool isAuto) ──

    private static MethodInfo _playerHit;

    /// <param name="hitTick">
    /// r150+ 异步输入的音频 tick；旧版忽略（无该形参）。
    /// 传 null 即让游戏按当前角度自行推算（等价于老版语义）。
    /// </param>
    internal static bool PlayerHit(scrPlayer player, bool isAuto, long? hitTick = null)
    {
        if (player == null)
            return false;
        _playerHit ??= FindByArity(typeof(scrPlayer), "Hit", 2,
                m => m.ReturnType == typeof(bool) && m.GetParameters()[1].ParameterType == typeof(bool))
            ?? FindByArity(typeof(scrPlayer), "Hit", 1,
                m => m.ReturnType == typeof(bool) && m.GetParameters()[0].ParameterType == typeof(bool));
        if (_playerHit == null)
        {
            WarnOnce("PlayerHit", new MissingMethodException("scrPlayer.Hit"));
            return false;
        }

        try
        {
            object[] args = _playerHit.GetParameters().Length == 2
                ? new object[] { hitTick, isAuto }
                : new object[] { isAuto };
            return (bool)_playerHit.Invoke(player, args);
        }
        catch (Exception e)
        {
            WarnOnce("PlayerHit", e);
            return false;
        }
    }

    // ── scrPlayer.Hit 是否自带输入事件触发（r150 异步输入重构起的标志形状）──

    private static bool? _hitFiresInputEvents;

    /// <summary>
    /// true = 本版本的 scrPlayer.Hit 内部已自调 HitInputEvent（r150: scrPlayer.cs:754，
    /// handleAll:false）并自行按 ignoreInput 中止。
    /// 判据用形状而非版本号：Hit 首参为 long?（音频 tick）即异步输入重构后的新 Hit，
    /// 该重构同时引入了"Hit 内自触发输入事件"。游戏再改签名时此处自然重新探测。
    /// </summary>
    internal static bool HitFiresInputEvents
    {
        get
        {
            if (_hitFiresInputEvents.HasValue)
                return _hitFiresInputEvents.Value;
            bool hit = false;
            try
            {
                hit = FindByArity(typeof(scrPlayer), "Hit", 2,
                        m => m.ReturnType == typeof(bool)
                             && m.GetParameters()[0].ParameterType == typeof(long?)) != null;
            }
            catch (Exception e)
            {
                WarnOnce("HitFiresInputEvents", e);
            }
            _hitFiresInputEvents = hit;
            return hit;
        }
    }

    // ── scrPlayer.HitInputEvent(bool, InputEventState[, bool handleAll]) ──

    private static MethodInfo _hitInputEvent;

    internal static bool HitInputEvent(scrPlayer player, bool isAuto, InputEventState state)
    {
        if (player == null)
            return false;
        _hitInputEvent ??= FindByArity(typeof(scrPlayer), "HitInputEvent", 3, null)
            ?? FindByArity(typeof(scrPlayer), "HitInputEvent", 2, null);
        if (_hitInputEvent == null)
        {
            WarnOnce("HitInputEvent", new MissingMethodException("scrPlayer.HitInputEvent"));
            return false;
        }

        try
        {
            // handleAll: false —— 与游戏自身 scrPlayer.Hit 内的调用严格一致
            // （scrPlayer.cs:754 `HitInputEvent(isAuto, InputEventState.Down, handleAll: false)`）。
            // 传 true 会让一次按键触发多个 inputEventFfx 目标，与原版行为分叉
            object[] args = _hitInputEvent.GetParameters().Length == 3
                ? new object[] { isAuto, state, false }
                : new object[] { isAuto, state };
            return (bool)_hitInputEvent.Invoke(player, args);
        }
        catch (Exception e)
        {
            WarnOnce("HitInputEvent", e);
            return false;
        }
    }

    // ── scrHitErrorMeter.AddHit(float, [HitMargin,] float, scrPlanet, scrFloor) ──

    private static MethodInfo _errorMeterAddHit;

    internal static void ErrorMeterAddHit(scrHitErrorMeter meter, float angleDiff, HitMargin margin,
        float marginScale, scrPlanet planet, scrFloor hitFloor)
    {
        if (meter == null)
            return;
        _errorMeterAddHit ??= FindByArity(typeof(scrHitErrorMeter), "AddHit", 5, null)
            ?? FindByArity(typeof(scrHitErrorMeter), "AddHit", 4, null);
        if (_errorMeterAddHit == null)
        {
            WarnOnce("ErrorMeterAddHit", new MissingMethodException("scrHitErrorMeter.AddHit"));
            return;
        }

        try
        {
            object[] args = _errorMeterAddHit.GetParameters().Length == 5
                ? new object[] { angleDiff, margin, marginScale, planet, hitFloor }
                : new object[] { angleDiff, marginScale, planet, hitFloor };
            _errorMeterAddHit.Invoke(meter, args);
        }
        catch (Exception e)
        {
            WarnOnce("ErrorMeterAddHit", e);
        }
    }

    // ── scrMarginTracker.deadTiles（r150 删除）→ GetHits(HitMargin.FailedFloor) ──

    private static FieldInfo _deadTilesField;
    private static bool _deadTilesProbed;

    internal static int DeadTiles(scrMarginTracker tracker)
    {
        if (tracker == null)
            return 0;
        if (!_deadTilesProbed)
        {
            _deadTilesProbed = true;
            try
            {
                _deadTilesField = AccessTools.Field(typeof(scrMarginTracker), "deadTiles");
            }
            catch (Exception e)
            {
                WarnOnce("DeadTiles", e);
            }
        }

        if (_deadTilesField != null)
        {
            try
            {
                return (int)_deadTilesField.GetValue(tracker);
            }
            catch (Exception e)
            {
                WarnOnce("DeadTiles.Read", e);
                return 0;
            }
        }

        return HitMarginCompat.TryResolve("FailedFloor", out var failed) ? tracker.GetHits(failed) : 0;
    }

    // ── 误差条喂数是否已由游戏自身完成（r150 起在 scrPlayer.Hit 内）──

    private static bool? _hasUpdateHitErrorMeter;

    internal static bool HasUpdateHitErrorMeter
    {
        get
        {
            if (_hasUpdateHitErrorMeter.HasValue)
                return _hasUpdateHitErrorMeter.Value;
            bool found = false;
            try
            {
                found = AccessTools.Method(typeof(scrController), "UpdateHitErrorMeter") != null;
            }
            catch (Exception e)
            {
                WarnOnce("HasUpdateHitErrorMeter", e);
            }

            _hasUpdateHitErrorMeter = found;
            return found;
        }
    }
}