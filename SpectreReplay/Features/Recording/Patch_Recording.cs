using System;
using SpectreReplay.Patching;
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