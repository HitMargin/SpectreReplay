using System;
using System.Collections.Generic;
using UnityEngine;
using SpectreReplay.Ui;
using SpectreReplay.Patching;
using SpectreReplay.Features.Recording;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 状态轨回放：把录制时命中的内部状态**原样写回**游戏。
    ///
    /// 照 Spectre 的 Features/Replay/ReplayPlayer.cs:304-361（ReplayHit）
    /// 与 Features/Replay/Patch_Playback.cs:99-161（驱动循环）。
    ///
    /// **为什么必须有它（偏差 1）。** 键轨把按键喂回游戏，走的是和真人
    /// 完全一样的判定链路。但判定结果不保证相同：游戏按**当帧的行星角度**
    /// 算判定窗口，而回放时注入的按键落在录制容差之外是常态
    /// （量化到一帧 + SendInput 有一帧延迟）。一旦判成 Fail/Overload，
    /// 行星停在原地不前进——用户看到的就是「回放卡住了」，而且**没有报错**。
    ///
    /// 状态轨把这些量按录制值覆盖：angle、targetExitAngle、overloadCounter、
    /// midspinInfiniteMargin。结果是判定复现，不依赖注入的按键落在哪。
    ///
    /// **两条轨必须互锁**，这是最容易做错的地方：
    /// 键轨让游戏自然地跑判定，状态轨又强制把状态改回录制值。
    /// 两边同时生效 = 同一块砖被判两次（一次判定条飘字、一次行星跳），
    /// 行星角度也会被反复拉回。Spectre 用 HitLocked 解决：
    /// 状态轨回放期间 HitLocked=true，键轨触发的 scrPlayer.Hit 直接被
    /// 前缀掐掉（Patch_Recording.cs:39 `if (HitLocked) return false;`）。
    /// </summary>
    internal static class HitStateReplayer
    {
        /// <summary>
        /// 状态轨回放期间为真：此时游戏自己触发的 Hit 必须被掐掉，
        /// 由本类独占命中推进。见 <see cref="PlaybackEngine.HitLocked"/>。
        /// </summary>
        private static bool _locked;

        /// <summary>已回放过的命中条数（游标）。</summary>
        internal static int Cursor { get; private set; }

        internal static bool HasTrack => _hits != null && _hits.Count > 0;

        private static List<ReplayHitContext> _hits;

        internal static void Load(ReplayData data)
        {
            _hits = data?.Hits;
            Cursor = 0;
            _locked = false;
        }

        internal static void Reset()
        {
            Cursor = 0;
            _locked = false;
        }

        internal static void Unload()
        {
            _hits = null;
            Cursor = 0;
            _locked = false;
        }

        /// <summary>键轨触发的 scrPlayer.Hit 放行 / 拦截。</summary>
        internal static bool Locked => _locked;

        /// <summary>
        /// 回放期间是否禁止游戏自己的 <c>scrController.UpdateFreeroam</c>。
        /// 纯查询（不缓存）：前缀每帧问一次，判据就是"下一条命中的区段号
        /// 与当前区段号一致"，直接现算比每帧同步一次更不容易走偏——
        /// 不存在"上一帧算的答案留在这一帧"的情况。
        /// </summary>
        internal static bool FreeroamSuppressed
            => ShouldSuppressFreeroam(scrController.instance);

        /// <summary>
        /// <c>scrController.UpdateFreeroam</c> 的前缀。
        ///
        /// ── 为什么必须禁 ─────────────────────────────────────────
        /// 自由漫游区段结束的那一帧，游戏（scrController.cs:2817-2873）会
        /// 一口气做完：淡出整段砖块、给行星挂 <c>DOMove</c>、锁输入、设无敌帧、
        /// 移动相机，然后重写行星状态——
        ///     <c>playerOne.lastHit = …</c>（:2861）
        ///     <c>SetTargetExitAngle / SetSnappedLastAngle / Update_RefreshAngles</c>（:2867-2869）
        ///     <c>TweenCosmeticAngle</c>（:2871）
        ///     <c>gameworld = true</c>（:2872）
        ///     <c>curFreeRoamSection++</c>（:2875）
        ///
        /// **这与状态轨直接冲突。** 状态轨正要按录制数据写回同一批字段
        /// （见 <see cref="ReplayHit"/>），两边同帧各写一次，
        /// 行星角度与相机落到谁的取值上取决于执行顺序——画面随机地
        /// "跳一下"或"倒转一点"。
        ///
        /// 更麻烦的是 <c>gameworld = true</c>：全仓库只有三处赋值
        /// （<c>scnGame</c> / <c>scnEditor</c> 进关时，和这里），而它的读点
        /// 遍布判定条、失败条、暂停菜单、社交按钮、相机跟随——
        /// 一次误翻会把一大片本该关闭的表现层全打开。这就是此前
        /// "渲染里混进了编辑器状态"那类现象的机制。
        ///
        /// 禁掉整个方法同时也就禁掉了 <c>curFreeRoamSection++</c>——
        /// 那正是我们要的：区段号由**状态轨**按录制数据推进（见
        /// <see cref="Pump"/> 里的 <c>section</c> 对位），游戏自己那份
        /// 是第二个真相源。
        ///
        /// 与 Spectre 的 Patch_UpdateFreeroam 同解（Patch_Misc.cs:100-115），
        /// 门控条件也照抄：回放中且下一条命中的区段号与当前一致时禁掉。
        /// </summary>
        [GamePatch(typeof(scrController), "UpdateFreeroam", PatchKind.Prefix, "playback")]
        private static bool FreeroamPrefix(scrController __instance)
        {
            return !ShouldSuppressFreeroam(__instance);
        }

        private static bool ShouldSuppressFreeroam(scrController controller)
        {
            if (controller == null || _hits == null || _hits.Count == 0)
            {
                return false;
            }
            if (Cursor >= _hits.Count)
            {
                return false;
            }
            try
            {
                return _hits[Cursor].FreeRoamSection == controller.curFreeRoamSection;
            }
            catch
            {
                // 读不到区段号就放行：让游戏跑原逻辑，别因为探针失败
                // 把整个自由漫游段掐掉（那会让砖块永久停在起点）。
                return false;
            }
        }

        /// <summary>
        /// 每帧推进状态轨。由回放泵在**注入按键之后**调用。
        ///
        /// 返回 true 表示"本帧状态轨至少推进了一次"，
        /// 调用方据此判断回放是否已到终点。
        /// </summary>
        internal static bool Pump(scrController controller)
        {
            if (_hits == null || _hits.Count == 0 || controller == null)
            {
                return false;
            }
            // Update 类方法一帧可能被调多次（多个状态机分支都会走到）。
            // 不挡掉的话同一批命中会在一帧内回放多遍——行星连跳。
            if (Time.frameCount == _lastFrame)
            {
                return false;
            }
            _lastFrame = Time.frameCount;

            // 对位：关卡若没停在录像的第 N 块砖上（例如玩家在中途重开），
            // 游标落后就得跳过，否则会在完全错误的位置触发命中。
            // 与 Spectre 的做法一致（Patch_Playback.cs:102-116）：
            // 地板序号或自由漫游区段对不上时，把明显落后的条目丢掉。
            int floor = SafeFloorId(controller);
            int section = controller.curFreeRoamSection;
            if (Cursor < _hits.Count)
            {
                var h = _hits[Cursor];
                if (h.CurrentFloorId != floor || section > h.FreeRoamSection)
                {
                    while (Cursor < _hits.Count
                        && (_hits[Cursor].CurrentFloorId < floor
                            || _hits[Cursor].FreeRoamSection < section))
                    {
                        Cursor++;
                    }
                    if (Cursor >= _hits.Count)
                    {
                        return true;    // 已对齐到末尾，交由调用方判定结束
                    }
                }
            }

            var player = controller.playerOne;
            if (player == null)
            {
                return false;
            }
            var planet = controller.chosenPlanet;
            if (planet == null)
            {
                return false;
            }
            float angle = AngleOf(controller, planet);

            bool advanced = false;
            _locked = true;
            try
            {
                // 同一次录制里密集段可能有多条命中落在同一帧的同一角度区间，
                // 必须一次推完（while 而不是 if）——少推一条，那块砖就少一次
                // 行星切换，画面会停在两块砖的中间态。
                while (Cursor < _hits.Count)
                {
                    var hc = _hits[Cursor];
                    if (hc.CurrentFloorId != floor || hc.FreeRoamSection != section)
                    {
                        break;
                    }
                    if (angle < hc.Angle)
                    {
                        break;      // 还没转到这条命中的角度：等下一帧
                    }
                    ReplayHit(controller, player, hc);
                    advanced = true;
                    Cursor++;
                    if (Cursor >= _hits.Count)
                    {
                        break;
                    }
                    angle = AngleOf(controller, planet);
                }
            }
            finally
            {
                _locked = false;
            }
            return advanced;
        }

        private static int _lastFrame = -1;

        /// <summary>
        /// 相对角度 = angle - targetExitAngle，再按轨道方向取正负。
        /// 照 Spectre 的 SpectreState.GetAngle()（SpectreState.cs:218-226）。
        /// 必须是"当前值"而非录制值——这里问的是"游戏此刻转到哪了"。
        /// </summary>
        internal static float AngleOf(scrController controller, scrPlanet planet)
        {
            float a = (float)(planet.angle - planet.targetExitAngle);
            var system = controller.playerOne != null
                ? controller.playerOne.planetarySystem : null;
            if (system != null && !system.isCW)
            {
                a = -a;
            }
            return a;
        }

        private static int SafeFloorId(scrController controller)
        {
            try
            {
                var f = controller.currFloor;
                return f != null ? f.seqID : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 把一次录制的命中状态写回游戏，并真正推进行星。
        ///
        /// 照 Spectre 的 ReplayHit（ReplayPlayer.cs:304-361），但**去掉
        /// 两条 Spectre 有而我们没有的**：
        ///
        /// ① **不透传 HitTick。** 那是 SkyHook 的绝对 tick，换算要减本次
        ///    运行启动时校准的 <c>AsyncInputManager.offsetTick</c>，每局不同。
        ///    拿录制局的 tick 在回放局换算会得到完全错位的时间差，
        ///    判定算不出数 → 被 noFail 兜住 → 行星原地打转（"卡输入"）。
        ///    我们录制时压根不记它（见 Patch_HitRecord 的说明），传 null，
        ///    让游戏走 <c>GetHitMarginInDeg</c> 的角度分支——角度里已经
        ///    含 AdjustAngle 的精确度。
        ///
        /// ② **不走 ReplayHitOld 手工复刻整条链路**，而是直接调
        ///    <c>scrPlayer.Hit</c>。老式复刻要自己调 SwitchChosen、喂判定条、
        ///    管相机脉冲，任何一处抄漏就与真机表现分叉；而直接调 Hit 让
        ///    **游戏自己的代码**推进行星，跨版本自动成立。
        ///
        /// 顺序照 Spectre：先把录制状态覆盖上去，再调 Hit。
        /// </summary>
        private static bool ReplayHit(scrController controller, scrPlayer player,
            ReplayHitContext hc)
        {
            var planet = controller.chosenPlanet;
            if (planet == null)
            {
                return false;
            }
            // ① 覆盖录制时的内部状态
            player.consecMultipressCounter = 0;
            controller.multipressPenalty = false;
            controller.noFailInfiniteMargin = hc.NoFailHit;
            planet.SetTargetExitAngle(hc.TargetExitAngle);
            planet.angle = hc.CachedAngle;
            player.midspinInfiniteMargin = hc.MidspinInfiniteMargin;
            if (player.failBar != null)
            {
                player.failBar.overloadCounter = hc.OverloadCounter;
            }
            player.responsive = true;
            RDC.auto = hc.IsAuto;
            if (hc.NoFailHit)
            {
                // 录制时这一砖是 noFail 命中的，屏幕上有一个 miss 指示器在闪。
                // 必须复现它，否则成片里少了那个闪烁。
                //
                // 而**别的时候**必须屏蔽 MarkFail（PlaybackClamps 的 Prefix）：
                // 它无条件 Instantiate 指示器并挂在 next 行星上。回放期间若有
                // 真人误触，屏幕上会多出一个红色指示器，并跟着行星一路飞出
                // 画面——成片里就是一团跟着跑的红色鬼影。
                //
                // 所以"开屏蔽 → 调 → 关屏蔽"必须成对，且用 try/finally：
                // BlinkForSeconds 之后若还有代码抛异常，标志会永久留在 true，
                // 之后玩家整局都看不到 miss 指示器，且无从察觉。
                PlaybackClamps.MarkFailScope(true, () =>
                {
                    var ind = planet.MarkFail();
                    if (ind != null)
                    {
                        ind.BlinkForSeconds(3f);
                    }
                });
            }
            // ② 让游戏自己走命中链路推进行星。
            //
            // **传 hitTick: null**，理由见方法说明 ①。
            //
            // _locked 此刻为 true，Hit 的前缀不会把它掐掉——本次调用
            // 是我们主动发起的，必须真的执行。
            bool result = CallHit(player, hc.IsAuto);
            // ③ auto 标志按录制数据收尾（照 Spectre ReplayPlayer.cs:345-352）
            if (!hc.NextFloorAuto && hc.RdcAuto)
            {
                RDC.auto = true;
            }
            else
            {
                RDC.auto = false;
            }
            controller.noFailInfiniteMargin = false;
            return result;
        }

        /// <summary>
        /// 调 <c>scrPlayer.Hit</c>，委托给 <see cref="GameCompat.PlayerHit"/>。
        ///
        /// 这里刻意**不自己缓存 MethodInfo**：形状探测是"先找新版形状、
        /// 找不到退旧版"，它的逻辑有 5 处分支，复制一份必然与兼容层走偏
        /// （上一轮就是这样：兼容层和这里各有一份 FindHitMethod）。
        /// 探测只跑一次（结果存在兼容层的静态字段里），调用本身才是热路径。
        ///
        /// **hitTick 恒为 null**，理由见 <see cref="ReplayHit"/> 的方法说明 ①。
        /// </summary>
        private static bool CallHit(scrPlayer player, bool isAuto)
            => GameCompat.PlayerHit(player, isAuto, null);
    }
}
