using System;
using System.Collections.Generic;
using System.Linq;
using SpectreReplay.Features.Compat;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

internal static class ReplayPlayer
{
    // 复用列表，避免 PlayKeyboardEvent 每帧分配 List<INPUT>
    private static readonly List<KeyboardSimulation.INPUT> _inputBuffer = new List<KeyboardSimulation.INPUT>(256);

    internal static void Start()
    {
        if (!is_playing && !ApiBlockPlaying)
        {
            Stop();
            CurrKeyState = new byte[256];
            ManagedInput.Clear();
            PlayIndex = 0;
            // **这里必须从 0 开始，不能按 songposition_minusi 对齐。**
            // 那一刻 songposition_minusi 还是上一局遗留的陈旧值（Rewind() 只重置
            // dspTimeSong / crotchetAtStart，不碰 _songposition_minusi，
            // 见 scrConductor.cs:352-376），用曲中位置去对齐等于直接跳到文件末尾。
            // 检查点/中途开局的对齐由 Patch_Lifecycle 调 FastForward 负责。
            KeyEventIndex = 0;
            is_playing = true;
            is_recording = false;
            HitLocked = true;
            LateSaveMode = false;
            IgnoreMarkFail = true;

            // 把回放按键桥接给 KeyViewer 系显示模组。
            // 这些模组只读 Unity Input 或**它们自己被补丁过的** KeyInput.GetKey，
            // 而回放走的是托管覆盖 + SkyHook 合成事件，两条都不经过 Unity Input——
            // 不桥接的话键位显示器永远是黑的，即使游戏判定完全正常。
            // 在这里而不是 Main.OnToggle：模组加载顺序不保证 KeyViewer 先就绪，
            // 而回放开始时它一定已经在 AppDomain 里了。
            KeyViewerBridge.TryInstall();
        }
    }

