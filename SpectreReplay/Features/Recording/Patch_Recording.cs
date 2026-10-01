using System;
using SpectreReplay.Patching;
using SpectreReplay.Features.Playback;
using UnityEngine;
using ADOFAI;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 把采集器接到游戏生命周期上。
    ///
    /// 挂载点与理由：
    ///   scrController.Won_Update      通关——结算时收尾并存盘
    ///   scrController.PlayerControl_Update  游玩中——每帧泵一次队列
    ///   scrMarginTracker.AddHit       判定——记一次判定与时刻
    ///   scrController.Start_Rewind    重开（checkpointNum==0）——丢弃本次数据
    ///
    /// UpdateInput 而不是 PlayerControl_Update 做泵点：Input 在输入子系统处理完
    /// 之后才调用，此时 SkyHook 队列已经被游戏自己取空过一次，我们拿到的是
    /// "本帧之后"的增量，不会和游戏抢同一批事件。
    /// </summary>
    internal static class Patch_Recording
    {
        [GamePatch(typeof(scrController), "Won_Update", PatchKind.Postfix, "recording")]
        private static void WonPostfix()
        {
            try
            {
                KeyRecorder.End(Settings.AutoSave);
                VideoRender.RenderService.OnLevelEnd();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 收尾失败：" + ex);
            }
        }

        [GamePatch(typeof(scrController), "UpdateInput", PatchKind.Postfix, "recording")]
        private static void Pump()
        {
            try
            {
                KeyRecorder.Pump(scrConductor.instance);
            }
            catch (Exception ex)
            {
                // 输入泵每帧都跑，不能让它把异常抛到游戏主循环里
                Debug.LogError("[SpectreReplay] 输入泵异常：" + ex);
            }
        }

        /// <summary>
        /// 重开 = 一次尝试结束。
        ///
        /// 绑参名这件事踩过坑：Harmony 是**按参数名**把补丁方法的形参对到
        /// 原方法形参上的，而这个方法真正的形参是
        /// <c>Start_Rewind(int _currentSeqID = -1)</c>——是"回退到第几个 seqID"，
        /// 不是"第几个检查点"。之前按 checkpointNum 命名，名字对不上直接抛异常，
        /// 补丁挂不上（日志里那条 Start_Rewind（Exception））。
        /// 这里干脆不要形参：重开这件事本身不需要知道回退到哪。
        /// </summary>
        [GamePatch(typeof(scrController), "Start_Rewind", PatchKind.Postfix, "recording")]
        private static void RewindPostfix()
        {
            try
            {
                // 一次尝试 = 一个回放文件。失败后重开就把上一段收尾落盘，
                // 用户的每次尝试能单独回看，而不是所有尝试挤在一个文件里。
                KeyRecorder.End(Settings.AutoSave);
                VideoRender.RenderService.OnLevelEnd();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 重开收尾失败：" + ex);
            }
        }

        /// <summary>
        /// 保存 / 取消保存的快捷键。
        ///
        /// 挂在 <c>scrConductor.Update</c> 的 Postfix 上，与 Spectre 同一个
        /// 挂点同一个做法（见它的 Features/Replay/Patch_Misc.cs 里的
        /// Patch_ConductorUpdate）。
        ///
        /// **必须有它。** 之前换成 UMM 的 OnGUI 时我把所有热键都删了，
        /// 理由是"界面热键没用了"——但保存键不是界面热键：没有它，玩家
        /// 想主动存一局就只能等通关自动保存，练到一半的短局根本存不下来。
        /// 模组因此变得没法用。
        ///
        /// 只在 3/5/6 状态（游玩中/暂停/结算）响应，跟 Spectre 一致——
        /// 主菜单里按保存键不该有反应。
        /// </summary>
        [GamePatch(typeof(scrConductor), "Update", PatchKind.Postfix, "recording")]
        private static void HotkeyPostfix()
        {
            try
            {
                int state = (int)(States)scrController.instance.stateMachine.GetState();
                if (state != 3 && state != 5 && state != 6)
                {
                    return;
                }
                if (Settings.SaveKey > 0
                    && Input.GetKeyDown((KeyCode)Settings.SaveKey))
                {
                    string path = KeyRecorder.End(true);
                    if (path != null)
                    {
                        Debug.Log("[SpectreReplay] 已用快捷键保存：" + path);
                    }
                }
                else if (Settings.CancelSaveKey > 0
                    && Input.GetKeyDown((KeyCode)Settings.CancelSaveKey))
                {
                    // End(false) 已经够：它结束录制但**不落盘**，数据就丢了。
                    // 原本还调了一个不存在的 Discard()，编译不过。
                    if (KeyRecorder.IsRecording)
                    {
                        KeyRecorder.End(false);
                        Debug.Log("[SpectreReplay] 已取消保存");
                    }
                }
            }
            catch (Exception ex)
            {
                // 每帧都跑，不能让异常抛进游戏主循环
                Debug.LogError("[SpectreReplay] 快捷键处理异常：" + ex);
            }
        }

                /// <summary>
        /// 关卡重开后让回放接管。
        ///
        /// **这是"点了回放没反应"的根因所在。** 之前 Load 完就以为开始了，
        /// 可泵每帧读当前歌曲位置——而关卡还停在结算画面，位置早就跑完整个
        /// 回放长度，于是游标一次性冲到底、什么都不注入。
        ///
        /// 挂点照 Spectre 的 Features/Replay/Patch_Lifecycle.cs：它把
        /// "开始播"挂在 <c>scrController.Start_Rewind</c> 的 **Postfix** 上，
        /// **跟着关卡重开一起走**，歌曲位置自然从 0 开始。
        ///
        /// 用 Postfix 而不是 Prefix：Prefix 时关卡还没重置完，位置和判定
        /// 状态都还是旧的。
        /// </summary>
        [GamePatch(typeof(scrController), "Start_Rewind", PatchKind.Postfix, "playback")]
        private static void PlaybackBeginPostfix()
        {
            try
            {
                if (PlaybackEngine.IsPending)
                {
                    PlaybackEngine.Begin();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 回放启动失败：" + ex);
            }
        }

        [GamePatch(typeof(scrMarginTracker), "AddHit", PatchKind.Postfix, "recording")]
        private static void HitPostfix(HitMargin hit)
        {
            try
            {
                KeyRecorder.RecordMargin(hit, KeyRecorder.SongPositionOfNow());
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 判定记账异常：" + ex);
            }
        }

        /// <summary>进入关卡：开始录制（若开着）。由关卡进入补丁调用。</summary>
        internal static void BeginIfEnabled()
        {
            if (!Settings.RecordingOn && !Settings.RenderOn)
            {
                return;
            }
            var conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }
            var controller = scrController.instance;
            if (controller == null || controller.gameworld != true)
            {
                return;
            }
            if (Settings.RecordingOn)
            {
                KeyRecorder.Begin(conductor, controller.currFloor);
            }
            VideoRender.RenderService.OnLevelStart();
        }
    }
}