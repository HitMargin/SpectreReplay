using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SpectreReplay.Patching
{
    /// <summary>
    /// 游戏 API 形状差异的运行期桥（对照文档偏差 15）。
    ///
    /// SpectreReplay 编译时只引用一份 Assembly-CSharp（r150），凡是 r150 改了
    /// 签名的成员，**直接调用会编译通过、在旧版本运行期抛 MissingMethodException**。
    /// 补丁特性上的 MinVersion/MaxVersion 只管"补丁挂不挂"，管不了"我怎么调"——
    /// 那必须在这里按**参数形状**择路。
    ///
    /// **本类不做版本号判断**，一律"先找新版形状，找不到退旧版形状"。
    /// 理由照 Spectre 的 GameCompat 头注（GameCompat.cs:17-18）：游戏再改一次
    /// 签名时降级路径依然可用。而版本号判断有个更硬的坑——
    /// 探测不到就是 0，0 会让所有 `rel >= 150` 的分支被误判为"旧版"，
    /// 于是**升级之后**反而走进旧路径。
    ///
    /// 但**形状探测也必须配一条版本兜底**：游戏若把某个成员整个改名，
    /// 形状探测会返回"都不像"（同样落到旧路径）。这时用真实版本号判断
    /// 哪条路径才对。见 <see cref="GameVersion"/>。
    /// </summary>
    internal static class GameCompat
    {
        private const BindingFlags AnyMethod =
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        private static readonly HashSet<string> Warned =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 每条兼容通道只警告一次。
        ///
        /// 这些通道都在**每帧**的热点路径上（命中、判定）。异常每帧抛一次的话，
        /// UMM 的日志窗口会被刷成一片，真正有用的首条信息反而看不见。
        /// </summary>
        private static void WarnOnce(string key, Exception e)
        {
            if (Warned.Add(key))
            {
                Debug.LogWarning("[SpectreReplay] 兼容层 " + key + " 失败，已降级："
                    + e.GetType().Name + " " + e.Message);
            }
        }

        // ── scrPlayer.Hit(bool) → Hit(long? hitTick, bool isAuto) ──
        //
        // 唯一**真的**会打到我们身上的形状变更（实测 r150 只有 2 参数那个重载）。
        //
        // 为什么必须按形状而不是直接调：SwitchChosen 的 hitTick 参数是 r150
        // 跟着一起加的，而 Hit 内部会把它传给 SwitchChosen。旧版上
        // `player.Hit(null, false)` 直接编译期就不成立（旧签名没有 2 参）。
        // 写成反射调用则在两边都成立，且游戏再改签名时只需要在这里加一条分支。

        private static MethodInfo _playerHit;

        /// <param name="hitTick">
        /// r150+ 异步输入的音频 tick；旧版忽略（无该形参）。
        /// **传 null 即让游戏按当前角度自行推算**，与老版语义一致。
        /// </param>
        internal static bool PlayerHit(scrPlayer player, bool isAuto, long? hitTick = null)
        {
            if (player == null)
            {
                return false;
            }
            _playerHit ??= FindByArity(typeof(scrPlayer), "Hit", 2,
                    m => m.ReturnType == typeof(bool)
                         && m.GetParameters()[1].ParameterType == typeof(bool))
                ?? FindByArity(typeof(scrPlayer), "Hit", 1,
                    m => m.ReturnType == typeof(bool)
                         && m.GetParameters()[0].ParameterType == typeof(bool));
            if (_playerHit == null)
            {
                // 只在首次失败时报一次：这条通道失效的表现是"状态轨推不动行星"，
                // 玩家看到的是"回放卡住"，完全不会联想到版本不匹配。
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

        /// <summary>
        /// 本版本的 <c>scrPlayer.Hit</c> 是否**自带**输入事件触发。
        ///
        /// r150 的重构把 <c>HitInputEvent</c> 挪进了 <c>Hit</c> 内部
        /// （scrPlayer.cs:754，<c>handleAll: false</c>）并自行按 ignoreInput 中止。
        /// 旧版上这一步由 Spectre 的录制前缀代发。
        ///
        /// **判据用形状而非版本号**（同 Spectre GameCompat.cs:258-259）：首参为
        /// <c>long?</c> 的重载即异步输入重构后的新 Hit，该重构同时引入了
        /// "Hit 内自触发输入事件"。游戏再改签名时此处自然重新探测。
        ///
        /// 我们目前**没有代发 HitInputEvent 的代码**（不做自定义判定改写），
        /// 但留着这个判据：将来若加，必须先问它 true 还是 false，否则会双触发
        /// （一次按键 Any+Action1 各发一次，Spectre 的 Patch_Recording.cs:42-48
        /// 详述了那个机制）。
        /// </summary>
        internal static bool HitFiresInputEvents
        {
            get
            {
                try
                {
                    return FindByArity(typeof(scrPlayer), "Hit", 2,
                        m => m.ReturnType == typeof(bool)
                             && m.GetParameters()[0].ParameterType == typeof(long?)) != null;
                }
                catch (Exception e)
                {
                    WarnOnce("HitFiresInputEvents", e);
                    return false;
                }
            }
        }

        // ── scrPlanet.SwitchChosen() → SwitchChosen(long? hitTick) ──
        //
        // 我们不直接调它（由游戏自己的 Hit 内部调，scrPlayer.cs:761），
        // 但保留通道：状态轨若将来需要绕过 Hit 单独切行星就会用到。
        // 直接写编译期调用在旧版上是 MissingMethodException。

        private static MethodInfo _switchChosen;

        internal static scrPlanet SwitchChosen(scrPlanet planet)
        {
            if (planet == null)
            {
                return null;
            }
            _switchChosen ??= FindByArity(typeof(scrPlanet), "SwitchChosen", 0, null)
                ?? FindByArity(typeof(scrPlanet), "SwitchChosen", 1,
                    m => m.ReturnType == typeof(scrPlanet));
            if (_switchChosen == null)
            {
                WarnOnce("SwitchChosen", new MissingMethodException("scrPlanet.SwitchChosen"));
                return planet;
            }
            try
            {
                object[] args = _switchChosen.GetParameters().Length == 0
                    ? null : new object[] { null };
                return (scrPlanet)_switchChosen.Invoke(planet, args);
            }
            catch (Exception e)
            {
                WarnOnce("SwitchChosen", e);
                return planet;
            }
        }

        // ── scrPlayer.HitInputEvent(bool, InputEventState[, bool handleAll]) ──

        private static MethodInfo _hitInputEvent;

        /// <param name="handleAll">
        /// 传 false ——与游戏自身 scrPlayer.Hit 内的调用严格一致
        /// （scrPlayer.cs:754）。传 true 会让一次按键触发多个 inputEventFfx 目标，
        /// 与原版行为分叉。
        /// </param>
        internal static bool HitInputEvent(scrPlayer player, bool isAuto,
            InputEventState state)
        {
            if (player == null)
            {
                return false;
            }
            _hitInputEvent ??= FindByArity(typeof(scrPlayer), "HitInputEvent", 3, null)
                ?? FindByArity(typeof(scrPlayer), "HitInputEvent", 2, null);
            if (_hitInputEvent == null)
            {
                WarnOnce("HitInputEvent", new MissingMethodException("scrPlayer.HitInputEvent"));
                return false;
            }
            try
            {
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

        // ── 误差条喂数是否已由游戏自身完成（r150 起在 scrPlayer.Hit 内）──
        //
        // 我们不手动喂判定条，所以这条只用于**自检**：若本版本由游戏喂，
        // 而将来有人照着老版路径补了一次，就会双记（同一砖两条 tick）。
        // 判据是成员是否**存在**（形状），不判版本号。

        private static bool? _hasUpdateHitErrorMeter;

        internal static bool HasUpdateHitErrorMeter
        {
            get
            {
                if (_hasUpdateHitErrorMeter.HasValue)
                {
                    return _hasUpdateHitErrorMeter.Value;
                }
                bool found;
                try
                {
                    found = FindByArity(typeof(scrController), "UpdateHitErrorMeter", 3, null) != null;
                }
                catch (Exception e)
                {
                    WarnOnce("HasUpdateHitErrorMeter", e);
                    found = false;
                }
                _hasUpdateHitErrorMeter = found;
                return found;
            }
        }

        private static MethodInfo FindByArity(Type type, string name, int arity,
            Func<MethodInfo, bool> accept)
        {
            try
            {
                foreach (MethodInfo m in type.GetMethods(AnyMethod))
                {
                    if (m.Name != name)
                    {
                        continue;
                    }
                    if (m.GetParameters().Length != arity)
                    {
                        continue;
                    }
                    if (accept != null && !accept(m))
                    {
                        continue;
                    }
                    return m;
                }
            }
            catch (Exception e)
            {
                WarnOnce(name + "@" + type.Name, e);
            }
            return null;
        }
    }

    /// <summary>
    /// 游戏版本号探测。
    ///
    /// **为什么不能只靠形状探测。** 形状探测的覆盖范围是"签名改了但名字没改"。
    /// 游戏若把某个成员整个改名（例如把 <c>scrMarginTracker.deadTiles</c> 删掉
    /// 换成别的东西），形状探测会返回"都不像"，于是静默走进旧路径——
    /// 而旧路径拿到的值是**错的**，不是"没有"。这比抛异常危险得多。
    /// 版本号是唯一的独立信号：它能回答"我是不是在 150 上"。
    ///
    /// **探测源与 Spectre 不同，这里按实测修正。** Spectre 的
    /// JALibMissing.cs:30 按 <c>{"GCNS", "Releases"}</c> 顺序找
    /// <c>releaseNumber</c>。实测本版本 Assembly-CSharp 里：
    ///   · <c>GCNS</c> 存在，但**只有场景名/分支列表**，没有任何版本字段；
    ///   · <c>Releases.releaseNumber</c> 才是真版本（Int32）。
    /// 照 Spectre 的顺序取，第一个命中的类型就是 GCNS，找不到字段、
    /// 继续找 Releases——结果**恰好**也能拿到，属于巧合性正确。
    /// 但 GCNS 若哪天加了个同名字段就会取错，所以这里显式按
    /// "逐个类型找**名为 releaseNumber 的 Int32 字段**"来写，
    /// 语义明确、不依赖"第一个存在的类型里恰好没有这个字段"。
    ///
    /// 探测失败返回 -1，**不返回 0**：0 会被 <c>rel &gt;= 150</c> 判成"旧版"，
    /// 于是升级之后反而走旧路径——那是最坏的失败方向。
    /// </summary>
    internal static class GameVersion
    {
        private const int Unknown = -1;

        internal static int ReleaseNumber { get; } = Probe();

        /// <summary>是否已确认在 r150 或更新。</summary>
        internal static bool Is150OrNewer => ReleaseNumber >= 150;

        /// <summary>版本未知（探测失败）。此时应当**按形状择路**，别按版本猜。</summary>
        internal static bool IsUnknown => ReleaseNumber == Unknown;

        private static int Probe()
        {
            try
            {
                Assembly game = typeof(ADOBase).Assembly;
                // 顺序有意义：先找"专门放版本号"的那个类型。
                // 不写成"第一个存在的类型"——GCNS 在 r150 就存在却不含版本字段。
                foreach (string typeName in new[] { "Releases", "GCNS" })
                {
                    Type type = game.GetType(typeName, false);
                    if (type == null)
                    {
                        continue;
                    }
                    FieldInfo field = type.GetField("releaseNumber",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null && field.FieldType == typeof(int))
                    {
                        return Convert.ToInt32(field.GetValue(null));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 游戏版本探测失败（将按成员形状择路）："
                    + ex.Message);
            }
            return Unknown;
        }

        /// <summary>
        /// 启动日志里的一行，便于玩家/排查确认探测结果。
        ///
        /// **只流向 Player.log / UMM 窗口**（由 Main 拼进 mod.Logger.Log），
        /// 所以不需要走 Loc 文案表——与 Main.cs、PatchLoader.cs 同类。
        /// </summary>
        internal static string Describe()
            => IsUnknown
                ? "游戏版本未知（按成员形状择路）"
                : "游戏 r" + ReleaseNumber;
    }
}