    internal static void Stop()
    {
        if (!is_playing)
        {
            return;
        }
        GCS.hitMarginLimit = Persistence.hitMarginLimit;

        // **先补发抬起，再清空。** 回放数据里常有「只有按下没有抬起」的孤立事件
        // （录制结束时按着的键不会产生 UP：录制在按键那一刻就停了）。这些键的
        // 按住状态会一路挂到 Stop 之后，而 Stop 之后 is_playing=false，
        // RDInput 覆盖随即失效、游戏读回真实键盘——那个键明明是空的，
        // 状态却已经泄漏到结算画面/关卡选择里。
        //
        // 顺序必须是「取快照 → 置 is_playing=false → 按快照发事件」：
        // 先置假才能让 Note 里的日志与查询语义正确，而快照保证我们发的是
        // 停止那一刻真正按住的那些键（清空之后就查不到了）。
        List<ushort> stillHeld = ManagedInput.SnapshotHeld();
        is_playing = false;
        for (int i = 0; i < stillHeld.Count; i++)
            ReleaseLeakedKey(stillHeld[i]);
        // 回放结束：清掉托管输入状态，避免残留的按住键继续被 RDInput 报出来
        ManagedInput.Clear();
        if (PlayMode && ManagedInput.Mirror)
        {
            var releases = new List<KeyboardSimulation.INPUT>();
            for (int i = 1; i < 256; i++)
            {
                if ((CurrKeyState[i] & 0x80) != 0)
                {
                    releases.Add(KeyboardSimulation.MakeInput((byte)i, false, IsExtendedKey((ushort)i)));
                }
            }
            KeyboardSimulation.SendInputs(releases);
        }
        // 场景已卸时 instance 为 null：noFail 无从复原，但后续清理（键盘音停播、
        // CachedNoFail/IgnoreMarkFail 复位）必须照常跑到尾
        var controller = scrController.instance;
        if (controller != null)
            controller.noFail = CachedNoFail ?? false;
        CachedNoFail = null;
        // 判定模式还原：回放期可能套用过录像的难度（见 Patch_Lifecycle 的自动对齐）。
        // 玩家自己的默认难度存在 Persistence、从未被改过，所以这里还原的是
        // 「本局开始前 GCS 里的值」——与 noFail 的 CachedNoFail 同一套语义。
        if (CachedDifficulty.HasValue)
        {
            GCS.difficulty = CachedDifficulty.Value;
            CachedDifficulty = null;
            SyncDifficultyLabel();
        }
        IgnoreMarkFail = false;
        SyncEditorNoFailButton();
        try
        {
            WavLoader.Stop_keybdsound();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    /// <summary>
    /// 给一个只按下、从未抬起的回放键补发抬起。
    ///
    /// <para>只走 SkyHook 广播（如果那条链可用）：合成一个 KeyReleased 事件
    /// 灌回事件管线，游戏自己的 keyMask 与订阅 KeyUpdated 的模组都会看到
    /// 正确的抬起。SendInput 路径不需要补——它本来就在每次 Stop 时按
    /// <c>CurrKeyState</c> 发抬起（见 Stop 后半段）。</para>
    /// </summary>
    private static void ReleaseLeakedKey(ushort vk)
    {
        try
        {
            if (!AsyncInputInjector.Injectable || !AsyncInputInjector.Healthy)
                return;
            // 用当前钉扎/实时位置都行：这里只关心「事件到达且是抬起」，
            // 时间戳落在现在即可（Inject 内部按 dsp 基准换算）。
            scrConductor conductor = ADOBase.conductor;
            if (conductor == null || conductor.song == null) return;
            double pos = CurrentSongPosition(conductor, out bool ready);
            if (!ready) return;
            AsyncInputInjector.Inject(vk, false, pos);
        }
        catch (Exception ex)
        {
            // 补发抬起是清理性质的，失败不该影响 Stop 的其余步骤
            Debug.LogWarning("[SpectreReplay] 补发按键抬起失败 vk=" + vk + ": " + ex.Message);
        }
    }

    internal static void FastForward(int targetFloor)
    {
        if (data == null) return;

        // 兜底保持 0（从头播），不要落到 Count。
        // PlayIndex = Count 会让 Patch_Playback 的完成判定
        // `PlayIndex >= Count && KeyEventIndex >= Count` 在**开始那一帧**成立，
        // 于是回放立即自我 StopPlaying——玩家看到的就是「跳转后按键完全没有反应」。
        // 从头播最多是错位，落在末尾是直接结束，后者严重得多。
        PlayIndex = 0;
        for (int i = 0; i < data.HitSnapshots.Count; i++)
        {
            if (data.HitSnapshots[i].CurrentFloorID >= targetFloor)
            {
                PlayIndex = i;
                break;
            }
        }
        // 边界检查要含下界：只判上界时负的 targetFloor 会通过检查，
        // 紧接着 listFloors[targetFloor] 抛 IndexOutOfRangeException。
        //
        // 取不到目标时间时（lm 未就绪、索引越界）**不要动 KeyEventIndex**，
        // 保持 Start() 给的 0 继续走。此时无法判断该从哪条事件接上，把游标
        // 推到末尾等于把整局按键全部丢掉；从当前位置继续虽然可能重复前几个事件，
        // 但按键至少还在。
        if (ADOBase.lm != null && targetFloor >= 0 && targetFloor < ADOBase.lm.listFloors.Count)
        {
            double targetTime = ADOBase.lm.listFloors[targetFloor].entryTime;
            KeyEventIndex = data.KeyEvents.Count;
            for (int i = 0; i < data.KeyEvents.Count; i++)
            {
                if (data.KeyEvents[i].SongPosition >= targetTime)
                {
                    KeyEventIndex = i;
                    break;
                }
            }
        }

        CurrKeyState = new byte[256];
        ManagedInput.Clear();

        // 重建跳转点仍然按住的键。
        // 被跳过的事件里「按下但还没抬起」的键，在跳转之后仍是按住状态；只清空不重建，
        // 那颗键就永远等不到按下事件，后面的抬起事件打在空状态上是幂等空操作。
        // 长按地板（hold）正是靠这个状态判定的，所以在长条中间跳转会直接漏判。
        for (int i = 0; i < KeyEventIndex; i++)
        {
            var ev = data.KeyEvents[i];
            ushort num = ev.KeyCode < Settings.key_code_convert.Length
                ? Settings.key_code_convert[ev.KeyCode]
                : (ushort)0;
            // 热键也不该在跳转后被「恢复成按住」：存量回放里录进了它们，
            // 而 Restore 不写帧戳、只改按住集合，于是它们会一直挂着到结束。
            if (num == 0 || num == 27 || ManagedInput.IsModHotkey(num)) continue;
            ManagedInput.Restore(num, ev.IsPressed);
        }
        // 托管状态已就位，同步一份给 SendInput 路径，Stop() 才知道该发哪些抬起
        for (int vk = 1; vk < CurrKeyState.Length; vk++)
        {
            if (ManagedInput.IsHeldVk((ushort)vk)) CurrKeyState[vk] = 128;
        }

        AllAutoHits = false;
    }

    internal static void PlayKeyboardEvent(bool flag)
    {
        // data 为空时（回放数据尚未装载/已被清空）直接返回：本方法每帧被调用，
        // 缺这一句就是每帧一次 NullReferenceException
        if (data == null || data.KeyEvents == null)
        {
            return;
        }

        // **结束判定必须排在「已耗尽」早退之前。**
        //
        // 最后一条按键事件通常在 PlayerControl 阶段就灌完了（KeyEventIndex 已经
        // 追平 Count）。之后关卡进入结算态，PlayerControl_Update 不再运行，
        // <see cref="Patch_PlayerControlUpdate"/> 里那条「命中与按键都耗尽就 Stop」
        // 的判定随之停摆；此时唯一还在每帧执行的就是本方法（Won_Update 的 Postfix）。
        //
        // 而它原先的第一道检查是「KeyEventIndex >= Count 就 return」——正好把
        // 这个唯一能收尾的入口挡在门外。后果不是「少一条提示」这么轻：
        // is_playing 永远为真 ⇒ ManagedInput.Active 永远为真 ⇒ RDInput 的
        // GetMain/GetState/GetStateKeys 全被回放数据覆盖，真人键盘完全失灵，
        // 结算画面按什么都不响应，只能去设置页手动卸载回放才能退出关卡。
        // 2026-10-24 实测：不渲染、仅正常播放回放，结束后同样必须卸载才能退出。
        //
        // 判据只认「按键已耗尽」，**不要求命中快照也吃完**：flag=true 只来自
        // Won_Update 的 Postfix，那时关卡已经打完，命中推进早就停了
        // （PlayerControl_Update 不再运行）。再要求 PlayIndex 追平，只要还剩
        // 几个快照没吃完就永远判不到结束——等于没修。
        if (flag && KeyEventIndex >= data.KeyEvents.Count)
        {
            KeyEventIndex = 0;
            PlayIndex = 0;
            Debug.Log("[SpectreReplay] replay finished (keys exhausted in Won state), stopping playback");
            Stop();
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
            return;
        }

        if (KeyEventIndex < 0 || KeyEventIndex >= data.KeyEvents.Count)
        {
            return;
        }
        // 本方法由 scrController.Won_Update 的 Postfix **每帧**调用（结算阶段亦如此）。
        // conductor 为空/已销毁时（场景卸载中、关卡尚未就绪）直接返回：否则每帧抛一次
        // 异常，而 Unity 已销毁的对象再访问其成员会抛 MissingReferenceException。
        scrConductor conductor = ADOBase.conductor;
        if (conductor == null)
        {
            return;
        }
        // 与 PumpInput() 同因：songposition_minusi 在同一帧的 Update 末尾才重算，
        // 这里读到的是上一帧的值。按游戏自己的公式现场算。
        double songPosition = CurrentSongPosition(conductor, out bool ready);
        if (!ready) return;

        PumpInput(songPosition, conductor);
        if (KeyEventIndex >= data.KeyEvents.Count)
        {
            if (flag)
            {
                KeyEventIndex = 0;
                PlayIndex = 0;
                Stop();
                TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
            }
        }
    }

    /// <summary>
    /// 把已经到时的按键事件灌进托管输入状态（以及 SendInput 缓冲）。
    ///
    /// **必须在游戏读输入之前调用。** 游戏读输入的时序是
    /// <c>PlayerControl_Update → Simulated_PlayerControl_Update</c>（scrController.cs:552），
    /// 后者内部按 :417 → :430 → :433 → :434 依次读 <c>ValidInputWasReleased</c> /
    /// <c>ValidInputWasTriggered</c>（含 mainPressCount）/ <c>holding</c> / <c>keyTimes</c>。
    /// 所以注入挂在两条驱动链的入口 Prefix 上（见 Patch_RDInput），
    /// 而 <see cref="PlayKeyboardEvent"/>（Won_Update 的 Postfix）只是结算阶段的兜底——
    /// 它跑在读取之后，只补 SendInput，不能作为主路径。
    /// </summary>
    internal static void PumpInput()
    {
        if (!is_playing) return;
        if (data == null || data.KeyEvents == null) return;
        if (KeyEventIndex < 0 || KeyEventIndex >= data.KeyEvents.Count) return;
        scrConductor conductor = ADOBase.conductor;
        if (conductor == null) return;
        double songPosition = CurrentSongPosition(conductor, out bool ready);
        if (!ready) return;
        PumpInput(songPosition, conductor);
    }

    /// <summary>
    /// 按 scrConductor 自己的公式现场求歌曲位置（scrConductor.cs:850-857）。
    ///
    /// <paramref name="ready"/> 为 false 表示时钟还没建立：此时返回值的量级
    /// 是"音频系统启动以来的秒数"，拿它去比事件时间会把整局按键一次灌完。
    /// 这是「播放一瞬间全部模拟完」的成因。
    /// </summary>
    private static double CurrentSongPosition(scrConductor conductor, out bool ready)
    {
        ready = false;

        // **渲染期以钉扎时间轴为唯一时间源。**
        //
        // 渲染时画面侧（songposition_minusi 的 getter 被替换）与命中侧
        // （Patch_PlayerControlUpdate 读同一个钉扎值）都按 VideoRender.PinnedSongPosition
        // 推进，而这里原本按 dspTime 现场重算——那是**另一条时钟**。两条时钟的速率
        // 只在渲染恰好等于实时时才一致；离线渲染慢于实时（编码/回读是瓶颈）时
        // 二者越走越开，按键于是整体早于或晚于画面，听起来就是「回放慢拍子」。
        //
        // 三处（画面 / 命中 / 按键）必须同源，所以渲染期直接取钉扎值。
        // 非渲染期 TimePinned 恒为假，原样走下面的游戏公式，行为不变。
        if (Features.VideoRender.VideoRender.TryReadPinnedPosition(out double pinned))
        {
            ready = true;
            return pinned;
        }

        if (conductor == null || conductor.song == null) return 0.0;

        if (!GCS.d_oldConductor && !GCS.d_webglConductor)
        {
            // dspTimeSong 未排定时恰好是 0（Rewind 里显式置 0），此时下面的式子
            // 退化成 (dspTime - 0) * pitch，是几万秒量级的伪值，必须挡掉。
            //
            // 判据必须是 `== 0`，**不能用 `<= 0`**：从检查点起跑时游戏会执行
            // ScrubMusicToTime，把 dspTimeSong 设成 dspTime - entryTime，也就是一个
            // 大负数（例：29 - 214 = -184.9）。那个负数正是「歌曲从第 214 秒开始」的
            // 表达方式，挡掉它会让整段回放一个按键都灌不进去。
            if (conductor.dspTimeSong == 0.0) return 0.0;
            ready = true;
            return (conductor.dspTime - conductor.dspTimeSong - (double)scrConductor.calibration_i)
                   * (double)conductor.song.pitch - conductor.addoffset;
        }

        ready = true;
        return (double)(conductor.song.time - scrConductor.calibration_i)
               - conductor.addoffset / (double)conductor.song.pitch;
    }

    private static void PumpInput(double songPosition, scrConductor conductor)
    {
        if (!is_playing) return;
        if (data == null || data.KeyEvents == null) return;
        if (KeyEventIndex < 0 || KeyEventIndex >= data.KeyEvents.Count) return;

        if (conductor == null || conductor.song == null) return;

        // 时钟是否就绪由调用方的 CurrentSongPosition 判定（ready=false 已在上层返回），
        // 这里拿到的一定是同一时刻算出的真实歌曲位置。

        // 无待办事件时提前返回，避免空 List 分配
        if (songPosition < data.KeyEvents[KeyEventIndex].SongPosition)
            return;

        const int MaxEventsPerFrame = 64;
        int pumped = 0;

        var inputs = _inputBuffer;
        inputs.Clear();
        bool asyncInject = AsyncInputInjector.Injectable && AsyncInputInjector.Healthy;
        // SendInput 与**真正在灌**的那条互斥：只要异步广播在走就不发 OS 级按键
        // （SkyHook 会把 SendInput 的键再捕一次，同一颗键算两遍）。
        bool sendInput = ManagedInput.Mirror && !asyncInject;
        while (KeyEventIndex < data.KeyEvents.Count
            && pumped < MaxEventsPerFrame
            && songPosition >= data.KeyEvents[KeyEventIndex].SongPosition)
        {
            var ev = data.KeyEvents[KeyEventIndex];
            // 回放文件里的键码是 ushort，损坏/伪造文件可能越界；映射表恒为 256 长度，
            // 直接索引会在每帧回放里抛 IndexOutOfRange。越界键按被屏蔽键（0）处理跳过
            ushort num = ev.KeyCode < Settings.key_code_convert.Length
                ? Settings.key_code_convert[ev.KeyCode]
                : (ushort)0;
            if (num != 0 && num != 27 && !ManagedInput.IsModHotkey(num))
            {
                // 托管输入侧：把回放状态并进 RDInput 的查询结果（见 Patch_RDInput）。
                // 与平台/聚焦无关，也【不】受 IsBlockedKey 影响——那个表是给
                // SendInput 用的（OS 级注入修饰键会干扰系统），托管路径没有这个问题。
                // 不过滤修饰键：原版 holdKeys 由 RDInput.GetMainHeldKeys 填充，
                // Shift/Ctrl/Alt 同样算「按住」；若在此滤掉，录音里只按住修饰键的长条
                // 会在回放时被判成松手。Esc(27) 与 0（未映射/无效）仍排除。
                ManagedInput.Note(num, ev.IsPressed);
                if (asyncInject && AsyncInputInjector.Healthy)
                {
                    try { AsyncInputInjector.Inject(num, ev.IsPressed, ev.SongPosition); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            if (sendInput && num != 0 && num != 27 && !IsBlockedKey(num))
            {
                inputs.Add(KeyboardSimulation.MakeInput((byte)num, ev.IsPressed, IsExtendedKey(num)));
                CurrKeyState[num] = (byte)(ev.IsPressed ? 128u : 0u);
            }
            KeyEventIndex++;
            pumped++;
        }
        KeyboardSimulation.SendInputs(inputs);
    }

    internal static bool ReplayHitOld(scrPlayer player, HitSnapshot hitdata)
    {
        player.consecMultipressCounter = 0;
        // 命中路径上 controller 一定存在（游戏正是在派发命中），但场景卸载的中间态里
        // 静态 instance 可能已为 null。**只跳过依赖 controller 的两项赋值**并继续往下走：
        // 中途 return 会让后续的行星角度 / auto 标志 / SwitchChosen 都不执行，留下
        // "半个命中"的状态（行星没转、auto 没设），比缺两项 controller 级设置更糟。
        scrController controller = scrController.instance;
        if (controller != null)
        {
            controller.multipressPenalty = false;
            controller.noFailInfiniteMargin = hitdata.NoFailHit;
        }
        player.failBar.overloadCounter = 0f;
        bool isAuto = hitdata.IsAuto;
        player.planetarySystem.chosenPlanet.SetTargetExitAngle(hitdata.TargetExitAngle);
        player.midspinInfiniteMargin = hitdata.MidspinInfiniteMargin;
        player.failBar.overloadCounter = hitdata.OverloadCounter;
        player.planetarySystem.chosenPlanet.angle = hitdata.CachedAngle;
        RDC.auto = hitdata.IsAuto;
        player.responsive = true;
        IgnoreMarkFail = false;
        if (hitdata.NoFailHit)
        {
            scrMissIndicator val = player.planetarySystem.chosenPlanet.MarkFail();
            if (val != null)
            {
                val.BlinkForSeconds(3f);
            }
        }
        scrMisc.Vibrate(50L);
        if (!player.responsive || player.isReunifying)
        {
            return false;
        }
        if (ADOBase.isLevelEditor && ADOBase.controller.paused)
        {
            return false;
        }
        bool flag = player.planetarySystem.chosenPlanet.currfloor.nextfloor != null && player.planetarySystem.chosenPlanet.currfloor.nextfloor.auto;
        player.planetarySystem.chosenPlanet.cachedAngle = player.planetarySystem.chosenPlanet.angle;
        scrFloor currFloor = player.planetarySystem.chosenPlanet.player.currFloor;
        player.planetarySystem.chosenPlanet.next.planetRenderer.ChangeFace(true, false);
        scrPlanet chosenPlanet = player.planetarySystem.chosenPlanet;
        // SwitchChosen 在 r150 起多了一个 long? hitTick 形参：老版 0 参、新版 1 参，
        // 直接调用会按编译期签名绑定，旧游戏上抛 MissingMethodException，必须走兼容层择路
        player.planetarySystem.chosenPlanet = GameCompat.SwitchChosen(player.planetarySystem.chosenPlanet);
        bool result = chosenPlanet != player.planetarySystem.chosenPlanet;
        // r150 起准度条喂数搬进了 scrController.UpdateHitErrorMeter（由 scrPlayer.Hit 调用，
        // 内部还会自取 HitMargin 决定 tick 颜色）。此处若再按老签名补一次就会双记。
        // 有 UpdateHitErrorMeter 的版本一律交给游戏自己喂
        if (!GameCompat.HasUpdateHitErrorMeter
            && ADOBase.controller.errorMeter != null && ADOBase.controller.gameworld
            && (int)Persistence.hitErrorMeterSize > 0)
        {
            float num = (float)(chosenPlanet.cachedAngle - chosenPlanet.targetExitAngle);
            if (currFloor.isCCW)
            {
                num *= -1f;
            }
            if (!player.midspinInfiniteMargin)
            {
                // 走到这里必然是旧版（r150 起由 UpdateHitErrorMeter 自喂，上面已 return 掉）：
                // 旧 AddHit 没有 HitMargin 形参，margin 传参会被兼容层丢弃，仅供新签名分支占位
                if ((player.auto || flag) && !RDC.useOldAuto)
                {
                    GameCompat.ErrorMeterAddHit(ADOBase.controller.errorMeter, 0f,
                        HitMarginCompat.Absent, 1f, player.planetarySystem.chosenPlanet, currFloor);
                }
                else
                {
                    GameCompat.ErrorMeterAddHit(ADOBase.controller.errorMeter, num,
                        HitMarginCompat.Absent, (float)player.currFloor.marginScale,
                        player.planetarySystem.chosenPlanet, currFloor);
                }
            }
        }
        if (ADOBase.playerIsOnIntroScene)
        {
            return result;
        }
        bool flag2 = player.planetarySystem.chosenPlanet.currfloor.holdLength == -1 || (player.planetarySystem.chosenPlanet.currfloor.holdLength > -1 && ADOBase.controller.lastCamPulseFloor < player.planetarySystem.chosenPlanet.currfloor.seqID);
        ADOBase.controller.lastCamPulseFloor = player.planetarySystem.chosenPlanet.currfloor.seqID;
        scrCamera camy = ADOBase.controller.camy;
        if (flag2)
        {
            camy.UpdateFollowCam(false);
        }
        if (camy.isPulsingOnHit && flag2)
        {
            camy.Pulse();
        }
        bool flag3 = true;
        if (ADOBase.lm != null && ADOBase.controller.gameworld)
        {
            if (player.currFloor.midSpin || (player.currFloor.seqID > 0 && ADOBase.lm.listFloors[player.currFloor.seqID - 1].holdLength > -1))
            {
                flag3 = false;
            }
            if (player.currFloor.seqID > 1 && ADOBase.lm.listFloors[player.currFloor.seqID - 1].midSpin && ADOBase.lm.listFloors[player.currFloor.seqID - 2].holdLength > -1)
            {
                flag3 = false;
            }
        }
        if (flag3)
        {
            if (scnEditor.instance != null)
            {
                scnEditor.instance.OttoBlink();
            }
            if (VirtualAvatarCanvas.instance != null)
            {
                VirtualAvatarCanvas.instance.Hit(player.playerID);
            }
        }
        if (player.currFloor.midSpin)
        {
            player.midspinInfiniteMargin = true;
            player.keyTimes.Add(Time.unscaledTimeAsDouble);
        }
        else
        {
            player.midspinInfiniteMargin = false;
        }
        player.planetarySystem.chosenPlanet.Update_RefreshAngles();
        if (!hitdata.NextFloorAuto && hitdata.RDC_auto)
        {
            RDC.auto = true;
        }
        else
        {
            RDC.auto = false;
        }
        // 收尾：controller 缺失时跳过（不抛）
        scrController tailController = scrController.instance;
        if (tailController != null)
        {
            tailController.noFailInfiniteMargin = false;
        }
        IgnoreMarkFail = true;
        return result;
    }

    internal static bool ReplayHit(scrPlayer player, HitSnapshot hitdata)
    {
        if (debug_use_old_hit)
        {
            return ReplayHitOld(player, hitdata);
        }
        player.consecMultipressCounter = 0;
        // 同 ReplayHitOld：只跳过依赖 controller 的两项，其余照常
        scrController controller = scrController.instance;
        if (controller != null)
        {
            controller.multipressPenalty = false;
            controller.noFailInfiniteMargin = hitdata.NoFailHit;
        }
        player.failBar.overloadCounter = 0f;
        bool isAuto = hitdata.IsAuto;
        player.planetarySystem.chosenPlanet.SetTargetExitAngle(hitdata.TargetExitAngle);
        player.midspinInfiniteMargin = hitdata.MidspinInfiniteMargin;
        player.failBar.overloadCounter = hitdata.OverloadCounter;
        player.planetarySystem.chosenPlanet.angle = hitdata.CachedAngle;
        RDC.auto = hitdata.IsAuto;
        player.responsive = true;
        IgnoreMarkFail = false;
        if (hitdata.NoFailHit)
        {
            scrMissIndicator val = player.planetarySystem.chosenPlanet.MarkFail();
            if (val != null)
            {
                val.BlinkForSeconds(3f);
            }
        }
        HitLocked = false;
        // 【绝不透传录制的 HitTick】它是 SkyHook 的原始 tick，换算歌曲时间要减
        // AsyncInputManager.offsetTick——那是【本次运行】启动时才校准的锚点，每局都不同。
        // 拿录制局的原始 tick 在回放局换算会得到完全错位的时间差 → GetHitMarginInSec
        // 判出不计数 → 回放强制 noFail 吞掉失败 → 行星原地打转、永不前进（"卡输入"）。
        // 回放已精确恢复了 angle / targetExitAngle：传 null 让 SwitchChosen 走
        // GetHitMarginInDeg 的角度分支（录制前缀在 AdjustAngle 之后取角，角度里已含
        // tick 的精确度），判定与录制瞬间一致。HitTick 仅作录制元数据保留在文件里。
        bool result = GameCompat.PlayerHit(player, isAuto, null);
        HitLocked = true;
        if (!hitdata.NextFloorAuto && hitdata.RDC_auto)
        {
            RDC.auto = true;
        }
        else
        {
            RDC.auto = false;
        }
        // 收尾：controller 缺失时跳过（不抛）
        scrController tailController = scrController.instance;
        if (tailController != null)
        {
            tailController.noFailInfiniteMargin = false;
        }
        IgnoreMarkFail = true;
        return result;
    }
}
