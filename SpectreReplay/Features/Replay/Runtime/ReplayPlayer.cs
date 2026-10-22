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
        is_playing = false;
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
            SyncEditorDifficultyLabel();
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

    internal static void FastForward(int targetFloor)
    {
        if (data == null) return;

        PlayIndex = 0;
        for (int i = 0; i < data.HitSnapshots.Count; i++)
        {
            if (data.HitSnapshots[i].CurrentFloorID >= targetFloor)
            {
                PlayIndex = i;
                break;
            }
        }

        // 边界检查要含**下界**：只判上界时负的 targetFloor 会通过检查，
        // 紧接着 listFloors[targetFloor] 抛 IndexOutOfRangeException
        if (ADOBase.lm != null && targetFloor >= 0 && targetFloor < ADOBase.lm.listFloors.Count)
        {
            double targetTime = ADOBase.lm.listFloors[targetFloor].entryTime;
            KeyEventIndex = 0;
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
        // 跳转后按键状态未知（与 CurrKeyState 同一处理方式）：清空，等后续事件重新建立
        ManagedInput.Clear();
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
        // **不能读 conductor.songposition_minusi。**
        //
        // 这个属性在 scrConductor.Update 的**末尾**（:852）才被重算，而
        // scrController.UpdateInput 在同一方法的 :799 就被调用——也就是说
        // 这里读到的永远是**上一帧**的值，开局第一帧甚至还是上一局遗留的。
        //
        // 实测（诊断日志）：第一帧读到 15.262（上一局的曲中位置），而按当时的
        // dspTimeSong/dspTime 现场算出来是 -1.526（倒计时阶段，正确）。那个假值
        // 让游标一次性推进了 64 个事件——前 8 秒的按键在同一帧被全部按下。
        //
        // 所以按游戏自己的公式现场算（scrConductor.cs:852 / :856），它和
        // dspTimeSong、dspTime 出自同一时刻，不存在跨帧陈旧。
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
        if (conductor == null || conductor.song == null) return 0.0;

        if (!GCS.d_oldConductor && !GCS.d_webglConductor)
        {
            // dspTimeSong 只在歌曲真正排定播放时被赋值（:668/:675）。
            // 为 0 时下面的式子退化成 (dspTime - 0) * pitch，是几万秒量级的伪值。
            if (conductor.dspTimeSong <= 0.0) return 0.0;
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

        // 单帧事件上限，**只做限速、绝不丢弃**。正常情况下每帧只有个位数事件
        // （16ms 内最多几次按键）；一帧里出现成百上千个"待办"只可能是这一帧来得
        // 太晚（掉帧、暂停恢复、或时钟刚就绪）。闷头灌完的后果是判定链在同一帧里
        // 被调几十次，玩家看到"一瞬间全部模拟完"——所以限速；但剩下的必须留到
        // 下一帧继续，游标只能前进、不能跳过（曾经在这里跳游标，直接把整局按键
        // 跳没了，表现是按键模拟完全失效）。
        const int MaxEventsPerFrame = 64;
        int pumped = 0;

        var inputs = _inputBuffer;
        inputs.Clear();
        // ── 注入方式选择 ──────────────────────────────────────
        //
        // **首选合成事件入队**（<see cref="AsyncInputInjector"/>）：它不经过
        // 操作系统，失焦可用、不受 UIPI 拦截、时间戳是亚帧精度。
        // 事件走 SkyHookManager.KeyUpdated.Invoke —— 与真实键盘回调同一个入口，
        // 游戏自己的监听器会把它送进 keyQueue，随后 keyMask / keyDownMask /
        // ProcessKeyInputs(tick) 全部按真实按键的路径走。
        //
        // 不可用时（未开异步输入、反射解析失败、实际入队失败）退回托管覆盖
        // + SendInput。**两条路互斥**：同时开会把同一颗键算两次
        // （SendInput 的事件会被 SkyHook 钩子再捕一次，正是 ADOFAIMacro 里
        // 要专门做"回声配额"丢弃的那种重复）。
        //
        // 判据用 Healthy 而不只是 Usable：Usable 是"看起来能用"，而一旦它
        // 判错、托管覆盖又已经放行，游戏那条链读到的就是永远空的 keyMask——
        // 两条路一起失效。
        // **广播判据与「游戏是否出队」是两件事，不能混用。**
        //
        // 这里用 Injectable（只看钩子活着、字段拿得到），而不是 Usable
        // （额外要求 asyncKeyboard.isActive，即只在 PlayerControl 为真）。
        //
        // 原因：KeyUpdated 的订阅者不只是游戏。JipperResourcePack 直接监听
        // `SkyHookManager.KeyUpdated`（KeyViewer.cs:2216），完全不读 RDInput /
        // keyMask / UnityEngine.Input。用 Usable 当判据的话，非 PlayerControl
        // 状态下 Inject 不被调用、Invoke 不发生，这类模组整段看不到按键。
        //
        // 广播到非 PlayerControl 不会污染判定：游戏监听器只把它写进 keyQueue，
        // 而 UpdateInput 在 asyncKeyboard.isActive 为假时开头早退（scrController.cs:2900），
        // 没人出队；AsyncInputManager.Update 又在 !flag 时每帧 ClearKeys()
        // （AsyncInputManager.cs:159-162 → :110-116 含 keyQueue.Clear）。
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
            if (num != 0 && num != 27)
            {
                // 托管输入侧：把回放状态并进 RDInput 的查询结果（见 Patch_RDInput）。
                // 与平台/聚焦无关，也【不】受 IsBlockedKey 影响——那个表是给
                // SendInput 用的（OS 级注入修饰键会干扰系统），托管路径没有这个问题。
                // 不过滤修饰键：原版 holdKeys 由 RDInput.GetMainHeldKeys 填充，
                // Shift/Ctrl/Alt 同样算「按住」；若在此滤掉，录音里只按住修饰键的长条
                // 会在回放时被判成松手。Esc(27) 与 0（未映射/无效）仍排除。
                ManagedInput.Note(num, ev.IsPressed);
                // 真路径：把事件按**它自己的** SongPosition 换算成绝对 tick 入队。
                // 用 ev.SongPosition 而不是外层 songPosition：外层是"现在"，事件可能
                // 是这几帧里漏掉的早先按键，用现在的时间戳会把它整体后移，时间戳一偏
                // ProcessKeyInputs 算出来的角度就偏，判定跟着偏。
                // 不套 IsBlockedKey：那个表是给 OS 级 SendInput 用的（修饰键会干扰系统），
                // 合成事件不经过 OS，没有这个问题。
                // 同一个事件循环里再问一次 Healthy：这一帧的第一个事件就可能已经
                // 入队失败（conductor 还没建好之类），后面的事件再往同一条断掉的
                // 管子里塞只会重复告警。失败后交给托管覆盖层兜底。
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
