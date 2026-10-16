using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender;
using SpectreReplay.Ui;
using UnityFileDialog;
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
                //
                // **它同时是唯一能改保存目录的入口**，对应 Settings.SavePath。
                // 此前这一行只是一句只读的 Gui.KeyValue：数据在（Settings 里有
                // 字段）、链路通（ConfigManager 落盘、SpectreState 真的按它写
                // 文件），却没有任何一行界面能改它——想换个盘放回放只能去手改
                // Configs.json，而从界面上看不出有这一项。与 ⑨/⑩ 是同一类病。
                DrawSaveDirRow();
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
            DrawOpenLevelRow();
        }

        /// <summary>
        /// 「打开这一关」按钮。从已载入回放里读 <c>level_path</c>，一键跳到它录自的那一关。
        ///
        /// **为什么需要它。** 回放只在"重开/换关/进关"时才启动（见
        /// <see cref="OpenReplayLevel"/> 的说明），而练习谱/自制谱要自己找回去
        /// 很麻烦——玩家录完一局、退出、回放页点载入，然后只能靠记忆翻关卡列表。
        /// 上游 Spectre 有这颗按钮（SpectreUiPages.More.cs:98-121），我们移植时丢了。
        ///
        /// **只对"已装弹的回放"显示**（<c>PlayMode &amp;&amp; data != null</c>）：
        /// 录制模式下 data 也在记录，不加这个门槛的话自己打一局也会冒出这颗按钮。
        /// </summary>
        private static void DrawOpenLevelRow()
        {
            if (!PlayMode || data == null)
            {
                return;
            }
            if (!data.strings.TryGetValue(LevelPath, out string levelPath)
                || string.IsNullOrEmpty(levelPath))
            {
                return;
            }

            GUILayout.Space(4f);
            // 官方关卡没有可打开的谱面文件：说清楚，别给一颗点了没反应的按钮。
            bool official = levelPath.StartsWith("OfficialLevel", StringComparison.Ordinal)
                || (data.bools.TryGetValue(IsOfficialLevel, out bool iso) && iso);
            if (official)
            {
                string internalName = data.strings.TryGetValue(InternalLevelName, out string nm) ? nm : "";
                GUILayout.Label(Loc.T("playback.openlevel.official")
                    + (string.IsNullOrEmpty(internalName) ? levelPath : internalName),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }
            if (!File.Exists(levelPath))
            {
                GUILayout.Label(Loc.T("playback.openlevel.missing") + ClipMiddle(levelPath, 46),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }

            // 录自编辑器就回编辑器、录自关卡就回关卡——两边起跳逻辑不同
            string recScene = data.strings.TryGetValue(SceneName, out string sc) ? sc : "";
            bool editorReplay = recScene == "scnEditor";
            string captured = levelPath;
            if (GUILayout.Button(Loc.T(editorReplay ? "playback.openlevel.editor" : "playback.openlevel"),
                    Gui.ButtonStyle, GUILayout.Width(160f), GUILayout.Height(24f)))
            {
                try
                {
                    OpenReplayLevel.Go(captured, editorReplay);
                }
                catch (Exception ex)
                {
                    // 跳转失败（谱面损坏/场景状态异常）不该把整个设置页带走
                    Debug.LogWarning("[SpectreReplay] 打开关卡失败：" + ex.Message);
                    Status.Warn("playback.openlevel.busy");
                }
            }
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

            // ── 命中分布图 ──────────────────────────────────────────
            // 上游 Spectre 是"点分析按钮才画"（SpectreUiPages.More.cs:123-156）。
            // 我们这里**不需要按钮**：摘要已经是自动算的（Analyze 在
            // DrawTimingSummary 开头就调过），图只是把同一份 Report 画出来，
            // 再加一颗按钮等于让玩家多点一下才看到已有的数据。
            // （原计划的"手动分析按钮"因此取消——见 Loc 里未使用的
            //  playback.analyze* 三个键，它们是为那个方案准备的，现已不需要。）
            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("playback.analyze.hint"),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            DrawScatter(rep);
        }

        /// <summary>
        /// 画命中分布柱状图。贴图由 <see cref="HitScatter"/> 缓存并按需重建；
        /// 这里只负责"取一张矩形、把贴图贴上去"。
        ///
        /// 用 <c>GUI.DrawTexture</c> 而不是 GUILayout.Box/Image：IMGUI 里画贴图
        /// 只有这一条路（上游那套 uGUI 的 RawImage 在 IMGUI 页面里不存在）。
        /// </summary>
        private static void DrawScatter(ReplayAnalyzer.Report rep)
        {
            try
            {
                Texture2D tex = HitScatter.Get(rep, data);
                if (tex == null)
                {
                    GUILayout.Label(Loc.T("playback.analyze.none"),
                        Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    return;
                }
                // 高度固定 120（与贴图 H 一致，1:1 不缩放，柱子不会被拉糊）
                Rect r = GUILayoutUtility.GetRect(1f, 120f, GUILayout.ExpandWidth(true));
                Color old = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
                GUI.color = old;
                Gui.Frame(r, Gui.LineCol);
            }
            catch (Exception ex)
            {
                // 画图纯属附加信息，任何异常都不该把整个设置页带走
                Debug.LogWarning("[SpectreReplay] 命中分布图绘制失败：" + ex.Message);
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
            // 宽/高只对「纯净相机」有意义：游戏画面模式的分辨率跟随窗口，
            // 置灰不如**不显示**——留两个调了没反应的滑条，正是本项目反复
            // 栽过的「有界面但改不了」。上游同样在该模式下用说明替换掉这组控件
            // （SpectreUiPages.Features.cs:716-722）。
            if (RenderCaptureMode != 1)
            {
                OnGui.RowSlider("render.width", RenderWidth, 320f, 7680f,
                    v => ((int)v) + " px", v => { RenderWidth = (int)v; Commit(); });
                OnGui.RowSlider("render.height", RenderHeight, 240f, 4320f,
                    v => ((int)v) + " px", v => { RenderHeight = (int)v; Commit(); });
            }
            OnGui.RowSlider("render.fps", RenderFps, 15f, 480f,
                v => ((int)v) + " fps", v => { RenderFps = (int)v; Commit(); });
            OnGui.RowSlider("render.tail", RenderTailSeconds, 0f, 15f,
                v => v.ToString("0.#") + " s", v => { RenderTailSeconds = v; Commit(); });
            OnGui.RowDropdown("render.surface", new[]
            {
                Loc.T("render.surface.clean"), Loc.T("render.surface.screen"),
            }, RenderCaptureMode, i => { RenderCaptureMode = i; Commit(); });
            // 两种画面源的取舍差异很大（要不要 UI、分辨率能不能超采样），
            // 只给两个下拉项说不出这些——照上游在选中项下面补一句说明。
            // 两个键都写在源码里（三元表达式），LocCheck 才能都核到。
            OnGui.Note(RenderCaptureMode == 1
                ? "render.surface.screen_note" : "render.surface.clean_note");
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
        /// 编码器那行只读状态、不起探测进程（DescribeEncoder 复用 NVENC 的已缓存结论）；
        /// ffmpeg 那行会**在首次且仅当模组目录没有 exe 时**探一次系统 PATH，
        /// 但结果带 30s 冷却缓存（VideoEncoder.Cooling），不会每帧拉进程。
        /// </summary>
        private static void DrawEncoderNote()
        {
            try
            {
                var container = (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3);
                string name = VideoEncoder.DescribeEncoder(
                    (VideoEncoder.Quality)Mathf.Clamp(RenderEncoderMode, 0, 3), container);
                Gui.KeyValue("render.codec.current", name, Gui.DimCol);

                // ffmpeg 状态。**为什么要有这一行**：渲染与转换全都依赖 ffmpeg，
                // 而没有它的时候此前只有"点了开始渲染"才弹一句游戏内提示——
                // 玩家在设置页看不出任何异常，只会以为是自己参数没配对。
                // 判定口径与上游一致（SpectreUiPages.Features.cs:693）：
                // 模组/游戏根目录里找到 exe 就算就绪，否则再探一次系统 PATH。
                string exe = VideoEncoder.LocateEncoder();
                bool ready = exe != "ffmpeg" || VideoEncoder.PathProbeSucceeded();
                Gui.KeyValue("render.ffmpeg",
                    Loc.T(ready ? "render.ffmpeg.ready" : "render.ffmpeg.missing"),
                    ready ? Gui.OkCol : Gui.BadCol);
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
                // 目标格式：**独立于 render.format**（用户 2026-10-12 拍板方案 B）。
                //
                // 这里原先是一行**只读**的 Gui.KeyValue，只把 RenderOutputFormat
                // 显示出来——玩家看到"输出格式 .mp4"却点不动，是「有界面但改不了」
                // 那一族缺陷的第 4 例（前三例：键盘音三项、回放驱动开关、保存目录）。
                // 真能改的下拉在渲染页，所以本页这一行看起来像坏了。
                //
                // 现在改成真下拉，绑 Settings.ConvertTarget。**为什么解耦**：
                // 渲染格式是"这局录成什么"，转换格式是"把已有的一堆转成什么"，
                // 复用同一个值会让"渲染 mp4、顺手把旧视频转 mkv"做不到，只能来回改。
                // （权衡记录：原先复用是怕"选了 webm 却转出 mp4"——那个风险现在由
                //  这一行自己显示当前值来消除，玩家看得到自己要转成什么。）
                OnGui.RowDropdown("convert.target", new[] { "mp4", "mkv", "webm", "mov" },
                    ConvertTarget, i => { ConvertTarget = i; Commit(); });

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
                    (VideoEncoder.OutContainer)Mathf.Clamp(ConvertTarget, 0, 3), ff);
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

        /// <summary>把过长的路径折成「头…尾」，保证单行显示得完。</summary>
        private static string ClipMiddle(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s;
            }
            int keep = Math.Max(1, max - 1);
            int head = keep / 2;
            int tail = keep - head;
            return s.Substring(0, head) + "\u2026" + s.Substring(s.Length - tail);
        }

        // ── 保存目录 ────────────────────────────────────────────

        /// <summary>点击后置位，等下一拍再弹选择框（见 DrawSaveDirRow）。</summary>
        private static bool _pickSaveDirPending;

        /// <summary>
        /// 一行「回放目录」：显示当前目录 + 一个「更改…」按钮。对应
        /// <c>Settings.SavePath</c>。
        ///
        /// 这一行此前只是只读的 <c>Gui.KeyValue</c>——数据在（Settings 有字段）、
        /// 链路通（ConfigManager 落盘、SpectreState 真的按它写文件），却没有任何
        /// 一行界面能改它。想换个盘放回放只能去手改 Configs.json，而从界面上
        /// 看不出有这一项。与 ⑨（键盘音三项）/ ⑩（回放驱动开关）是同一类病。
        ///
        /// **为什么按钮点了不直接弹选择框。** <c>FileBrowser.PickFolder</c> 是
        /// **阻塞式原生对话框**（UnityFileDialog.dll → file_dialog_pick_folder，
        /// 见 UnityFileDialog.decompiled.cs:266）。而这里是在 IMGUI 的控件回调里
        /// ——Layout/Repaint 那一趟还没走完就钻进一个模态原生窗口，事件队列的
        /// 配对会错位。所以点击只置一个待办，真正的调用交给 MainThread.Defer，
        /// 在下一拍 Update（OnGUI 之外）执行。
        /// </summary>
        private static void DrawSaveDirRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("record.savedir"), Gui.LabelStyle, GUILayout.Width(Gui.LabelW));
            // 路径可能很长（换到别的盘之后尤其），中段省略：盘符/开头与目录名/
            // 结尾是玩家用来确认"是不是我选的那个"的两端，尾巴省略反而认不出。
            GUILayout.Label(ClipMiddle(SaveDirText(), 46), Gui.LabelStyle,
                GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Loc.T("record.changedir"), Gui.ButtonStyle,
                    GUILayout.Width(90f), GUILayout.Height(22f)))
            {
                // **录制中不许改。** 本局键盘音写在**旧**目录的 tmp.wav 里，而保存时
                // SpectreState.SaveFile 是按当前 SavePath 去找它的
                // （SpectreState.cs:266 与 :277）。中途改掉 ⇒ tmp.wav 找不着，
                // 回放照存、键盘音静默丢失，而且把一局的产物拆到两个目录里。
                if (is_recording)
                {
                    Status.Warn("record.savedir.busy");
                }
                else
                {
                    _pickSaveDirPending = true;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_pickSaveDirPending)
            {
                return;
            }
            _pickSaveDirPending = false;
            try
            {
                MainThread.Defer(PickSaveDirDeferred);
            }
            catch (Exception ex)
            {
                // 连"排进下一拍"都失败（极端情况下程序集没解析到）也不该让
                // OnGUI 抛出去——UMM 对 OnGUI 异常的处理是收掉整个设置面板。
                Debug.LogWarning("[SpectreReplay] 无法启动目录选择框：" + ex.Message);
            }
        }

        /// <summary>在 OnGUI 之外跑原生选择框，拿到结果再写设置。</summary>
        private static void PickSaveDirDeferred()
        {
            try
            {
                string dir = FileBrowser.PickFolder(
                    SaveDirText(), null, null, Loc.T("record.savedir.pick"));
                if (string.IsNullOrEmpty(dir))
                {
                    return;   // 玩家取消
                }
                SavePath = dir;
                // 走统一落盘入口：它除了写盘还会 Settings.check 并刷新补丁开关。
                SaveNow();
                // 列表缓存作废，下一帧从新目录重扫（否则「回放」页还列着旧目录的文件）
                _replayCache = null;
                _replayCacheTick = int.MinValue;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 选择保存目录失败：" + ex.Message);
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
