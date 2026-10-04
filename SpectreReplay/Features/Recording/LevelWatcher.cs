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
        /// 判据：**当前场景是游玩场景**（scnGame / scnEditor），且歌曲已在播。
        ///
        /// ── 为什么必须看场景名 ─────────────────────────────────
        /// 前两版都栽在"找一个表示在关卡里的布尔量"上：
        ///   · <c>gameworld</c> 只在 <b>玩家首次命中之后</b>才置 true
        ///     （scrController.cs:2872，位于 UpdateFreeroam 的尾部）。
        ///     拿它当判据 = 丢掉开局那一大段，倒计时期间压根不录。
        ///   · <c>currentState</c> 由 scrController.Update（:1675）赋值，
        ///     而 Start 状态要等玩家按下开始键才切过去。拿它当判据 =
        ///     整局都不录（上一版就是这么把功能改废的）。
        ///   · <c>playerOne</c> 是 players[0] 索引属性，空表时抛异常。
        ///
        /// 这些字段都有共同点：**它们描述的是"玩到哪一步"，
        /// 而不是"在不在关卡里"。** 场景名才回答后者。
        /// Spectre 也是这么判的（Patch_Lifecycle.cs:80-99 直接读
        /// SceneManager.GetActiveScene().name）。
        ///
        /// 整体 try/catch：这一段每帧执行，抛一次就是一次每帧异常。
        /// </summary>
        private static bool ProbeLevel()
        {
            try
            {
                // 场景判断先行：不在游玩场景就直接走，连 conductor 都不用碰
                string scene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
                if (scene != GCNS.sceneGame && scene != GCNS.sceneEditor)
                {
                    return false;
                }
                var conductor = scrConductor.instance;
                if (conductor == null || conductor.song == null)
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