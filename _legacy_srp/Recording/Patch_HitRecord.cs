using System;
using UnityEngine;
using SpectreReplay.Patching;
using SpectreReplay.Features.Playback;
using Object = UnityEngine.Object;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 状态轨的采集点：<c>scrPlayer.Hit</c> 的 Prefix。
    ///
    /// 挂 Prefix 而不是 Postfix，理由与 Spectre 完全一致
    /// （Features/Replay/Patch_Recording.cs:26-35）：必须在游戏**改动**状态
    /// 之前取样。Hit 一进来就先把 chosenPlanet 切到下一块
    /// （scrPlayer.cs:761 SwitchChosen），之后再取到的 angle / targetExitAngle
    /// 已经是**下一块砖**的值了。回放时把它写回去，行星会提前半块砖转过去——
    /// 画面上表现为"转快了半格"，而且从此每块砖都错，越滚越偏。
    ///
    /// 覆盖三个游戏版本形状（照 Spectre 的模式③）：
    ///   r141–r149  Hit(bool isAuto)
    ///   r150+       Hit(long? hitTick, bool isAuto)
    ///   本机版本    Hit(long? hitTick, bool isAuto = false)
    ///
    /// 前两条按**参数类型**消歧，第三条兜底。三者共用同一个 Record，
    /// 录制逻辑不分叉。
    /// </summary>
    internal static class Patch_HitRecord
    {
        /// <summary>
        /// r150+ 那条前缀是否已经挂上并执行过。
        ///
        /// 只有精确匹配成功时它才会被调用；它一被调用就置位，
        /// 兜底条据此弃权。用途是防止同一个目标上挂两条前缀
        /// （见下面兜底条的说明）。
        /// </summary>
        private static bool ExactMatchSeen;

        internal static void Reset()
        {
            ExactMatchSeen = false;
        }

        // r150+：Hit(long? hitTick, bool isAuto) —— 异步输入长按命中靠 tick 定位准时。
        //
        // 泛型反引号在特性参数里**必须转义**，否则解析器在 ` 后面就截断
        // （特性参数的词法不允许裸反引号），整条补丁会在特性解析阶段
        // 就抛 FormatException，表现为"编译期就过不去"。
        // 写成 typeof(long?).Name 也不行——特性参数是常量上下文。
        //
        // 另一条：这是**唯一**一条按精确参数类型消歧的。
        // 不能再加"r141–r149 的 Hit(bool)"那条：ResolveTarget 找不到
        // 精确匹配时会**退回第一个同名方法**，而本版本只有一个 Hit
        // （2 参数），所以旧签名那条会解析到它——同一个目标上挂两次
        // 前缀，每次命中采样两遍，状态轨里出现成对重复的记录。
        // 旧版本兼容留给下面的**兜底条**：它不声明任何形参，
        // 与任意签名的 Hit 都能配对。
        [GamePatch(typeof(scrPlayer), "Hit", PatchKind.Prefix, "recording",
            "System.Nullable`1", "System.Boolean")]
        private static bool PrefixNew(scrPlayer __instance, long? hitTick = null,
            bool isAuto = false)
        {
            ExactMatchSeen = true;
            return Record(__instance, isAuto);
        }

        /// <summary>
        /// 兜底：签名与上面那条**不同时**才挂。
        ///
        /// 两条都是 scrPlayer.Hit 的前缀，而本版本只有 2 参数的 Hit，
        /// 所以旧签名那条会解析到它——同一个目标上挂两次前缀，
        /// 每次命中采样两遍，状态轨里出现成对重复的记录。
        /// 两次记录之间只隔几微秒，回放时表现为"同一次命中被判了两遍"。
        ///
        /// 所以兜底条在**精确匹配**成功时主动弃权。ResolveTarget 找不到
        /// 精确匹配会退回第一个同名方法（正是我们要的兼容行为），
        /// 弃权判断放在方法体内——而不是靠"多写一条补丁"——
        /// 才有唯一的一份状态。
        /// </summary>
        [GamePatch(typeof(scrPlayer), "Hit", PatchKind.Prefix, "recording")]
        private static bool PrefixAny(scrPlayer __instance)
        {
            if (ExactMatchSeen)
            {
                return true;     // 精确匹配那条已经在记了：这里必须弃权
            }
            return Record(__instance, ResolveLegacyIsAuto());
        }

        /// <summary>
        /// 旧签名 Hit(bool isAuto) 的 isAuto。
        ///
        /// 兜底条不声明形参（否则绑不上），所以只能从对象上反推。
        /// r141–r149 那个版本里 auto 状态有专用读取点吗——没有，
        /// 只能退到 <c>RDC.auto</c>（玩家开着的 auto 开关）。
        ///
        /// 这对回放**没有影响**：IsAuto 只用于回放时还原 auto 标志，
        /// 而回放期间 auto 本来就被状态轨接管；判错一帧的表现只是
        /// auto 砖上的图标闪一下。比起让整条补丁绑不上，这个代价值得付。
        /// </summary>
        private static bool ResolveLegacyIsAuto() => RDC.auto;

        /// <summary>
        /// 采一次命中状态。返回 false 表示**不拦下**原方法。
        ///
        /// **永远返回 true。** 前缀若返回 false 会跳过整个 Hit——
        /// 那等于"把游戏自己的命中掐掉"，是最坏的结果。这里的每个早退
        /// （没在录、不是玩家一、场景不对）都只该"什么都不记"，不该拦。
        ///
        /// **HitTick 刻意不记。** 它是 SkyHook 的绝对 tick，换算歌曲时间要减
        /// <c>AsyncInputManager.offsetTick</c>——那是**本次运行**启动时校准的
        /// 锚点，每局都不同。记下来在回放局换算会得到完全错位的时间差，
        /// 判定算不出数、回放被 noFail 兜住失败、行星原地打转（"卡输入"）。
        /// Spectre 同样只在文件里保留它做元数据、回放时**绝不透传**
        /// （ReplayPlayer.cs:336-343 的注释就是在讲这件事）。
        /// 角度本身已经由 AdjustAngle 精调过，精度信息没丢。
        /// </summary>
        private static bool Record(scrPlayer __instance, bool isAuto)
        {
            try
            {
                // ── 双轨互锁（偏差 1）─────────────────────────────
                //
                // **必须第一个判，不能放在 IsRecording 之后。**
                // 回放时录制是关的（RecordingOn 没开、或已收尾），
                // 所以 IsRecording 为 false 是**常态**——把互锁放在它后面，
                // 互锁就永远走不到，键轨触发的 Hit 一次都掐不掉，
                // 双判照旧发生。
                //
                // 状态轨回放期间，键轨注入的按键会让游戏自己去调 scrPlayer.Hit。
                // 那次命中必须掐掉：它已经由状态轨用录制数据主动复现过了
                // （HitStateReplayer.ReplayHit），再让游戏自己判一次就是
                // 同一块砖判两次——判定条飘两次字、行星跳两格。
                //
                // 返回 false = 跳过原方法，正是这里要的语义。
                // 这是整个文件里**唯一**返回 false 的地方。
                if (PlaybackEngine.HitLocked)
                {
                    return false;
                }
                if (!KeyRecorder.IsRecording)
                {
                    return true;
                }
                var controller = scrController.instance;
                if (controller == null || __instance == null)
                {
                    return true;
                }
                // 只记玩家一：coop 下另一个玩家实例没有 chosenPlanet 这套状态。
                if (__instance != controller.playerOne)
                {
                    return true;
                }
                var planet = controller.chosenPlanet;
                if (planet == null)
                {
                    return true;
                }
                var system = __instance.planetarySystem;
                var failBar = __instance.failBar;
                var hc = new ReplayHitContext
                {
                    Angle = AngleOf(controller),
                    CachedAngle = planet.angle,
                    TargetExitAngle = planet.targetExitAngle,
                    NoFailHit = controller.noFailInfiniteMargin,
                    IsAuto = isAuto,
                    NextFloorAuto = planet.currfloor != null
                        && planet.currfloor.nextfloor != (Object)null
                        && planet.currfloor.nextfloor.auto,
                    RdcAuto = RDC.auto,
                    MidspinInfiniteMargin = __instance.midspinInfiniteMargin,
                    OverloadCounter = failBar != null ? failBar.overloadCounter : 0f,
                    CurrentFloorId = controller.currFloor != null
                        ? controller.currFloor.seqID : -1,
                    FreeRoamSection = controller.curFreeRoamSection,
                };
                KeyRecorder.RecordHit(hc);
                return true;
            }
            catch (Exception ex)
            {
                // 采集失败不能掐掉游戏自己的命中，也不能终止录制。
                Debug.LogError("[SpectreReplay] 状态轨采集异常：" + ex);
                return true;
            }
        }

        /// <summary>
        /// 命中瞬间的"相对角度"，判定的唯一输入。
        ///
        /// 照 Spectre 的 SpectreState.GetAngle()（SpectreState.cs:218-226）：
        ///   angle - targetExitAngle，再按轨道方向取正负。
        ///
        /// 用它而不是绝对角度：绝对角度是当前砖的 0~360 局部量，
        /// 一旦回放时行星没转到同一块砖，差值毫无意义；而这个相对量
        /// 正是游戏算判定窗口时用的那个量（scrPlayer.cs:474）。
        ///
        /// 静态 instance 可能为 null（场景卸载/重载的中间态），
        /// 所以入参优先用 __instance 拿——不对，这里两处都需要 controller：
        /// chosenPlanet 与 planetarySystem 都要。判空返回 0，录制继续。
        /// </summary>
        internal static float AngleOf(scrController controller)
        {
            var planet = controller.chosenPlanet;
            if (planet == null)
            {
                return 0f;
            }
            float a = (float)(planet.angle - planet.targetExitAngle);
            var system = controller.playerOne != null
                ? controller.playerOne.planetarySystem : null;
            if (system != null && !system.isCW)
            {
                a = -a;
            }
            return a;
        }
    }
}
