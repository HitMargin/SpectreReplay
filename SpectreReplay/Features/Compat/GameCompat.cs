using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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

    // ── scrMisc.GetAdjustedAngleBoundaryInDeg ──
    //
    // r150：返回嵌在 scrMisc 里的泛型结构体（.Perfect/.Pure 是公开字段），参数是 Difficulty
    // r149-：返回 double，参数是 HitMarginGeneral，每个判定档单独调一次
    //
    // <para><b>为什么这里必须用表达式树，而不是直接写类型名 + CreateDelegate。</b>
    // r150 那个返回值类型在 r147/r148 的程序集里**根本不存在**。源码里只要出现一次
    // 这个类型名，用旧程序集编译就是 CS0426——而「同一份源码能被 r147/r148/r150 三份
    // 程序集编译通过」正是本轮兼容性唯一可静态验证的手段，绝不能为了写法好看把它弄丢。
    // 表达式树把类型留在运行期（MethodInfo + 字段名），源码只出现两版都有的
    // Difficulty 与 double；调用期仍是一次普通方法调用加一次字段读。</para>
    //
    // <para><b>为什么不能退回 MethodInfo.Invoke。</b>调用点（ReplayAnalyzer.Analyze）
    // 在「每次命中」的循环里，而它又被每帧的 DrawTimingSummary 调用；一份 2000 击的
    // 回放在 60fps 下就是 12 万次/秒，Invoke 的装箱会把界面直接拖垮。</para>

    private static Func<Difficulty, double, double, double, double> _angleBoundaryNewPerfect;
    private static Func<Difficulty, double, double, double, double> _angleBoundaryNewPure;

    private static Func<HitMarginGeneral, double, double, double, double> _angleBoundaryOld;
    private static bool _angleBoundaryProbed;

    /// <summary>
    /// 取该上下文下原版 Perfect / Pure 的**角度**边界（度）。
    /// 返回 false 表示两种形状都没解析出来；调用方应把窗口按 0 填入，
    /// 散点图会据此退回 25/60ms 的兜底估算（HitScatterMath.Windows）。
    /// </summary>
    internal static bool AngleBoundary(Difficulty difficulty, double bpmTimesSpeed,
        double conductorPitch, double marginMult, out double perfect, out double pure)
    {
        perfect = 0.0;
        pure = 0.0;

        if (!_angleBoundaryProbed)
        {
            _angleBoundaryProbed = true;
            try
            {
                MethodInfo byDifficulty = FindByArity(typeof(scrMisc), "GetAdjustedAngleBoundaryInDeg", 4,
                    m => m.GetParameters()[0].ParameterType == typeof(Difficulty));
                if (byDifficulty != null)
                {
                    _angleBoundaryNewPerfect = CompileStructFieldGetter(byDifficulty, "Perfect");
                    _angleBoundaryNewPure = CompileStructFieldGetter(byDifficulty, "Pure");
                }
                else
                {
                    MethodInfo byMargin = FindByArity(typeof(scrMisc), "GetAdjustedAngleBoundaryInDeg", 4,
                        m => m.GetParameters()[0].ParameterType == typeof(HitMarginGeneral));
                    if (byMargin != null)
                    {
                        _angleBoundaryOld = (Func<HitMarginGeneral, double, double, double, double>)
                            Delegate.CreateDelegate(
                                typeof(Func<HitMarginGeneral, double, double, double, double>), byMargin);
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce("AngleBoundary.Resolve", e);
            }

            if (_angleBoundaryNewPerfect == null && _angleBoundaryOld == null)
                WarnOnce("AngleBoundary", new MissingMethodException("scrMisc.GetAdjustedAngleBoundaryInDeg"));
        }

        try
        {
            if (_angleBoundaryNewPerfect != null && _angleBoundaryNewPure != null)
            {
                perfect = _angleBoundaryNewPerfect(difficulty, bpmTimesSpeed, conductorPitch, marginMult);
                pure = _angleBoundaryNewPure(difficulty, bpmTimesSpeed, conductorPitch, marginMult);
            }
            else if (_angleBoundaryOld != null)
            {
                perfect = _angleBoundaryOld(HitMarginGeneral.Perfect, bpmTimesSpeed, conductorPitch, marginMult);
                pure = _angleBoundaryOld(HitMarginGeneral.Pure, bpmTimesSpeed, conductorPitch, marginMult);
            }
        }
        catch (Exception e)
        {
            WarnOnce("AngleBoundary", e);
            perfect = 0.0;
            pure = 0.0;
        }

        return perfect > 0.0 && pure > 0.0;
    }

    /// <summary>
    /// 把「调用 <paramref name="method"/> → 读返回值上的某个 double 字段」编译成委托。
    ///
    /// <para>方法体里只经由 <see cref="MethodInfo"/> 与字段名访问，源码不出现返回类型名，
    /// 因此不含该类型的旧程序集也能编译通过——这是本兼容层的可验证性前提。
    /// 展开后的委托等价于一次普通调用加一次字段读，可以放在每帧几十万次的循环里。</para>
    /// </summary>
    private static Func<Difficulty, double, double, double, double> CompileStructFieldGetter(
        MethodInfo method, string fieldName)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 4
            || parameters[0].ParameterType != typeof(Difficulty)
            || parameters[1].ParameterType != typeof(double)
            || parameters[2].ParameterType != typeof(double)
            || parameters[3].ParameterType != typeof(double))
        {
            return null;
        }

        ParameterExpression difficulty = Expression.Parameter(typeof(Difficulty), "difficulty");
        ParameterExpression bpmTimesSpeed = Expression.Parameter(typeof(double), "bpmTimesSpeed");
        ParameterExpression conductorPitch = Expression.Parameter(typeof(double), "conductorPitch");
        ParameterExpression marginMult = Expression.Parameter(typeof(double), "marginMult");

        Expression body = Expression.Field(
            Expression.Call(method, difficulty, bpmTimesSpeed, conductorPitch, marginMult),
            fieldName);

        return Expression.Lambda<Func<Difficulty, double, double, double, double>>(
            body, difficulty, bpmTimesSpeed, conductorPitch, marginMult).Compile();
    }

    // ── RDInput.GetMainHeldKeys() → GetMainHeldKeys(List<AnyKeyCode>) ──
    //
    // r150 改成「传进来填充」；r149- 是「返回一个新列表」。调用点在
    // scrPlayer.ValidInputWasReleased 的 Postfix 上，每帧一次，同样缓存委托。

    private static Action<List<AnyKeyCode>> _heldKeysFill;
    private static Func<List<AnyKeyCode>> _heldKeysList;
    private static bool _heldKeysProbed;

    /// <summary>
    /// 把「当前按住的主键」追加进 <paramref name="keys"/>。旧版返回的列表直接
    /// AddRange（语义等价于新版的填充）。返回 false 表示形状没解析出来，此时
    /// <paramref name="keys"/> 保持调用前的状态（原样为空）。
    /// </summary>
    internal static bool AppendMainHeldKeys(List<AnyKeyCode> keys)
    {
        if (keys == null)
            return false;

        if (!_heldKeysProbed)
        {
            _heldKeysProbed = true;
            try
            {
                MethodInfo fill = FindByArity(typeof(RDInput), "GetMainHeldKeys", 1,
                    m => m.ReturnType == typeof(void));
                if (fill != null)
                {
                    _heldKeysFill = (Action<List<AnyKeyCode>>)Delegate.CreateDelegate(
                        typeof(Action<List<AnyKeyCode>>), fill);
                }
                else
                {
                    MethodInfo list = FindByArity(typeof(RDInput), "GetMainHeldKeys", 0, null);
                    if (list != null)
                    {
                        _heldKeysList = (Func<List<AnyKeyCode>>)Delegate.CreateDelegate(
                            typeof(Func<List<AnyKeyCode>>), list);
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce("MainHeldKeys.Resolve", e);
            }

            if (_heldKeysFill == null && _heldKeysList == null)
                WarnOnce("MainHeldKeys", new MissingMethodException("RDInput.GetMainHeldKeys"));
        }

        try
        {
            if (_heldKeysFill != null)
            {
                _heldKeysFill(keys);
                return true;
            }

            if (_heldKeysList != null)
            {
                List<AnyKeyCode> got = _heldKeysList();
                if (got != null)
                    keys.AddRange(got);
                return true;
            }
        }
        catch (Exception e)
        {
            WarnOnce("MainHeldKeys", e);
        }

        return false;
    }
}
