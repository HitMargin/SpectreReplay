using System;
using UnityEngine;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 进关/出关的检测器。
    ///
    /// 为什么不用"钩某个方法"：首次进入关卡这条路径在游戏不同版本里落点不同
    /// （编辑器里点开始、从主菜单进、试玩…），钩错地方就完全不触发。
    /// 而"现在正在玩一首歌"这个状态本身是稳定的——由游戏自己的状态机
    /// 加上关卡对象的存在性共同确定。
    ///
    /// ── 上一版的判据为什么会误报 ──────────────────────────────
    /// 原来每帧比对 (gameworld, conductor.song != null, playerOne != null)，
    /// 三条全真才算"在关卡里"。这三条都会在**关卡加载的中间态**短暂失效：
    ///   · scrController.playerOne 是 `ADOBase.playerManager.players[0]`
    ///     ——索引属性，players 还是空表时直接抛 ArgumentOutOfRange；
    ///   · gameworld 在场景切换过程中会翻回 false；
    ///   · conductor.song 在换谱时会短暂为 null。
    /// 于是一帧的抖动就被当成"掉出关卡"，把刚开始的录制和渲染立刻收尾。
    /// 现象是"刚点开始播放就显示渲染完成"——实测只录到 27 帧 / 3 秒。
    ///
    /// 现在改成：只有**连续多帧都探测不到**才认定掉出关卡，并且
    /// 探测本身整体包在 try/catch 里——判据用状态机 + 关卡存在性，
    /// 不再碰可能抛异常的索引属性。
    /// </summary>
    internal sealed class LevelWatcher : MonoBehaviour
    {
        /// <summary>
        /// 连续多少帧探测不到才算"真的掉出关卡"。
        ///
        /// 场景切换会持续十几帧到几十帧，中间态不可能只有两三帧；
        /// 用 5 帧（约 80ms）的迟滞把抖动滤掉，又不至于让真退出拖很久。
        /// </summary>
        private const int DropFrames = 5;

        private int _absent;
        private bool _wasInLevel;

        internal static void Install()
        {
            var go = new GameObject("SpectreReplayLevelWatcher");
            DontDestroyOnLoad(go);
            go.AddComponent<LevelWatcher>();
        }

        private void Update()
        {
            if (ProbeLevel())
            {
                _absent = 0;
                if (_wasInLevel)
                {
                    return;
                }
                _wasInLevel = true;
                Patch_Recording.BeginIfEnabled();
                return;
            }

            // 迟滞：单帧探测不到不算数
            _absent++;
            if (_absent < DropFrames || !_wasInLevel)
            {
                return;
            }
            _wasInLevel = false;
            _absent = 0;
            FinishCurrentLevel();
        }

        /// <summary>结束本次游玩：录制落盘、渲染收尾。两条独立判据。</summary>
        private static void FinishCurrentLevel()
        {
            // 录制没在进行就别动它——End() 内部已判空，这里只是顺序：
            // 先问录制，再问渲染，任一为真都该收尾，两个都真也都收。
            bool wasRecording = KeyRecorder.IsRecording;
            if (wasRecording)
            {
                KeyRecorder.End(Settings.AutoSave);
            }
            // 渲染与录制是两条独立的生命周期：玩家关掉"启用录制"但开着
            // "启用渲染"时，视频也必须收尾，否则临时文件永远留着。
            if (VideoRender.LiveSession.IsRunning)
            {
                VideoRender.RenderService.OnLevelEnd();
            }
        }

        /// <summary>
        /// 现在是否在一局里。
        ///
        /// 判据：**游戏自己的状态机进入了与游玩相关的状态**，且关卡对象还在。
        /// 不用 <c>scrController.playerOne</c>——那是索引属性
        /// （players[0]），加载中间态会抛异常；也不用 gameworld 单独作数，
        /// 它在场景切换过程中会翻。
        ///
        /// 整体 try/catch：这一段每帧执行，抛一次就是一次每帧异常。
        /// 中间态探测不到就返回 false，交给迟滞去滤。
        /// </summary>
        private static bool ProbeLevel()
        {
            try
            {
                var controller = scrController.instance;
                if (controller == null || !controller.gameworld)
                {
                    return false;
                }
                var conductor = scrConductor.instance;
                if (conductor == null || conductor.song == null)
                {
                    return false;
                }
                // 状态机在 Start/Countdown/Checkpoint/PlayerControl/Fail/Won
                // 之间都算"在一局里"；只有 None 才是真的不在。
                // 玩家还没按下开始键时状态是 Start，也应当算进来——
                // 否则"等倒计时"的这段完全不录，而玩家以为已经在录了。
                //
                // 读 currentState 字段而不是 stateMachine.GetState()：
                // 前者是 States 强类型、且由游戏自己在 Update 开头每帧刷新
                // （scrController.cs:1675），拿它是快照而不是再次驱动状态机。
                // GetState() 返回的是 System.Enum，要靠强转才能比。
                if (controller.currentState == States.None)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                // 加载中间态：算作"探测不到"，不抛给游戏主循环
                return false;
            }
        }
    }
}