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

        [GamePatch(typeof(scrController), "Start_Rewind", PatchKind.Postfix, "recording")]
        private static void RewindPostfix(scrController __instance, int checkpointNum)
        {
            if (checkpointNum == 0)
            {
                KeyRecorder.End(false);
                VideoRender.RenderService.OnLevelEnd();
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