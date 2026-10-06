using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender;
using SpectreReplay.Ui;
using UnityEngine;
using static SpectreReplay.Settings;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 设置页内容（UMM OnGUI）。外观一律走 <see cref="Gui"/> 的分组卡片，
    /// 这里只负责"哪一页有哪些行"。
    ///
    /// **三条规矩，每一条都对应一类真实故障：**
    ///   1. 玩家可见的文字一律进 Loc 表。写死中文的话，切成英文的玩家
    ///      会在英文界面里看到夹进来的中文，而这在中文环境下永远看不出。
    ///   2. 每个控件行必须真的接到一个设置字段。"画了一行但没接线"比不画更糟：
    ///      玩家以为改了什么，实际什么都没发生。
    ///   3. 页面体一律包 try。UMM 对模组 OnGUI 的异常处理是
    ///      LogException + ShowModSettings = -1 + GUIUtility.ExitGUI()，
    ///      等于**整个设置面板当场消失**——玩家的感受是"点一下窗口没了"。
    /// </summary>
    internal static class Pages
    {
        /// <summary>
        /// 当前载入的回放文件。
        ///
        /// 自己记而**不去问回放引擎**：LegacyReplayData 只装数据，
        /// 不记得自己来自哪个文件。列表里那个"● 已载入"标记要靠它，
        /// 否则玩家放了哪一个只能靠猜。
        /// </summary>
        private static string _loadedPath = "";

        // 回放列表缓存与刷新时刻（TickCount 差值按无符号比较，回绕安全）。
        private static List<string> _replayCache;
        private static int _replayCacheTick = int.MinValue;

        // ── 录制 ────────────────────────────────────────────────
        internal static void DrawRecord()
        {
            Gui.Section("page.record", Gui.Accent, () =>
            {
                OnGui.Note("record.howto");
                Gui.Divider();
                OnGui.Row("record.recording", ReplayOn, v => { ReplayOn = v; Commit(); });
                OnGui.Row("record.autosave", AutoSave, v => { AutoSave = v; Commit(); });
                // 保存策略七项：**每一项都真的在控制回放什么时候落盘**
                // （Patch_Recording.cs:136/137/139/142/163、SpectreState.cs:254、
                // ReplayRecorder.cs:66、Patch_Lifecycle.cs:92/261），而原版界面上
                // 一个都改不了——存档里那七项只可能是默认值。玩家最常见的两个诉求
                // 正好都被默认值挡着：想存"打了一半的练习"（CompleteSave 默认
                // true ⇒ 非完整通关直接不存）、想存"失手了的一局"
                // （DontSaveWhenMiss 默认 true ⇒ 有 miss 直接不存）。
                OnGui.Row("record.completesave", CompleteSave, v => { CompleteSave = v; Commit(); });
                OnGui.Row("record.dontsavewhenauto", DontSaveWhenAuto, v => { DontSaveWhenAuto = v; Commit(); });
                OnGui.Row("record.dontsavewhenmiss", DontSaveWhenMiss, v => { DontSaveWhenMiss = v; Commit(); });
                OnGui.Row("record.latesave", LateSave, v => { LateSave = v; Commit(); });
                OnGui.Row("record.failsave", FailSave, v => { FailSave = v; Commit(); });
                OnGui.Row("record.backup", Backup, v => { Backup = v; Commit(); });
                OnGui.Row("record.limitkey", LimitKey, v => { LimitKey = v; Commit(); });
                // 这三项都是**真的行为开关**（不是摆设）：ManualSave 管 F9 手动保存
                // 那条补丁挂不挂（Main.cs:96 + Patch_Misc.cs:50）、GetDataFromAsyncInput
                // 管按键从异步输入还是轮询取（Main.cs:98 + AsyncKeyRecorder.cs:43）、
                // FollowGameLanguage 管界面语言是否跟着游戏走（Settings.cs:375 +
                // LocalizationManager.cs:5915）。之前界面上一个都没有，改它们只能手改
                // Configs.json——而 ManualSave 关掉后 F9 直接失效，玩家会以为按键没绑上。
                OnGui.Row("record.manualsave", ManualSave, v => { ManualSave = v; Commit(); });
                OnGui.Row("record.asyncinput", GetDataFromAsyncInput, v => { GetDataFromAsyncInput = v; Commit(); });
                OnGui.Row("record.followlang", FollowGameLanguage, v => { FollowGameLanguage = v; Commit(); });
                Gui.Divider();
                // 把**真实**目录摆出来。原先只在 record.note 里写了一句
                // "保存在 Mods/SpectreReplay/Replays/"，而 SavePath 实际是模组根
                // （modEntry.Path），回放散在根目录里、和 dll 混在一起——玩家照
                // 那句话去 Replays/ 找只会看到空目录，以为保存没成功。
                Gui.KeyValue("record.savedir", SaveDirText(), Gui.DimCol);
            });

            Gui.Section("record.hotkeys", Gui.Accent, () =>
            {
                // 按任意键改键：等待期间 Esc 取消并独占 Esc（见 OnGui.RowHotkey）。
                // 这两个键是**手动存这一局**的入口——没有它，练到一半的短局
                // 只能等通关自动保存，等于存不下来。
                OnGui.RowHotkey("record.savekey", () => SaveButton, k => { SaveButton = k; Commit(); });
                OnGui.RowHotkey("record.cancelsavekey", () => CancelSaveButton, k => { CancelSaveButton = k; Commit(); });
                OnGui.Note("record.note");
            });

            // **键盘音三项：原版 Settings 有字段、ConfigManager 也落盘、
            // Patch_Playback 也真的在消费，却没有任何一行界面能改。**
            // 存档里那三项只可能是默认值——尤其 MicrophoneOffset 是**校准值**
            // （毫秒）：键盘声整体偏早/偏晚时它是唯一的修正手段，玩家没得调，
            // 只能去手改 Configs.json，而他从界面上看不出有这一项。
            // 与 DrawRenderOptions 那次是同一个病：数据在、链路通、界面缺。
            Gui.Section("record.keybd", Gui.Accent, () =>
            {
                OnGui.Row("record.keybd.record", KeybdSoundRecordActive,
                    v => { KeybdSoundRecordActive = v; Commit(); });
                // 音量：内部 0..100，Patch_Playback 用它 /10f 当 AudioSource.volume
                OnGui.RowSlider("record.keybd.volume", KeybdSoundVolume, 0f, 100f,
                    v => ((int)v) + " %", v => { KeybdSoundVolume = (int)v; Commit(); });
                // 偏移：毫秒，可正可负（PSlider 支持负区间）
                OnGui.RowSlider("record.keybd.offset", MicrophoneOffset, -500f, 500f,
                    v => ((int)v) + " ms", v => { MicrophoneOffset = (int)v; Commit(); });
                OnGui.Note("record.keybd.note");
            });
        }

        // ── 回放 ────────────────────────────────────────────────
        internal static void DrawPlayback()
        {
            // **回放驱动开关必须放在回放页第一屏。** Settings.LegacyEngine 名字骗人——
            // 它管的不是"旧引擎"，而是**整个按键模拟回放链路**：
            //   ReplayPlayer.cs:38  关掉后停止回放时**不会释放已按住的键**（键会卡住）
            //   ReplayPlayer.cs:133 关掉后回放时**一个键都不注入**（画面毫无反应）
            // 上游 Spectre 把它当一等公民摆在"回放设置"首页，标签就叫
            // 「模拟按键 / Simulate Key Press」（SpectreUiPages.More.cs:160）。
            // 我们移植时丢了 UI，于是这个开关只剩 Configs.json 一条路——
            // 而它恰好是"回放没反应"时最该被检查的一项。
            Gui.Section("playback.drive", Gui.Accent, () =>
            {
                OnGui.Row("playback.simulate", LegacyEngine, v => { LegacyEngine = v; Commit(); });
                OnGui.Note("playback.drive.note");
            });

            Gui.Section("playback.current", Gui.OkCol, () => DrawPlaybackState());

            var files = ReplayList();
            Gui.Section("playback.files", Gui.Accent, () =>
            {
                if (files.Count == 0)
                {
                    OnGui.Note("playback.empty");
                    return;
                }
                int shown = 0;
                foreach (string path in files)
                {
                    // 只列最近 40 个：回放攒到几百个时把整页全画出来，
                    // 设置页会长到没法用，而真正要放的多半就是最近那几局。
                    if (shown++ >= 40)
                    {
                        break;
                    }
                    string captured = path;
                    bool loaded = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase);
                    GUILayout.BeginHorizontal();
                    if (loaded)
                    {
                        Gui.Badge(GUILayoutUtility.GetRect(46f, 20f,
                            GUILayout.Width(46f), GUILayout.Height(20f)),
                            Loc.T("playback.loaded"), Gui.OkCol);
                    }
                    GUILayout.Label(Path.GetFileNameWithoutExtension(path),
                        loaded ? Gui.BoldStyle : Gui.LabelStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(Loc.T("playback.replay"), Gui.ButtonStyle,
                            GUILayout.Width(110f), GUILayout.Height(22f)))
                    {
                        LoadReplayFile(captured);
                    }
                    GUILayout.EndHorizontal();
                }
            });
        }

        private static void LoadReplayFile(string path)
        {
            try
            {
                if (!Features.Replay.ReplayManager.LoadReplayAuto(
                        out LegacyReplayData loaded, path))
                {
                    Status.Warn("playback.idle_err", Path.GetFileNameWithoutExtension(path));
                    return;
                }
                data = loaded;
                _loadedPath = path;
                // **只装弹、不发射。** PlayMode = true 只是"下一次 Start_Rewind 时
                // 走 TryStartPlayback"——而 Start_Rewind 只在【重开/换关/进关】时
                // 由游戏调用。也就是说点完这个按钮，玩家必须自己再进一次关卡，
                // 回放才会开始。
                // 原先这里打一句 status.loaded（「已载入回放」），读起来像"已经
                // 开始了"，而画面毫无变化——玩家点一下什么都没发生，
                // 而这正是"点了没反应"那一类。
                // playback.ready / status.pending_replay 两条文案从建库起就在表里，
                // 说的都是"重开这关才会自动播放"。这里说清楚，并给出直接重开的入口。
                PlayActions.SwitchToPlayMode();
                Status.Say(PlayMode && !is_playing ? "status.pending_replay" : "status.loaded",
                    Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 载入回放失败：" + ex);
            }
        }

        /// <summary>
        /// 「当前」这一组。**任何时候都有话说**：
        /// 播放中说"正在播放：某某"并给停止入口，录制中说"正在录制"并给放弃入口，
        /// 其余说"还没载入回放"。
        ///
        /// 原先这一整块只在 is_playing 时才画，玩家看到的是"这页上面什么都没有"，
        /// 分不清是没载入、载入了没生效、还是校验把它拒了——
        /// 而这三件事的处理方式完全不同。
        /// </summary>
        private static void DrawPlaybackState()
        {
            if (is_playing)
            {
                string song = "?";
                if (data != null && data.strings.TryGetValue(SongName, out string sn)
                    && !string.IsNullOrEmpty(sn))
                {
                    song = sn;
                }
                GUILayout.Label(Loc.F("playback.armed", song), Gui.LabelStyle);
                if (GUILayout.Button(Loc.T("playback.stop"), Gui.ButtonStyle,
                        GUILayout.Width(140f), GUILayout.Height(26f)))
                {
                    PlayActions.StopPlaying();
                }
                return;
            }
            if (is_recording)
            {
                GUILayout.Label(Loc.T("status.recording"), Gui.LabelStyle);
                // **这个按钮以前绑的是 record.cancelsavekey「取消保存」。**
                // 那是另一个动作（CancelSave：丢掉这一局待存的数据），绑在这里的
                // 后果是玩家想"停止录制"，看到的是"取消保存"——点了之后录制
                // 确实也停了，但本局数据被标成不再保存，玩家以为自己只是停了
                // 一下，回放却永远不出现。两个动作必须分开：这里是停止录制。
                if (GUILayout.Button(Loc.T("record.stop"), Gui.ButtonStyle,
                        GUILayout.Width(140f), GUILayout.Height(26f)))
                {
                    PlayActions.StopRecording();
                }
                return;
            }
            GUILayout.Label(Loc.T("playback.idle"), Gui.NoteStyle, GUILayout.ExpandWidth(true));
            DrawArmedHint();
            DrawTimingSummary();
        }

        /// <summary>
        /// 已装弹（PlayMode）但还没开始播：说清楚"下一步该干什么"。
        ///
        /// 回放的启动时机是 **Start_Rewind**（重开/换关/进关时由游戏调用），
        /// 而设置页里点「回放」只把数据装好。原先装完只说一句"已载入"，
        /// 没有这个状态行，玩家点完按钮画面毫无变化——既不知道要重开，
        /// 也看不出到底装上了没有。
        ///
        /// 注意这里**不画 timing 摘要**：调用方紧接着就画，嵌在里面会出两遍。
        /// </summary>
        private static void DrawArmedHint()
        {
            if (!PlayMode || data == null)
                return;
            string song = data.strings.TryGetValue(SongName, out string sn) && !string.IsNullOrEmpty(sn)
                ? sn : "?";
            GUILayout.Space(2f);
            GUILayout.Label(Loc.F("playback.ready", song), Gui.NoteStyle, GUILayout.ExpandWidth(true));
        }

        /// <summary>
        /// 已载入回放的命中误差摘要。
        ///
        /// **为什么要在这里动手。** ReplayAnalyzer.Analyze 随移植搬了过来（纯计算、
        /// 不碰引擎），但 SpectreReplay 侧**一个调用点都没有** —— 那一整块"整体偏早/
        /// 偏晚、中位数、标准差"是玩家打完一局最想知道的事，而它对玩家等于不存在。
        ///
        /// 只在**已载入回放且关卡已加载**时显示：Analyze 的返回 null 带有
        /// need_level（没进关）/ no_data（没有可分析的命中）两种本地化键后缀，
        /// 都不该在这里刷成一行错误信息——回放页没进关是常态。
        /// </summary>
        private static void DrawTimingSummary()
        {
            if (data == null)
                return;
            string reason;
            ReplayAnalyzer.Report rep;
            try
            {
                rep = ReplayAnalyzer.Analyze(data, out reason);
            }
            catch (Exception ex)
            {
                // 分析纯属附加信息，任何异常都不该把整个设置页带走
                // （OnGUI 抛异常 = UMM 收掉整个面板）
                Debug.LogWarning("[SpectreReplay] 命中误差分析失败：" + ex.Message);
                return;
            }
            if (rep == null)
                return;

            GUILayout.Space(4f);
            Gui.Divider();
            GUILayout.Label(Loc.T("playback.timing"), Gui.LabelStyle);
            GUILayout.Label(Loc.F("playback.timing_summary",
                rep.AnalyzedHits, rep.EarlyCount, rep.LateCount, SignMs(rep.MedianMs)),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label(Loc.F("playback.timing_detail",
                SignMs(rep.MeanMs), rep.SigmaMs.ToString("0.0") + "ms",
                SignMs(rep.MinMs), SignMs(rep.MaxMs)),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            int skipped = rep.TotalHits - rep.AnalyzedHits;
            if (skipped > 0)
            {
                GUILayout.Label(Loc.F("playback.timing_skipped", skipped),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>毫秒值统一带符号：-1.2ms / +0.8ms。纯 ASCII 与数字，不进文案表。</summary>
        private static string SignMs(float ms)
            => (ms >= 0f ? "+" : "") + ms.ToString("0.0") + "ms";

        // ── 视频渲染 ────────────────────────────────────────────
        internal static void DrawRender()
        {
            Gui.Section("render.enable", Gui.Accent, () =>
            {
                // **这一行以前绑的是 ReplayOn**（录制总开关）。后果有两层：
                // 关掉"视频渲染"会把录制/回放一起关掉，而这一页又没有任何
                // 能真正关掉渲染的开关。现在绑 RenderOn，与录制页彻底分开。
                OnGui.Row("render.on", RenderOn, v => { RenderOn = v; Commit(); });
                Gui.Divider();
                DrawRenderState();
            });

            if (RenderOn && !Features.VideoRender.VideoRender.Active)
            {
                Gui.Section("render.start", Gui.Accent, () =>
                {
                    OnGui.Button("render.start", StartRender, true);
                    OnGui.Note("render.note.offline");
                });
            }
            else if (Features.VideoRender.VideoRender.Active)
            {
                Gui.Section("render.cancel", Gui.WarnCol, () =>
                {
                    OnGui.Button("render.cancel",
                        () => Features.VideoRender.VideoRender.CancelRender(), false);
                });
            }

            Gui.Section("render.options", Gui.Accent, () => DrawRenderOptions());

            // 转换已有视频放在最后：它是"渲染完再做的事"，与渲染参数不是一类。
            DrawConvertSection();
        }

        /// <summary>启动渲染。前置判据全部交给服务层（它会自己弹"先载入回放"、
        /// "先进入关卡"、"找不到 ffmpeg"、"渲染已在设置中关闭"）。</summary>
        private static void StartRender()
        {
            try
            {
                Features.VideoRender.VideoRender.StartRender();
            }
            catch (Exception ex)
            {
                // 这里绝不能让它抛出去：OnGUI 里抛异常，UMM 会把整个设置
                // 面板收掉，玩家看到的是"点一下窗口没了"，而不是一条错误。
                Debug.LogError("[SpectreReplay] 启动渲染失败：" + ex);
            }
        }

        /// <summary>
        /// 渲染状态：不在跑就说上一次的结果（或空闲），在跑就画阶段 + 进度条。
        ///
        /// 关键是**不在渲染时不能说"完成"**——那会让"从来没渲染过"和
        /// "刚渲染完一局"看起来一模一样。
        /// </summary>
        private static void DrawRenderState()
        {
            var s = Features.VideoRender.VideoRender.Current;
            if (!Features.VideoRender.VideoRender.Active)
            {
                if (!string.IsNullOrEmpty(s.Error))
                {
                    GUILayout.Label(s.Error, Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    return;
                }
                string last = string.IsNullOrEmpty(s.Output)
                    ? Loc.T("status.idle")
                    : Loc.F("status.render_done", Path.GetFileName(s.Output));
                GUILayout.Label(last, Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }

            string stage = string.IsNullOrEmpty(s.Stage) ? Loc.T("render.active") : s.Stage;
            GUILayout.Label(stage, Gui.LabelStyle);
            if (s.FramesTotal > 0)
            {
                Gui.ProgressBar(s.Progress,
                    s.FramesDone + " / " + s.FramesTotal + "   "
                    + (s.Progress * 100f).ToString("F0") + "%");
                if (s.EtaSeconds >= 0.0)
                {
                    GUILayout.Label(Loc.F("render.eta", FormatEta(s.EtaSeconds)), Gui.NoteStyle);
                }
            }
        }

        /// <summary>秒 → "1m30s" / "45s"。纯数字与 ASCII 单位，不进文案表。</summary>
        private static string FormatEta(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            return ts.TotalMinutes >= 1
                ? (int)ts.TotalMinutes + "m" + ts.Seconds.ToString("00") + "s"
                : (int)ts.TotalSeconds + "s";
        }

        /// <summary>
        /// 渲染参数。**每个控件都真的接到一个 Settings 字段上。**
        ///
        /// 编码器 / 画质 / 结尾秒数在原版里 Settings 有字段、ConfigManager 也
        /// 落盘，却**没有任何一行界面能改它们**——存档里那几项只可能是默认值，
        /// 玩家改了没得反应，还以为是自己没找到。
        /// </summary>
        private static void DrawRenderOptions()
        {
            OnGui.RowSlider("render.width", RenderWidth, 320f, 7680f,
                v => ((int)v) + " px", v => { RenderWidth = (int)v; Commit(); });
            OnGui.RowSlider("render.height", RenderHeight, 240f, 4320f,
                v => ((int)v) + " px", v => { RenderHeight = (int)v; Commit(); });
            OnGui.RowSlider("render.fps", RenderFps, 15f, 480f,
                v => ((int)v) + " fps", v => { RenderFps = (int)v; Commit(); });
            OnGui.RowSlider("render.tail", RenderTailSeconds, 0f, 15f,
                v => v.ToString("0.#") + " s", v => { RenderTailSeconds = v; Commit(); });
            OnGui.RowDropdown("render.surface", new[]
            {
                Loc.T("render.surface.clean"), Loc.T("render.surface.screen"),
            }, RenderCaptureMode, i => { RenderCaptureMode = i; Commit(); });
            OnGui.RowDropdown("render.codec", new[]
            {
                Loc.T("render.codec.auto"), Loc.T("render.codec.fastest"),
                Loc.T("render.codec.quality"), Loc.T("render.codec.cpu"),
            }, RenderEncoderMode, i => { RenderEncoderMode = i; Commit(); });
            OnGui.RowDropdown("render.quality", new[]
            {
                Loc.T("render.quality.low"), Loc.T("render.quality.standard"),
                Loc.T("render.quality.high"), Loc.T("render.quality.extreme"),
            }, RenderQualityMode, i => { RenderQualityMode = i; Commit(); });
            // 格式名（mp4/mkv/webm/mov）保持原样：那是实际的文件格式，
            // 翻译了反而对不上。索引与 VideoEncoder.OutContainer 一一对应。
            OnGui.RowDropdown("render.format", new[] { "mp4", "mkv", "webm", "mov" },
                RenderOutputFormat, i => { RenderOutputFormat = i; Commit(); });
            // 输出目录：这一项对应 **Settings.RenderOutputDir**（由 OnGui.RowInput 的
            // 共享 _textDraft 读写，收口在 OnGui.CommitText / FlushConfig）。
            // **为什么要在注释里显式写出字段名**：RowInput 只收一个文案键，控件与
            // 字段之间只靠约定连着——连我自己的 settings-ui-audit 都追不到，一度把
            // 这一项误报成"无界面控件"。写出来之后 grep / 审计都能顺藤摸到，
            // 也就不必为了可审计性去改 RowInput 的签名（它本质是单实例共享草稿，
            // 加字段参数反而会暗示一个它并不具备的绑定能力）。
            OnGui.RowInput("render.outdir");
            DrawEncoderNote();
        }

        /// <summary>
        /// 本局渲染实际会用的编码器。
        ///
        /// **为什么必须画出来。** <c>VideoEncoder.DescribeEncoder</c> 在移植里
        /// 一个调用点都没有：选「最快」时若 NVENC 探测失败，编码器会静默落到
        /// libx264(CPU)，渲染速度差一个数量级，而界面上看不到任何差别——
        /// 玩家只会觉得"这台机器跑不动"，去改画质/帧率那些无关的设置。
        /// 这一行只读状态、不起探测进程（DescribeEncoder 复用 NVENC 的已缓存结论）。
        /// </summary>
        private static void DrawEncoderNote()
        {
            try
            {
                var container = (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3);
                string name = VideoEncoder.DescribeEncoder(
                    (VideoEncoder.Quality)Mathf.Clamp(RenderEncoderMode, 0, 3), container);
                Gui.KeyValue("render.codec.current", name, Gui.DimCol);
            }
            catch (Exception ex)
            {
                // 纯提示信息：探测失败不该把整个设置页带走
                Debug.LogWarning("[SpectreReplay] 编码器提示失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 转换已有视频：VideoConverter 随移植搬了过来，但**一直零调用点**。
        /// Spectre 是在它的 uGUI 设置页里调的（SpectreUiPages.Features.cs:778），
        /// 我们没有那套页面，于是"批量转格式"这个功能对玩家等于不存在。
        ///
        /// 这里给一个轻量入口：只认上面那个输出目录，目标容器就是 render.format
        /// 当前选的那个。不另开目录选择器——那是另一个功能，不是这个缺口的一部分。
        /// </summary>
        private static void DrawConvertSection()
        {
            Gui.Section("convert.title", Gui.Accent, () =>
            {
                OnGui.Note("convert.hint");
                Gui.Divider();
                // 目标格式直接复用 render.format 的选择：不引入第二处格式来源。
                // 两处各选一个的话，"选 webm 却转出 mp4" 这类事必然发生。
                Gui.KeyValue("render.format",
                    VideoEncoder.ExtensionOf(
                        (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3)),
                    Gui.DimCol);

                var st = _convertState;
                if (st != null && st.Running)
                {
                    // 只读 snapshot：ConvertState 用 volatile 字段，Running=false 之后
                    // 才读 Messages（那边不是线程安全的）
                    GUILayout.Label(Loc.F("convert.running", st.Done, st.Total,
                        st.Converted, st.Skipped, st.Failed), Gui.NoteStyle,
                        GUILayout.ExpandWidth(true));
                    return;
                }

                if (st != null)
                {
                    // 上一次的结果。转换是在后台线程跑的，失败详情在 Messages 里
                    string summary = Loc.F("convert.done",
                        st.Converted, st.Skipped, st.Failed);
                    if (st.Error != null)
                        summary += "\n" + st.Error.Message;
                    else if (st.Messages.Count > 0)
                        summary += "\n" + string.Join("\n", st.Messages);
                    GUILayout.Label(summary, Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    Gui.Divider();
                }

                OnGui.Button("convert.button", StartConvert, false);
            });
        }

        // 转换状态。null = 还没转过一次。只在主线程读写，后台线程只写它内部的
        // volatile 字段（ConvertState 的契约）
        private static VideoConverter.ConvertState _convertState;

        private static void StartConvert()
        {
            try
            {
                if (_convertState != null && _convertState.Running)
                    return;
                string dir = string.IsNullOrEmpty(RenderOutputDir)
                    ? Path.Combine(SavePath, "RenderVideos")
                    : RenderOutputDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Status.Warn("convert.err.no_dir");
                    return;
                }
                string ff = VideoEncoder.LocateEncoder();
                if (ff == "ffmpeg" && !VideoEncoder.PathProbeSucceeded())
                {
                    Status.Warn("convert.err.no_ffmpeg");
                    return;
                }
                _convertState = VideoConverter.Start(dir,
                    (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3), ff);
            }
            catch (Exception ex)
            {
                // OnGUI 里抛异常 = UMM 收掉整个设置面板
                Debug.LogError("[SpectreReplay] 启动转换失败：" + ex);
            }
        }

        // ── 关于 ────────────────────────────────────────────────
        internal static void DrawAbout()
        {
            Gui.Section("page.about", Gui.Accent, () =>
            {
                OnGui.Note("about.note");
                Gui.Divider();
                Gui.KeyValue("about.version", VersionText(), Gui.TextCol);
                Gui.KeyValue("about.engine", Loc.T("about.engine.value"), Gui.DimCol);
                Gui.KeyValue("about.path", SaveDirText(), Gui.DimCol);
            });
        }

        private static string VersionText()
        {
            try
            {
                return Main.mod != null && Main.mod.Info != null
                    ? Main.mod.Info.Version.ToString() : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string SaveDirText()
        {
            try
            {
                string dir = SavePath;
                if (string.IsNullOrEmpty(dir) && Main.mod != null)
                {
                    dir = Main.mod.Path;
                }
                return string.IsNullOrEmpty(dir) ? "?" : dir;
            }
            catch
            {
                return "?";
            }
        }

        // ── 工具 ────────────────────────────────────────────────
        /// <summary>拖动滑条期间被推迟的落盘。见 Commit 的说明。</summary>
        private static bool _pendingCommit;

        /// <summary>
        /// 改一个设置后落盘 + 刷新补丁开关。
        ///
        /// 必须包 try：OnGUI 里抛异常会被 UMM 吞掉并把**整个设置面板收掉**，
        /// 表现是"点一下窗口没了"，而不是一条错误。
        ///
        /// **拖动滑条时只记待办、不落盘。** 滑条的值每帧都在变，onChange 于是每帧
        /// 都调到这里；一次拖动 = 几十次"全量序列化 + Settings.check（解析键表 +
        /// 256 次循环）+ 全量 Refresh + 写文件"。现场观感就是拖动一顿一顿的，
        /// 而这跟"设置存不下来"是两回事，很容易被当成同一个毛病去修。
        /// 字段本身当场就改了（滑条即时生效），所以推迟的只是**磁盘写入**；
        /// 松手那一帧由 FlushPending 补上。只推迟"按住鼠标"期间：按钮与下拉
        /// 在 MouseUp 那帧才触发，那帧 GetMouseButton(0) 已经是 false，照旧只写一次。
        /// </summary>
        private static void Commit()
        {
            if (Input.GetMouseButton(0))
            {
                _pendingCommit = true;
                return;
            }
            SaveNow();
        }

        /// <summary>补上拖动期间被推迟的落盘。force = 关窗/保存/退出时必须落地。
        /// 返回是否真的写了——调用方据此避免紧接着又写一遍。</summary>
        internal static bool FlushPending(bool force = false)
        {
            if (!_pendingCommit)
            {
                return false;
            }
            if (!force && Input.GetMouseButton(0))
            {
                return false;
            }
            SaveNow();
            return true;
        }

        private static void SaveNow()
        {
            _pendingCommit = false;
            try
            {
                if (Main.mod != null && !string.IsNullOrEmpty(Main.mod.Path))
                {
                    ConfigManager.SaveConfigs(Path.Combine(Main.mod.Path, "Configs.json"));
                    Settings.check(Main.mod.Path);
                }
                SpectrePatchHub.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 提交设置失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 回放文件列表，按修改时间倒序。
        ///
        /// **必须带缓存。** 这个方法是在 IMGUI 每帧（一帧要跑 Layout / Repaint 好几趟）
        /// 画列表的回调里调的；原实现每趟都 Directory.GetFiles + Array.Sort，而那个
        /// 比较器每次都调 File.GetLastWriteTimeUtc —— 等于每帧 O(n log n) 次系统调用。
        /// 回放攒到几百个时，光是停在这一页就会持续掉帧，而且很容易被误判成
        /// “渲染在跑”/“游戏卡”。缓存一秒：开销归零，新录一局后一秒内也会出现。
        ///
        /// 返回的是缓存本体，**调用方只许读、不要改**。
        /// </summary>
        internal static List<string> ReplayList()
        {
            if (_replayCache != null
                && (uint)(Environment.TickCount - _replayCacheTick) < 1000u)
            {
                return _replayCache;
            }
            var list = new List<string>();
            try
            {
                string dir = SavePath;
                if (string.IsNullOrEmpty(dir) && Main.mod != null)
                {
                    dir = Main.mod.Path;
                }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    _replayCache = list;
                    _replayCacheTick = Environment.TickCount;
                    return list;
                }
                // **三种格式都要列，不能只列默认那一种。**
                // 原实现只 glob `ReplayConstants.Extension`（.sprp，New 格式）。
                // 可 `LoadReplayAuto` 是**三种格式全能读**的（.sprp / .crpl2 /
                // .crpl 三套 magic 都有分支），而 .crpl2/.crpl 文件在列表里
                // **一个都不出现**——玩家从 Spectre 拷过来的回放、或自己把
                // `debug_save_format` 改成 Crp2 录的回放，全都"看得见文件、
                // 列表里没有"。点不了就不是不能用，而是根本找不到。
                // 扩展名仍从常量取，不写死字面量（格式一改这里会静默失效）。
                var files = Features.Replay.ReplayManager.ReadableExtensions
                    .SelectMany(ext => Directory.GetFiles(dir, "*" + ext))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Array.Sort(files, (a, b) =>
                    File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                list.AddRange(files);
            }
            catch
            {
                // 目录不可读就当作没有回放
            }
            _replayCache = list;
            _replayCacheTick = Environment.TickCount;
            return list;
        }
    }
}
