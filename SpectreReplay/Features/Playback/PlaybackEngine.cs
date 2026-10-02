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
    /// 回放引擎：按歌曲时间轴，**往 Windows 注入真实键盘输入**。
    ///
    /// 为什么不是往 AsyncInputManager.keyQueue 里塞 SkyHookEvent（我最初的做法）：
    ///   那条路根本走不通。scrController.cs:2885 有一道门——
    ///     if (!RDInput.asyncKeyboard.isActive &amp;&amp; !...Left... &amp;&amp; !...Right...)
    ///         return;
    ///   而 asyncKeyboard.isActive 只有**玩家自己在游戏设置里开了异步输入**才为真
    ///   （AsyncInputManager.Update:147）。默认输入下它恒为 false，UpdateInput
    ///   第 2885 行直接 return，队列里的东西永远没人读——"点了回放毫无反应"。
    ///   Spectre 也不走这条路，它用的是 Win32 SendInput。
    ///
    /// 注入真实按键后，游戏自己的 SkyHook 驱动会像真人输入一样收到事件，
    /// 走完全相同的判定链路。不复刻任何判定逻辑，也不要求玩家改任何设置。
    ///
    /// 时间基准与录制一致：<c>scrConductor.songposition_minusi</c>。
    /// 一帧内到点的按键一次性全部注入——每帧只补一个会让密集段整体延后。
    /// </summary>
    internal static class PlaybackEngine
    {
        private static ReplayData _data;
        private static int _cursor;
        private static double _position;
        private static string _sourcePath;
        private static int _lastFrame = -1;

        // ── 回放期间必须夹逼的全局状态 ────────────────────────────
        //
        // 照 Spectre 的 SpectreState。回放会改写玩家的全局设置，
        // 不夹住就会泄漏到回放之外——而玩家从界面上看不出任何异常。
        //
        // · NoFailBackup      回放强制 noFail=true，否则回放里的"失败"
        //                     会真的把玩家卡住（行星不前进）
        // · HitMarginBackup   回放期间判定窗口放宽，否则注��的按键
        //                     落在录制容差之外时会被判失败
        private static bool _noFailBackup;
        private static bool _noFailBackupValid;
        private static HitMarginLimit _hitMarginBackup;
        private static bool _hitMarginStored;

        // 每帧复用：Pump 每帧都跑，不能每帧 new List。
        private static readonly List<KeyboardSimulation.INPUT> Staged =
            new List<KeyboardSimulation.INPUT>(32);

        internal static bool IsPlaying => _data != null;
        internal static string CurrentPath => _sourcePath;
        internal static string LastError { get; private set; } = "";

        /// <summary>
        /// 已载入但还没真正开播——等关卡重开时接管。
        ///
        /// **必须这样。** Load 时关卡往往停在结算画面，
        /// songposition 早就跑完整个回放长度；真在 Load 里开播的话，
        /// 游标会一次性冲到底、什么都不注入，看起来就是"播了但什么也没发生"。
        ///
        /// 挂点照 Spectre 的 Features/Replay/Patch_Lifecycle.cs：
        /// 把"开始播"挂在 <c>scrController.Start_Rewind</c> 的 **Postfix**，
        /// **跟着关卡重开一起走**，歌曲位置自然从 0 开始。
        /// </summary>
        internal static bool Pending { get; private set; }

        /// <summary>已载入但待重开关卡才开播。</summary>
        internal static bool IsPending => Pending && _data != null;

        /// <summary>回放总长（秒）。未载入时为 0。</summary>
        internal static double ReplayLength => _data == null ? 0.0 : _data.Length;

        /// <summary>
        /// 状态轨回放期间为真。
        ///
        /// **这是两条轨互锁的关键**，照 Spectre 的 SpectreState.HitLocked
        /// （Playback 用 Patch_Recording.cs:39 的 `if (HitLocked) return false;`
        /// 掐掉键轨触发的 scrPlayer.Hit）。不互锁的话同一块砖会被判两次：
        /// 一次是键轨注入按键让游戏自然判定，一次是状态轨强制写回状态再判。
        /// 表现为判定条飘两次字、行星跳两格，且角度被反复拉回。
        /// </summary>
        internal static bool HitLocked => HitStateReplayer.Locked;

        /// <summary>游标归零但不卸载——离线渲染需要"从头再放一遍"。</summary>
        internal static void ResetCursor()
        {
            _cursor = 0;
            _position = 0;
            _lastFrame = -1;
            HitStateReplayer.Reset();
        }

        internal static string Describe()
        {
            if (_data == null)
            {
                return LastError.Length > 0 ? LastError : "—";
            }
            return (_data.SongName.Length > 0 ? _data.SongName : "?")
                + (_data.DifficultyName.Length > 0 ? " · " + _data.DifficultyName : "")
                + "   " + _data.Keys.Count + " keys"
                + "   " + _data.Hits.Count + " hits"
                + "   " + (_cursor * 100) / Math.Max(1, _data.Keys.Count) + "%";
        }

        internal static bool Load(string path)
        {
            Stop();
            try
            {
                _data = Recording.ReplayFile.Read(path);
                _sourcePath = path;
                _cursor = 0;
                _position = 0;
                _lastFrame = -1;
                HitStateReplayer.Load(_data);
                LastError = "";
                // playback 功能是按"是否有载入的回放"门控的，载入成功后
                // 必须让补丁层重挂一次，否则 UpdateInput 的 Prefix 还没上。
                Patching.PatchLoader.Refresh();
                Debug.Log("[SpectreReplay] 已载入回放：" + path
                    + "（" + _data.Keys.Count + " 按键 / " + _data.Hits.Count
                    + " 命中 / " + _data.Length.ToString("0.0") + "s）");
                if (_data.Keys.Count == 0 && _data.Hits.Count == 0)
                {
                    // 空回放：播了也不会有任何表现。直说，别让玩家对着
                    // "点了回放没反应"猜是哪一步坏了。
                    Debug.LogWarning("[SpectreReplay] 这个回放一个按键都没有，播了不会有任何表现");
                }
                Pending = true;
                Ui.Status.Say("status.pending_replay",
                    System.IO.Path.GetFileNameWithoutExtension(path));
                return true;
            }
            catch (Exception ex)
            {
                _data = null;
                LastError = ex.Message;
                Debug.LogError("[SpectreReplay] 回放读取失败：" + ex);
                Ui.Status.Warn("status.loaded_none", ex.Message);
                return false;
            }
        }

        internal static void Stop()
        {
            Pending = false;
            // 补发松开：回放中途停掉时，录制里配对的松开事件可能还没轮到，
            // 不补的话那个键会一直卡在按下状态，玩家的下一个物理按键
            // 只会被当成一次松开，之后判定全乱。
            KeyboardSimulation.ReleaseAll();
            // 状态轨：卸掉。**必须在 _data == null 的早退之前**——
            // 与 RestoreGlobalState 同理，这一份是我们自己持有了数据。
            HitStateReplayer.Unload();
            // 还原全局状态（noFail / 判定窗口）。
            // **必须在 _data == null 的早退之前**——回放被打断时
            // 数据可能已经清空了，但全局状态是我们自己改的，必须还原。
            RestoreGlobalState();
            if (_data == null)
            {
                return;
            }
            _data = null;
            _cursor = 0;
            _position = 0;
            // 同 Load：门控随状态变化，必须重挂才能真正摘掉 Prefix
            Patching.PatchLoader.Refresh();
            Ui.Status.Say("status.stopped");
        }

        /// <summary>
        /// 关卡重开后真正开播。由 Start_Rewind 的补丁调用。
        ///
        /// 游标必须归零：这时候关卡刚重开，songposition 才回到开头。
        /// 不归零的话游标还停在"载入后那次冲到的末尾"，看起来就是
        /// "播了，但一个键都没放"。
        ///
        /// 校验放在这里而不是 Load：Load 时玩家可能还没进关卡，
        /// 那时谱面、难度、起点都取不到，校验只会误报。
        /// 此刻这些值全部就绪，才是校验的时机。
        /// </summary>
        internal static void Begin()
        {
            Pending = false;
            if (_data == null)
            {
                return;
            }
            if (!PlaybackValidator.Validate(_data))
            {
                // 校验不通过：卸载数据。Pending 已在上面清掉，
                // 所以用户修好东西重新载入即可，不必重启关卡。
                Debug.Log("[SpectreReplay] 回放被校验拦下，已取消待播状态");
                Stop();
                return;
            }
            _cursor = 0;
            _position = 0;
            _lastFrame = -1;
            HitStateReplayer.Reset();
            ClampGlobalState();
            Debug.Log("[SpectreReplay] 回放开始：" + _data.Keys.Count
                + " 按键 / " + _data.Hits.Count + " 命中 / "
                + _data.Length.ToString("0.0") + "s");
            Ui.Status.Say("status.playing");
        }

        /// <summary>
        /// 开播前改写全局状态，并把原值备份下来。
        ///
        /// 两处，都照 Spectre（Patch_Lifecycle.cs:219-225）：
        ///
        /// ① <c>noFail = true</c> —— 回放是按录制数据推进的，注���的按键
        ///    不可能每次都落在玩家的判定窗口内。一旦判失败，行星就停在原地
        ///    不前进，表现为「回放卡住了」。强制 noFail 是 Spectre 的做法。
        ///
        /// ② <c>GCS.hitMarginLimit</c> —— 用**录制时的判定窗口**，而不是一律放宽。
        ///    照 Spectre（Patch_Lifecycle.cs:219-223）从回放数据里读出
        ///    <c>hitMarginLimit</c> 并应用，回放看到的就是录制时的判定档位，
        ///    飘字与判定结果因此与录制一致。
        ///    读不到（v1 旧文件没有这个字段）时退回最宽松
        ///    （(HitMarginLimit)0 = 不设限），与 Spectre 的默认值一致
        ///    （SpectreState.cs:183）——那时注���的按键必须全部收下，
        ///    否则回放会中途卡住。
        ///
        /// 备份**只在第一次生效**，否则重播一次就把"备份值"覆盖成了
        /// 上一次自己写进去的宽松值——玩家真实的设置就永久丢了。
        /// </summary>
        private static void ClampGlobalState()
        {
            var controller = scrController.instance;
            if (controller != null && !_noFailBackupValid)
            {
                _noFailBackup = controller.noFail;
                _noFailBackupValid = true;
                controller.noFail = true;
            }
            if (!_hitMarginStored)
            {
                _hitMarginBackup = GCS.hitMarginLimit;
                _hitMarginStored = true;
            }
            GCS.hitMarginLimit = StoredHitMarginLimitFrom(_data);
        }

        /// <summary>
        /// 从回放数据里取判定窗口，越界/取不到时退回"不设限"。
        ///
        /// 名字带 From：同类型的成员里已有一个 <see cref="StoredHitMarginLimit"/>
        /// 属性（给 LightUp 的 Postfix 用，读的是"当前生效值"）。
        /// 两者语义不同——这个是**解析**，那个是**读状态**。
        /// </summary>
        private static HitMarginLimit StoredHitMarginLimitFrom(ReplayData d)
        {
            // 越界的值说明文件损坏或来自别的版本。判不出就退回"不设限"，
            // 那是最宽松的：回放中途卡住比判定显示错更糟。
            int v = d?.HitMarginLimit ?? 0;
            var max = Enum.GetValues(typeof(HitMarginLimit)).Length;
            if (v < 0 || v >= max)
            {
                return (HitMarginLimit)0;
            }
            return (HitMarginLimit)v;
        }

        /// <summary>
        /// 当前生效的判定窗口（回放期间 = 录制时那档）。
        ///
        /// 给 <c>scrFloor.LightUp</c> 的 Postfix 用：LightUp 内部会把砖块
        /// 颜色按 <c>PerfectsOnly</c> 处理，方法体跑完要立刻恢复成录制档位，
        /// 否则后续判定飘的档位就不对了（照 Spectre 的 Patch_Misc.cs:92-96）。
        ///
        /// 没载入回放时直接回读 GCS——那时它本来就该是玩家自己的设置，
        /// 不做任何假设。
        /// </summary>
        internal static HitMarginLimit StoredHitMarginLimit
            => _data == null ? GCS.hitMarginLimit : StoredHitMarginLimitFrom(_data);

        /// <summary>停止时把全局状态还原。由 Stop 统一调用。</summary>
        private static void RestoreGlobalState()
        {
            if (_noFailBackupValid)
            {
                try
                {
                    var controller = scrController.instance;
                    if (controller != null)
                    {
                        controller.noFail = _noFailBackup;
                    }
                }
                catch { }
                _noFailBackupValid = false;
            }
            if (_hitMarginStored)
            {
                try
                {
                    GCS.hitMarginLimit = _hitMarginBackup;
                }
                catch { }
                _hitMarginStored = false;
            }
        }

        /// <summary>放弃待播的回放（玩家点了别的关卡或取消）。</summary>
        internal static void CancelPending()
        {
            Pending = false;
        }

        /// <summary>
        /// 每帧调用。把时间已到的按键注入系统，并推进状态轨。
        ///
        /// 挂在 <c>scrController.PlayerControl_Update</c> 的 **Prefix**：早于游戏自己的
        /// 输入处理，这一帧注入的当帧就被看到，凭空多一帧延迟。
        ///
        /// **两条轨的先后顺序：先键轨、后状态轨。** 与 Spectre 一致
        /// （Patch_Playback.cs:126-158：先 ReplayHit 一轮，最后才
        /// Play_keybd_event）。理由是状态轨按**当前角度**推进
        /// （angle >= hc.Angle 才判），先注入按键会让游戏自己把行星
        /// 推进一段，状态轨随后看到的就是"已经转过头的角度"——
        /// 于是密集段里每块砖的命中会早触发一帧到半帧，判定条整体前移。
        ///
        /// 反过来先状态轨、键轨后走，键轨触发的 scrPlayer.Hit 会被
        /// HitLocked 掐掉（这正是互锁的意义），键轨就只剩"驱动
        /// 长按/hold 段"这一个作用——那是它本来的职责。
        /// </summary>
        internal static void Pump()
        {
            // 待播（已载入、还没重开关卡）时不注入：关卡还停在结算画面，
            // 歌曲位置早就跑完整个回放了，这时注入只会把游标一次性冲到底。
            if (_data == null || Pending)
            {
                return;
            }
            if (_data.Keys.Count == 0 && !HitStateReplayer.HasTrack)
            {
                return;
            }
            // UpdateInput 一帧可能被调多次（多个状态机分支都会走到）。
            // 按 Time.frameCount 挡掉重复，否则同一批按键会被注入两遍——
            // 表现为"某个键莫名多按了一下"。
            if (_lastFrame == Time.frameCount)
            {
                return;
            }
            _lastFrame = Time.frameCount;

            var conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return;
            }
            double pos;
            try
            {
                pos = conductor.songposition_minusi;
            }
            catch
            {
                return;
            }
            // 位置回退 = 重开/跳关：游标跟着退，否则会一次性注入积压的一大串
            if (pos < _position - 0.001)
            {
                _cursor = 0;
                while (_cursor < _data.Keys.Count
                    && _data.Keys[_cursor].SongPosition < pos)
                {
                    _cursor++;
                }
                // 状态轨游标同样要退：它靠"地板序号对不上就快进"来纠偏，
                // 而重开时地板序号会**从低序号重新开始**，纠偏方向正好相反。
                // 不退的话游标停在末尾，重开后一整局都不再推进。
                HitStateReplayer.Reset();
            }
            _position = pos;

            // ── 轨一：按键 ────────────────────────────────────
            var keys = _data.Keys;
            Staged.Clear();
            while (_cursor < keys.Count && keys[_cursor].SongPosition <= pos)
            {
                var ev = keys[_cursor++];
                KeyboardSimulation.Stage(ev.Key, ev.IsPressed, Staged);
            }
            if (Staged.Count > 0)
            {
                KeyboardSimulation.Send(Staged);
            }
            Staged.Clear();

            // ── 轨二：状态 ────────────────────────────────────
            // 必须在按键之后（见方法说明）。
            HitStateReplayer.Pump(scrController.instance);

            // 结束时把仍按着的键补松掉，再卸数据——顺序不能反。
            //
            // 两条轨**都跑完**才算结束（照 Spectre Patch_Playback.cs:160-161
            // 的 `PlayIndex >= HitContext_list.Count && KeyEventIndex >= Count`）。
            // 少一条轨的结束条件就是"回放提前停掉"：状态轨还没走完就卸数据，
            // 后半段的行星状态就停在录制中途的样子，画面是错的。
            bool keysDone = _cursor >= keys.Count;
            bool hitsDone = !HitStateReplayer.HasTrack
                || HitStateReplayer.Cursor >= _data.Hits.Count;
            if (keysDone && hitsDone && pos > _data.Length + 1.5)
            {
                Debug.Log("[SpectreReplay] 回放结束");
                Stop();
            }
        }
    }

    /// <summary>把回放泵接到输入处理之前。</summary>
    internal static class Patch_Playback
    {
        /// <summary>
        /// 挂在 <c>scrController.PlayerControl_Update</c> 的 **Postfix**，而不是
        /// <c>UpdateInput</c>。
        ///
        /// **换挂载点的原因（回放完全不动的直接原因）：**
        /// UpdateInput 第一行就是
        ///     if (!RDInput.asyncKeyboard.isActive &amp;&amp; !...Left... &amp;&amp; !...Right...)
        ///         return;                                    // scrController.cs:2885
        /// 而 asyncKeyboard.isActive 只在玩家**自己去设置里勾了异步输入**才为真
        /// （AsyncInputManager.Update:147）。玩家用默认输入 → 恒为 false →
        /// UpdateInput 整个函数体都不执行。**把泵挂在它上面，等于挂在一个
        /// 永远提前 return 的方法上**，回放泵一帧都不会被调用。
        ///
        /// 连带影响：即使那个门打开了，注入 AsyncInputManager.keyQueue 也只在
        /// 异步输入开启时才有人读——所以注入方式一并改成了 Win32 SendInput
        /// （见 KeyboardSimulation）。
        ///
        /// PlayerControl_Update 是"游玩中每帧"的统一点，且不受异步输入开关影响
        /// （它在 !isActive 时调 Simulated_PlayerControl_Update，那是玩家的
        /// 模拟输入，与我们无关）。
        ///
        /// 同一个补丁也挂到 Countdown_Update / Checkpoint_Update：倒计时与检查点
        /// 阶段 PlayerControl_Update 不会被调用，而回放从倒计时就该开始。
        /// 照 Spectre（Features/Replay/Patch_Playback.cs 的
        /// Patch_PlayerControlUpdate，三个 [SpectrePatch] 挂同一方法）。
        /// </summary>
        [GamePatch(typeof(scrController), "PlayerControl_Update", PatchKind.Postfix, "playback")]
        [GamePatch(typeof(scrController), "Countdown_Update", PatchKind.Postfix, "playback")]
        [GamePatch(typeof(scrController), "Checkpoint_Update", PatchKind.Postfix, "playback")]
        private static void PumpPostfix()
        {
            try
            {
                PlaybackEngine.Pump();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 回放泵异常：" + ex);
                PlaybackEngine.Stop();
            }
        }
    }
}