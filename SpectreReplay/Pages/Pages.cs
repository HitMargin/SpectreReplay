using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpectreReplay.Features.EffectRemover;
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
        /// 自己记而**不去问回放引擎**：ReplayData 只装数据，
        /// 不记得自己来自哪个文件。列表里那个"● 已载入"标记要靠它，
        /// 否则玩家放了哪一个只能靠猜。
        /// </summary>
        private static string _loadedPath = "";

        // 回放列表缓存与刷新时刻（TickCount 差值按无符号比较，回绕安全）。
        private static List<string> _replayCache;
        private static int _replayCacheTick = int.MinValue;

        /// <summary>
        /// 正在等第二次点击确认删除的那个文件（空 = 没有待确认的删除）。
        ///
        /// **删除只能两段式**：第一下把按钮变成「确认删除」并把路径记在这里，
        /// 第二下才真删。IMGUI 里没有宿主级确认框（UMM 不提供），而
        /// 回放文件是玩家的**唯一产出**、删掉不可恢复（没有回收站、没有撤销），
        /// 一颗单点即删的按钮放在每行——行里还有「回放」那颗主按钮——
        /// 手滑一次就没了。
        ///
        /// 存路径而不是 bool：玩家点 A 再点 B 时，B 必须是**第一次**点击
        /// （若只存 bool，B 一点就直接删了）。
        /// </summary>
        private static string _pendingDelete = "";

        // ── 录制 ────────────────────────────────────────────────
        internal static void DrawRecord()
        {
            Gui.Section("page.record", Gui.Accent, () =>
            {
                OnGui.Note("record.howto");
                Gui.Divider();
                OnGui.Row("record.recording", ReplayOn, v => { ReplayOn = v; Commit(); });
                OnGui.Row("record.autosave", AutoSave, v => { AutoSave = v; Commit(); }, ReplayOn);
                // 保存策略七项：**每一项都真的在控制回放什么时候落盘**
                // （Patch_Recording.cs:136/137/139/142/163、SpectreState.cs:254、
                // ReplayRecorder.cs:66、Patch_Lifecycle.cs:92/261），而原版界面上
                // 一个都改不了——配置文件里那七项只可能是默认值。玩家最常见的两个诉求
                // 正好都被默认值挡着：想存"打了一半的练习"（CompleteSave 默认
                // true ⇒ 非完整通关直接不存）、想存"失手了的一局"
                // （DontSaveWhenMiss 默认 true ⇒ 有 miss 直接不存）。
                OnGui.Row("record.completesave", CompleteSave, v => { CompleteSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.dontsavewhenauto", DontSaveWhenAuto, v => { DontSaveWhenAuto = v; Commit(); }, ReplayOn);
                OnGui.Row("record.dontsavewhenmiss", DontSaveWhenMiss, v => { DontSaveWhenMiss = v; Commit(); }, ReplayOn);
                OnGui.Row("record.latesave", LateSave, v => { LateSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.failsave", FailSave, v => { FailSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.backup", Backup, v => { Backup = v; Commit(); }, ReplayOn);
                OnGui.Row("record.manualsave", ManualSave, v => { ManualSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.cancelsave", Settings.CancelSave,
                    v => { Settings.CancelSave = v; Commit(); }, ReplayOn);
                // 异步输入：**开关前先看游戏侧有没有开。** 这条设置只在游戏的
                // "使用异步输入"打开时才有意义——`AsyncKeyRecorder.FlushFrame`
                // 里那个 `AsyncInputManager.isActive` 守卫就是它：游戏侧没开时
                // 我们即使勾了也一个按键都取不到，而界面上完全看不出原因，
                // 玩家只会觉得"勾了没效果"。
                // 所以这里把游戏侧的状态直接说出来，没开就明确告诉他去哪儿开。
                DrawAsyncInputRow();
                // （原「界面语言跟随游戏」开关已删除：面板语言由 Ui.Loc.RefreshLanguage
                //   始终跟随游戏，那个开关实际只影响 14 语言大表里的通知文案，
                //   默认 true 就是对的，留着只会让人以为界面语言能单独设。）
                Gui.Divider();
                DrawSaveDirRow();
            });

            Gui.Section("record.hotkeys", Gui.Accent, () =>
            {
                // 按任意键改键：等待期间 Esc 取消并独占 Esc（见 OnGui.RowHotkey）。
                // 这两个键是**手动存这一局**的入口——没有它，练到一半的短局
                // 只能等通关自动保存，等于存不下来。
                //
                // 各自跟着上面那两项开关压暗：ManualSave / CancelSave 关掉时，
                // 对应的补丁整个不挂，这两个键按下去不会有任何反应。留着可点的
                // 改键行，玩家会以为是自己绑错了键。压暗后 RowHotkey 内部同时
                // 拦掉点击与扫键（Gui.Dim 只染色，拦不住内建 GUI.Button）。
                OnGui.RowHotkey("record.savekey", () => SaveButton, k => { SaveButton = k; Commit(); },
                    ReplayOn && ManualSave);
                OnGui.RowHotkey("record.cancelsavekey", () => CancelSaveButton, k => { CancelSaveButton = k; Commit(); },
                    ReplayOn && Settings.CancelSave);
                OnGui.Note("record.note");
            });

            // 键盘音三项必须有界面：MicrophoneOffset 是毫秒级校准值，键盘声整体
            // 偏早或偏晚时它是唯一的修正手段，不能让玩家去手改 Configs.json。
            Gui.Section("record.keybd", Gui.Accent, () =>
            {
                OnGui.Row("record.keybd.record", KeybdSoundRecordActive,
                    v => { KeybdSoundRecordActive = v; Commit(); });
                // 音量与偏移**只在录音开着时才有意义**：消费点全在
                // `if (Settings.KeybdSoundRecordActive …)` 里面
                // （Patch_Playback.cs:65/93、Patch_Lifecycle.cs:52）。
                // 录音关掉时调它们不会产生任何效果。
                // 音量：内部 0..100，Patch_Playback 用它 /10f 当 AudioSource.volume
                OnGui.RowSlider("record.keybd.volume", KeybdSoundVolume, 0f, 100f,
                    v => ((int)v) + " %", v => { KeybdSoundVolume = (int)v; Commit(); },
                    KeybdSoundRecordActive);
                // 偏移：毫秒，可正可负（PSlider 支持负区间）
                OnGui.RowSlider("record.keybd.offset", MicrophoneOffset, -500f, 500f,
                    v => ((int)v) + " ms", v => { MicrophoneOffset = (int)v; Commit(); },
                    KeybdSoundRecordActive);
                OnGui.Note("record.keybd.note");
            });

            // ── 特殊按键 ────────────────────────────────────────
            //
            // 游戏把 F1~F12 / Tab / End / Alt / Win / Print 等当作"不算数"的按键，
            // 按下也不计入按键数，因此**不会进回放**。对普通游玩这是合理的
            // （多半是误触或截图），但对录制来说假设是错的：玩家有意用这些键
            // 打谱时，回放里就是缺的，而界面上完全看不出为什么。
            //
            // 候选集不是随手写的。键盘那 22 个来自
            // `RDInputType_Keyboard.SpecialKeys` 的静态初始化数据（反编译读出来的
            // 实际值：Print/SysReq/左右 Alt/左右 Win/左右 Meta/Tab/End/F1~F12），
            // 异步那 4 个来自 `RDInputType_AsyncKeyboard.SpecialKeys`
            // （PrintScreen/F12/LAlt/Super）。**候选集必须跟着游戏的实际数组走**，
            // 否则玩家会看到一堆勾了也没用的键。
            Gui.Section("record.specialkeys", Gui.Accent, () =>
            {
                OnGui.Note("record.specialkeys.note");
                // 分组标签用 KeyValue 而不是 Row：这两行是**标题**不是控件，
                // 用 Row 会多画一颗开关，而玩家会去点它（真正的开关是下面的格子）。
                Gui.KeyValue("record.specialkeys.kb", "", Gui.DimCol);
                // **整块挂录制总开关**：录制关掉时 `SetFeatureToggle(SpecialKeys)`
                // 的谓词里 `Settings.ReplayOn` 为假 ⇒ 两条补丁整个不挂载，
                // 勾了也不会有任何效果。灰掉比"能点但不生效"诚实。
                Gui.KeyChips(KeyboardSpecialKeyNames, AllowedKeyboardSpecialKeys, 6,
                    () => Commit(), ReplayOn, "record.specialkeys.kb");
                if (GameAsyncInputOn())
                {
                    Gui.KeyValue("record.specialkeys.async", "", Gui.DimCol);
                    Gui.KeyChips(AsyncSpecialKeyNames, AllowedAsyncSpecialKeys, 4,
                        () => Commit(), ReplayOn, "record.specialkeys.async");
                }
                else
                {
                    OnGui.Note("record.specialkeys.async.off");
                }
            });

            Gui.Section("record.keymap", Gui.Accent, () =>
            {
                OnGui.Row("record.keymap.on", KeyConvertEnabled, v =>
                {
                    KeyConvertEnabled = v;
                    Commit();
                    // 建表是**启动时**做的（Settings.check），关掉/打开后必须
                    // 立刻重建，否则开关看着变了、实际映射还是旧的。
                    Settings.key_convert_list2array();
                });
                if (!KeyConvertEnabled)
                {
                    OnGui.Note("record.keymap.off");
                    return;
                }
                // 「只模拟列表中的按键」——与总开关同样要**当场重建表**。
                // 它改的是「表外那些键要不要静默」：开启后表外的键在回放时
                // 全部跳过（key_code_convert 填 0），只用手柄/宏键盘几个键
                // 打谱时靠它屏蔽其余按键。
                OnGui.Row("record.keymap.limit", LimitKey, v =>
                {
                    LimitKey = v;
                    Settings.key_convert_list2array();   // 不重建就当场不生效
                    Commit();
                });
                OnGui.Note("record.keymap.note");
                DrawKeyRemapList();
            });
        }

        // 正在等按键的那一行（-1 = 没有）。_remapFrom 表示改左半还是右半。
        private static int _remapRow = -1;
        private static bool _remapFrom;

        private static void DrawKeyRemapList()
        {
            var list = Settings.key_code_convert_list;
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                KeyValuePair<ushort, ushort> pair = list[idx];
                bool capturing = _remapRow == idx;
                OnGui.RowKeyRemap(pair.Key, pair.Value, capturing, _remapFrom,
                    from =>
                    {
                        // 再点同一格 = 取消等待
                        bool same = _remapRow == idx && _remapFrom == from;
                        _remapRow = same ? -1 : idx;
                        _remapFrom = from;
                        if (!same) OnGui.PrimeKeyRemapCapture();
                    },
                    vk =>
                    {
                        if (_remapRow != idx) return;
                        if (vk == 0) { _remapRow = -1; return; }
                        KeyValuePair<ushort, ushort> cur = list[idx];
                        list[idx] = _remapFrom
                            ? new KeyValuePair<ushort, ushort>(vk, cur.Value)
                            : new KeyValuePair<ushort, ushort>(cur.Key, vk);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    },
                    () =>
                    {
                        KeyValuePair<ushort, ushort> cur = list[idx];
                        list[idx] = new KeyValuePair<ushort, ushort>(cur.Key, 0);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    },
                    () =>
                    {
                        list.RemoveAt(idx);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    });
            }
            if (list.Count == 0)
            {
                OnGui.Note("record.keymap.empty");
            }
            OnGui.Button("record.keymap.add", () =>
            {
                Settings.key_code_convert_list.Add(new KeyValuePair<ushort, ushort>(0, 0));
                ApplyKeyRemap();
            }, false);
        }

        /// <summary>
        /// 把 list 序列化回 <see cref="Settings"/>.<see cref="KeyCodeConvert"/> 并重建运行时映射表。
        ///
        /// 对应 <c>Settings.KeyCodeConvert</c>（持久化 JSON 源）与 <c>Settings.key_code_convert</c>
        /// （运行时表）——`settings-ui-audit.ps1` 按字段名判界面入口，这里显式点名。
        ///
        /// **两步都不能省**：只序列化不建表 ⇒ 界面显示变了、实际映射还是旧的；
        /// 只建表不序列化 ⇒ 当场生效、下次启动全部丢失。
        /// 与上游 ApplyRemapChange 的顺序一致（先写 JSON 再 key_convert_list2array）。
        /// </summary>
        private static void ApplyKeyRemap()
        {
            KeyCodeConvert = Newtonsoft.Json.JsonConvert.SerializeObject(
                Settings.key_code_convert_list, Newtonsoft.Json.Formatting.Indented);
            Settings.key_convert_list2array();   // 这一步不能省：只写 JSON 不重建表，本次会话内映射仍是旧的
            Commit();
        }

        /// <summary>
        /// 键盘侧被游戏排除的特殊键。**顺序与分组照着游戏的 SpecialKeys 排**，
        /// 前后两组（功能键 / 修饰与系统键）在视觉上能连读。
        /// 名字必须是 <see cref="UnityEngine.KeyCode"/> 的真实成员——
        /// 存的是名字，解析失败会被静默跳过（见 Patch_SpecialKeys.EnsureCodes）。
        /// </summary>
        private static readonly string[] KeyboardSpecialKeyNames =
        {
            "Print", "SysReq", "Tab", "End",
            "LeftAlt", "RightAlt", "LeftWindows", "RightWindows",
            "LeftMeta", "RightMeta",
            "F1", "F2", "F3", "F4", "F5", "F6",
            "F7", "F8", "F9", "F10", "F11", "F12",
        };

        /// <summary>
        /// 异步侧被游戏排除的特殊键（SkyHook.KeyLabel 的成员名）。
        /// 游戏那边只有这 4 个，不要照"看起来像"往里加。
        /// </summary>
        private static readonly string[] AsyncSpecialKeyNames =
        {
            "PrintScreen", "F12", "LAlt", "Super",
        };

        /// <summary>游戏侧是否开了异步输入。取不到就当没开（宁可不显示，也不误导）。</summary>
        private static bool GameAsyncInputOn()
        {
            try
            {
                return RDC.useAsyncInput;
            }
            catch
            {
                return false;
            }
        }

        // ── 回放 ────────────────────────────────────────────────
        internal static void DrawPlayback()
        {
            // **「模拟按键」开关（原 Settings.LegacyEngine）已删除。**
            //
            // 它看起来是「要不要注入按键」，实际关掉后并非「只播不注入」：
            // 判定覆盖、长条 holdKeys 同步、键位显示桥接全都会跟着停，
            // 于是长条关卡莫名失败、KeyViewer 变黑，而画面上看不出原因。
            // 一个「关掉就坏」的开关摆在回放页第一屏，等于给所有用户
            // 准备了一个查不出来的坑。
            //
            // 真要停止注入，用 ReplayOn 总开关——它连补丁一起卸，语义干净。
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
                    // 文件名本身就是展开详情的开关。**不另加一颗「详情」按钮**：
                    // 这一行已经有两颗按钮（回放 / 删除），再加一颗会让「点名字」
                    // 这个玩家的自然动作失效——点了没反应，还得再找那颗小按钮。
                    Gui.ClickableLabel(Path.GetFileNameWithoutExtension(path),
                        loaded || ReplayInfoPanel.IsOpen(captured),
                        "playback.info.tip",
                        () => ReplayInfoPanel.Toggle(captured));
                    if (GUILayout.Button(Loc.T("playback.replay"), Gui.ButtonStyle,
                            GUILayout.Width(110f), GUILayout.Height(22f)))
                    {
                        LoadReplayFile(captured);
                    }
                    // **删除放在最后一列、用最轻的样式。** 这一行已经有
                    // 「回放」那颗主按钮，删除是破坏性操作，不该和它抢位置与
                    // 视觉权重；用 QuietButtonStyle 与「卸载回放」同一档。
                    DrawDeleteFileButton(captured);
                    GUILayout.EndHorizontal();

                    // 详情就地展开在这一行正下方（点文件名切换）。
                    if (ReplayInfoPanel.IsOpen(captured))
                    {
                        ReplayInfoPanel.Draw(captured);
                    }
                }
            });
        }

        private static void LoadReplayFile(string path)
        {
            _pendingDelete = "";
            try
            {
                if (!Features.Replay.ReplayManager.LoadReplayAuto(
                        out ReplayData loaded, path))
                {
                    Status.Warn("playback.idle_err", Path.GetFileNameWithoutExtension(path));
                    return;
                }
                data = loaded;
                _loadedPath = path;
                // 全量回放 = 从第 0 块录到本关最后一块。**必须现算**：这个标志
                // 原本只在关卡启动时（Patch_Lifecycle）赋值，而设置页载入回放时
                // 还没进关，读到的永远是上一次的残留值。
                //
                // 关卡没加载时 ADOBase.lm 为 null，此时算不出来 → 保持 false，
                // 宁可少显示一条提示，也不要对半截回放谎称"可任意位置开始"。
                IsFullRun = data.StartTile.HasValue && data.EndTile.HasValue
                    && data.StartTile == 0
                    && ADOBase.lm != null
                    && ADOBase.lm.listFloors != null
                    && ADOBase.lm.listFloors.Count > 0
                    && data.EndTile == ADOBase.lm.listFloors.Count - 1;
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
        /// 每行末尾那颗「删除」按钮。**两段式**：第一下只把按钮文案换成
        /// 「确认删除」，第二下才真的删文件。
        ///
        /// <para><b>为什么必须两段。</b>回放文件是玩家的唯一产出，删掉不可恢复
        /// （不走回收站、没有撤销）。而它所在的那一行左边就是「回放」主按钮，
        /// 手滑一次那局就没了。UMM 不提供宿主级确认框，所以在原地做。</para>
        ///
        /// <para><b>为什么记路径而不是 bool。</b>玩家先点 A 再点 B 时，B 必须是
        /// 「第一次点击」。只存 bool 的话 B 一点就直接删——两次点击落在两个
        /// 不同文件上，语义完全不同。</para>
        ///
        /// <para><b>为什么删的若是当前载入的那个要连带卸载。</b>文件没了而
        /// <c>data</c> 还装着它的内容，界面会一直显示「已载入」一个不存在的
        /// 文件；玩家再点重开还能播——这会让人以为删除失败了。</para>
        /// </summary>
        private static void DrawDeleteFileButton(string path)
        {
            bool pending = string.Equals(path, _pendingDelete, StringComparison.OrdinalIgnoreCase);
            string label = Loc.T(pending ? "playback.delete.confirm" : "playback.delete");
            float w = pending ? 110f : 70f;
            // 显式取矩形而不是让 GUILayout.Button 自己排：Gui.Tip 需要一个 Rect，
            // 而本项目「每个控件都能自我解释」的约定要靠它（删除尤其需要——
            // 它是这一页唯一的破坏性操作）。
            Rect r = GUILayoutUtility.GetRect(w, 22f,
                GUILayout.Width(w), GUILayout.Height(22f));
            Gui.Tip(r, "playback.delete");
            if (!GUI.Button(r, label,
                    pending ? Gui.AccentButtonStyle : Gui.QuietButtonStyle))
            {
                return;
            }
            if (!pending)
            {
                // 第一下：只进入待确认态，绝不碰文件。
                _pendingDelete = path;
                return;
            }

            // 第二下：真删。
            _pendingDelete = "";
            try
            {
                File.Delete(path);
                AfterReplayDeleted(path);
                Status.Say("playback.delete.done", Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 删除回放失败：" + ex);
                Status.Warn("playback.delete.err", Path.GetFileNameWithoutExtension(path));
            }
        }

        /// <summary>
        /// 文件删掉之后的连带处理。**两件事都必须做，且都不是可选的：**
        ///
        /// ① 删的若是当前载入那份，必须卸载并清 <c>_loadedPath</c>。否则界面
        ///    一直显示「已载入」一个已经不存在的文件，而玩家再点「重开这关」
        ///    还能播（数据在内存里）——看起来就像删除失败了。
        /// ② 必须刷列表缓存。列表有 1 秒缓存，不刷的话被删的那行还会挂着，
        ///    玩家再点一下就是 <c>File.Delete</c> 一个不存在的路径（异常 + 红字）。
        /// </summary>
        private static void AfterReplayDeleted(string path)
        {
            // 详情面板若正展开这一份，必须收起——它读的文件已经不在了，
            // 留着会让下一帧去读一个不存在的路径（每次都失败、每次都记日志）。
            ReplayInfoPanel.Forget(path);
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PlayActions.UnloadReplay();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 删除后卸载回放失败：" + ex.Message);
                }
                _loadedPath = "";
            }
            _replayCache = null;
            _replayCacheTick = int.MinValue;
        }

        private static void DrawPlaybackState()
        {
            if (is_playing)
            {
                string song = "?";
                if (data != null && data.SongName is string sn
                    && !string.IsNullOrEmpty(sn))
                {
                    song = sn;
                }
                GUILayout.Label(Loc.F("playback.armed", song), Gui.LabelStyle);
                // 这条提示只有在关卡加载后（Start_Rewind）才算得准，见 DrawFullRunBadge。
                DrawFullRunBadge();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Loc.T("playback.stop"), Gui.ButtonStyle,
                        GUILayout.Width(140f), GUILayout.Height(26f)))
                {
                    PlayActions.StopPlaying();
                }
                // 「停止」与「卸载」是两件事：停止只是不再注入按键，回放仍装着；
                // 卸载才是完全退回未装弹状态。
                DrawUnloadButton();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return;
            }
            if (is_recording)
            {
                GUILayout.Label(Loc.T("status.recording"), Gui.LabelStyle);
                // 停止录制，不是取消保存。CancelSave 会丢掉本局待存数据，两者必须分开。
                if (GUILayout.Button(Loc.T("record.stop"), Gui.ButtonStyle,
                        GUILayout.Width(140f), GUILayout.Height(26f)))
                {
                    PlayActions.StopRecording();
                }
                return;
            }
            // **"还没载入"只在真的什么都没装时才说。**
            // 这句原先无条件打印，而它上面的两个分支只覆盖「播放中」「录制中」——
            // 于是"已装弹、还没开播"这个状态走进了这里，同一块里先出现
            // 「还没载入回放」，紧接着 DrawArmedHint 又说「已装好：某某」，
            // 两句话当场打架，玩家分不清到底装没装上。
            if (PlayMode && data != null)
            {
                // 顺序：先报状态，再给操作。反过来会让「卸载回放」按钮压在
                // 「已装好：某某」上面，读起来像是给上面那句 idle 配的操作。
                DrawArmedHint();
                // 已装弹但还没开播时 data 仍在，玩家需要能反悔——此时上面那个
                // is_playing 分支走不到，不补这一处就会出现"刚载入就又卸不掉了"。
                GUILayout.BeginHorizontal();
                DrawUnloadButton();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                DrawTimingSummary();
                DrawOpenLevelRow();
                return;
            }

            GUILayout.Label(Loc.T("playback.idle"), Gui.NoteStyle, GUILayout.ExpandWidth(true));
        }

        /// <summary>
        /// 「卸载回放」按钮。**两个状态分支都要给**（回放中 / 已装弹未开播），
        /// 因为它们的宿主分支不同，只补一处就会剩下另一个状态卸不掉。
        ///
        /// 用 <see cref="Gui.QuietButtonStyle"/> 而不是普通按钮：它是"撤销装弹"
        /// 这类破坏性小操作，不该和「回放」那颗主行动按钮抢视觉权重。
        /// 二次确认也没必要——卸载不删文件，重新载入即可。
        /// </summary>
        private static void DrawUnloadButton()
        {
            if (GUILayout.Button(Loc.T("playback.unload"), Gui.QuietButtonStyle,
                    GUILayout.Width(110f), GUILayout.Height(26f)))
            {
                try
                {
                    PlayActions.UnloadReplay();
                    _loadedPath = "";
                }
                catch (Exception ex)
                {
                    // 卸载失败不该把整个设置页带走（与载入侧同一处理）
                    Debug.LogWarning("[SpectreReplay] 卸载回放失败：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 「打开这一关」按钮。从已载入回放里读 <c>level_path</c>，一键跳到它录自的那一关。
        ///
        /// **为什么需要它。** 回放只在"重开/换关/进关"时才启动（见
        /// <see cref="OpenReplayLevel"/> 的说明），而练习谱/自制谱要自己找回去
        /// 很麻烦——玩家录完一局、退出、回放页点载入，然后只能靠记忆翻关卡列表。
        /// 这颗按钮就是那条回去的路。
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
            if (data.LevelPath is not string levelPath
                || string.IsNullOrEmpty(levelPath))
            {
                return;
            }

            GUILayout.Space(4f);
            // 官方关卡没有谱面文件，关卡数据烤在游戏里，靠 GCS.internalLevelName
            // 选（形如 "0-0"/"AR-X"）。录制时已把名字存进 data.InternalLevelName。
            bool official = levelPath.StartsWith("OfficialLevel", StringComparison.Ordinal)
                || (data.IsOfficialLevel is bool iso && iso);
            if (official)
            {
                string internalName = data.InternalLevelName is string nm ? nm : "";
                // 老回放没有内部关卡名：给提示而不是一颗点了没反应的按钮。
                if (string.IsNullOrEmpty(internalName))
                {
                    GUILayout.Label(Loc.T("playback.openlevel.official")
                        + (string.IsNullOrEmpty(levelPath) ? "" : levelPath),
                        Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    return;
                }
                // 标题行 + 直达按钮：歌名与关卡 id 都写出来，玩家才知道要跳去哪。
                GUILayout.Label(Loc.F("playback.openlevel.official_go", internalName),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(Loc.T("playback.openlevel.official.go"),
                        Gui.ButtonStyle, GUILayout.Width(160f), GUILayout.Height(24f)))
                {
                    try
                    {
                        OpenReplayLevel.GoOfficial();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SpectreReplay] 打开官方关卡失败：" + ex.Message);
                        Status.Warn("playback.openlevel.busy");
                    }
                }
                return;
            }
            if (!File.Exists(levelPath))
            {
                GUILayout.Label(Loc.T("playback.openlevel.missing") + ClipMiddle(levelPath, 46),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }

            // 录自编辑器就回编辑器、录自关卡就回关卡——两边起跳逻辑不同
            string recScene = data.SceneName is string sc ? sc : "";
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

        private static void DrawArmedHint()
        {
            if (!PlayMode || data == null)
                return;
            string song = data.SongName is string sn && !string.IsNullOrEmpty(sn)
                ? sn : "?";
            GUILayout.Space(2f);
            GUILayout.Label(Loc.F("playback.ready", song), Gui.NoteStyle, GUILayout.ExpandWidth(true));
            DrawFullRunBadge();
        }

        /// <summary>
        /// 重算「全量回放」标志。判定 = 从第 0 块起、终点是本关最后一块：
        /// 起点与终点来自回放文件，**本关总块数只能问当前关卡**。
        ///
        /// <para><b>为什么每次绘制都重算，而不是只信启动时那一次赋值。</b>
        /// 这个标志依赖 <c>ADOBase.lm.listFloors</c>，而它只在关卡加载后存在：
        /// 从设置页「打开关卡」进关的路径上，装弹那一刻 <c>lm</c> 还是 null，
        /// 算不出来；<see cref="Features.Replay.Patch_Lifecycle"/> 里那次赋值又排在
        /// 一长串校验之后，任一校验提前返回就漏掉了。所以绘制时按活数据再算一次
        /// ——只要关卡在，结论就一定正确。</para>
        ///
        /// <para>关卡没加载（<c>lm</c> 为 null 或还没有地板）时**保持原值**：
        /// 此时算不出来，硬判 false 会把已经算对的结论擦掉。</para>
        /// </summary>
        private static void RefreshFullRun()
        {
            if (data == null || !data.StartTile.HasValue || !data.EndTile.HasValue)
            {
                IsFullRun = false;
                return;
            }
            if (data.StartTile != 0)
            {
                IsFullRun = false;
                return;
            }
            int total = 0;
            try
            {
                if (ADOBase.lm != null && ADOBase.lm.listFloors != null)
                    total = ADOBase.lm.listFloors.Count;
            }
            catch
            {
                total = 0;
            }
            if (total <= 0)
                return; // 关卡还没加载：算不出来，别把已有结论擦掉
            IsFullRun = data.EndTile == total - 1;
        }

        private static void DrawFullRunBadge()
        {
            // 每次画的时候再算一次：这个标志依赖关卡数据，而关卡数据在
            // 「先装弹、后进关」的流程里，装弹那一刻还不存在。
            RefreshFullRun();
            if (!IsFullRun)
                return;
            Color prev = GUI.contentColor;
            GUI.contentColor = Gui.OkCol;
            GUILayout.Label(Loc.T("playback.fullrun"), Gui.LabelStyle, GUILayout.ExpandWidth(true));
            GUI.contentColor = prev;
        }

        /// <summary>
        /// 已载入回放的命中误差摘要。
        ///
        /// **为什么要显示它。** ReplayAnalyzer.Analyze 是纯计算、不碰引擎，
        /// 但**必须有调用点**才算存在 —— 那一整块"整体偏早/偏晚、中位数、
        /// 标准差"是玩家打完一局最想知道的事，没人调用就等于不存在。
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
                // 绑 RenderOn（视频渲染开关）。不能用 ReplayOn：那是录制/回放总开关。
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

            // **渲染总开关关掉时，这一整块参数都压暗且不可调。**
            // RenderOn 为假时 VideoRender.StartRender 直接拒绝（VideoRender.cs:67），
            // 这些参数一个都不会被读到——留着可调等于让玩家白设一遍。
            // 用 Dim 包一整块而不是逐行加参数：这里二十多个控件，逐行写既啰嗦
            // 又容易漏一个，而漏的那一个正好会变成"整块都灰、只有它亮着"。
            Gui.Section("render.options", Gui.Accent, () => Gui.Dim(!RenderOn, DrawRenderOptions));

            // 转换已有视频放在最后：它是"渲染完再做的事"，与渲染参数不是一类。
            DrawConvertSection();
        }

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
                string last;
                switch (s.Result)
                {
                    case Features.VideoRender.VideoRender.Outcome.Completed:
                        last = Loc.F("status.render_done", Path.GetFileName(s.Output));
                        break;
                    case Features.VideoRender.VideoRender.Outcome.Canceled:
                        last = Loc.T("status.render_canceled");
                        break;
                    case Features.VideoRender.VideoRender.Outcome.Failed:
                        last = string.IsNullOrEmpty(s.Error)
                            ? Loc.T("status.render_failed")
                            : s.Error;
                        break;
                    default:
                        last = Loc.T("status.idle");
                        break;
                }
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
        /// 落盘，却**没有任何一行界面能改它们**——配置文件里那几项只可能是默认值，
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
            // 渲染途中按快捷键提前结束：对应 **Settings.RenderStopOn / RenderStopKey**。
            // 紧跟在「结尾秒数」之后：这两项描述的是同一件事——这段渲染到哪儿为止。
            // 快捷键那一行跟着上一项置灰：关掉之后那个键整个不被消费（补丁不挂），
            // 还让玩家去改一个永远不生效的键没有意义。RowHotkey 内部会按
            // `!enabled || Gui.IsDim` 拦掉点击与扫键——Gui.Dim 本身只染色，
            // 拦不住内建 GUI.Button，所以这里不能只靠外层包一层。
            OnGui.Row("render.stop.enable", RenderStopOn,
                v => { RenderStopOn = v; Commit(); });
            OnGui.RowHotkey("render.stop.key", () => RenderStopKey,
                k => { RenderStopKey = k; Commit(); }, RenderStopOn);
            if (RenderStopOn)
                OnGui.Note("render.stop.hint");
            OnGui.RowDropdown("render.surface", new[]
            {
                Loc.T("render.surface.clean"), Loc.T("render.surface.screen"),
            }, RenderCaptureMode, i => { RenderCaptureMode = i; Commit(); });
            // 两种画面源的取舍差异很大（要不要 UI、分辨率能不能超采样），
            // 只给两个下拉项说不出这些——照上游在选中项下面补一句说明。
            // 两个键都写在源码里（三元表达式），LocCheck 才能都核到。
            OnGui.Note(RenderCaptureMode == 1
                ? "render.surface.screen_note" : "render.surface.clean_note");
            // 鼠标指针开关：对应 **Settings.RenderCursorOn**。
            // 放在画面来源之后，因为它描述的是"成片里有什么"，与分辨率/帧率无关。
            // 游戏本身不把指针画进 backbuffer（Cursor.visible 只是藏/显硬件光标），
            // 所以打开时由 CursorOverlay 在像素层画一个上去。
            OnGui.Row("render.cursor", RenderCursorOn,
                v => { RenderCursorOn = v; Commit(); });
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
            OnGui.RowInput("render.outdir");
            DrawEncoderNote();
            // ── 实验性：确定性渲染时钟 ──────────────────────────────
            // 放在最後、并明确标"实验性"：它动的是游戏的时间源本身，影响面比
            // 其余选项都大，不该混在常规选项里让人顺手就开。
            Gui.Divider();
            OnGui.Row("render.clock.experimental", RenderClockOn,
                v => { RenderClockOn = v; Commit(); });
            OnGui.Note("render.clock.hint");
        }

        /// <summary>
        /// 本局渲染实际会用的编码器。
        ///
        /// **为什么必须画出来。** <c>VideoEncoder.DescribeEncoder</c> 的结论
        /// 必须落到界面上：选「最快」时若 NVENC 探测失败，编码器会静默落到
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
        /// 转换已有视频：VideoConverter 是纯逻辑转换器，**必须有界面入口**——
        /// 没有入口时"批量转格式"这个功能对玩家等于不存在。
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

        // ── 去特效 ──────────────────────────────────────────────

        /// <summary>
        /// 去特效页。
        ///
        /// **为什么单独一页而不是塞进录像页。** 这里有 27 个开关，全塞进任何一页都会
        /// 把原有的设置挤到几屏之外——而这一页本身**是给高级用途的**：想让谱面看起来
        /// 干净、或者机器跑不动渲染时减负。单独一页还有个附带好处：总开关关掉时它
        /// 就是个普通页签，玩家不会误以为"录制功能坏了"。
        ///
        /// **控件顺序按"风险从低到高"排。** 最前面是总开关与「允许存盘」（这两项
        /// 决定这次操作会不会改到磁盘），后面才是各个具体项——把破坏性选项（移除装饰物、
        /// 重置轨道颜色）摆在最上面，玩家顺手打开就会把谱面的美术全抹了。
        /// </summary>
        internal static void DrawEffects()
        {
            // ── 「全选本组」只给 visual / track / planet 三组 ──────────────
            // 口径是**这一组是不是一组同质的「要摘掉的事件类别」**，不是开关数量：
            //   · visual / track / planet：整组都是「摘掉某类事件」，一键同开同关有意义；
            //   · master：含总开关自身（自我指涉）与「允许覆盖关卡」授权，
            //     一键把破坏性授权也打开，正是这页刻意避免的；
            //   · decor：两项里「移除全部装饰」是清空级动作，不该被顺手打开；
            //   · audio：只有两个开关，多一次点击反而更慢；
            //   · gameplay：改的是运行逻辑不是画面，不该和视觉项共用一颗按钮。
            // 口径若改，同步改 tools\UiPreview\UiLogicShapeCheck.cs 的 ⑱f。
            Gui.Section("effects.master", Gui.Accent, () =>
            {
                OnGui.Row("effects.on", EffectRemoverOn, v =>
                {
                    EffectRemoverOn = v;
                    Commit();
                    if (!v)
                    {
                        EffectRemover.RestoreEditorSaveButtons();
                    }
                    else
                    {
                        EffectRemover.RefreshEditorSaveButtons();
                    }
                });
                OnGui.Note("effects.on.note");
                OnGui.Row("effects.enablesave", EffectRemoverEnableSave,
                    v =>
                    {
                        EffectRemoverEnableSave = v;
                        Commit();
                        EffectRemover.RefreshEditorSaveButtons();
                    }, EffectRemoverOn);
                OnGui.Note("effects.enablesave.note");
            });

            Gui.Section("effects.visual", Gui.Accent, () =>
            {
                // 一键全选本组（双向，见 DrawEffects 开头那段口径）。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverFilters || EffectRemoverAdvancedFilters
                             || EffectRemoverParticles || EffectRemoverBackgrounds
                             || EffectRemoverCameras);
                    EffectRemoverFilters = on;
                    EffectRemoverAdvancedFilters = on;
                    EffectRemoverParticles = on;
                    EffectRemoverBackgrounds = on;
                    EffectRemoverCameras = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.filters", EffectRemoverFilters, v => { EffectRemoverFilters = v; Commit(); });
                OnGui.Row("effects.advfilters", EffectRemoverAdvancedFilters, v => { EffectRemoverAdvancedFilters = v; Commit(); });
                OnGui.Row("effects.particles", EffectRemoverParticles, v => { EffectRemoverParticles = v; Commit(); });
                OnGui.Row("effects.backgrounds", EffectRemoverBackgrounds, v => { EffectRemoverBackgrounds = v; Commit(); });
                OnGui.Row("effects.cameras", EffectRemoverCameras, v => { EffectRemoverCameras = v; Commit(); });
                OnGui.Row("effects.setcamerazoom", EffectRemoverSetCameraZoom,
                    v => { EffectRemoverSetCameraZoom = v; Commit(); },
                    EffectRemoverOn && EffectRemoverCameras);
                OnGui.RowSlider("effects.camerazoom", EffectRemoverCameraZoomScale, 100f, 1000f,
                    v => ((int)v) + " %", v => { EffectRemoverCameraZoomScale = v; Commit(); },
                    EffectRemoverOn && EffectRemoverCameras && EffectRemoverSetCameraZoom);
            }, EffectRemoverOn);

            Gui.Section("effects.decor", Gui.Accent, () =>
            {
                OnGui.Row("effects.decorations", EffectRemoverDecorations, v => { EffectRemoverDecorations = v; Commit(); });
                OnGui.Row("effects.removealldecor", EffectRemoverRemoveAllDecorations,
                    v => { EffectRemoverRemoveAllDecorations = v; Commit(); },
                    EffectRemoverOn && EffectRemoverDecorations);
                OnGui.Note("effects.decor.note");
            }, EffectRemoverOn);

            Gui.Section("effects.track", Gui.Accent, () =>
            {
                // 一键全选本组（上游 effectremover.toggleall 同形）。
                // 放在组首：这是「先决定这一组要不要」的动作，不是某一项的配套。
                // 受总开关管——总开关关着时按下去等于什么都没发生，会误导。
                // **双向**：本组已有一项开着，按下去是全部关掉；全关着才全部打开。
                // 单向「只开不关」的话，玩家批量打开后想撤销只能逐项去点。
                //
                // **只覆盖「摘掉某类事件」的四项**，不含三个 reset：
                // resettrackopacity/resettrackanim 是改写残余事件的参数，
                // 不是「多摘一类」，混进全选会变成「顺手改了参数」。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverTrackAnimations || EffectRemoverTrackPositions
                             || EffectRemoverTrackMoves || EffectRemoverTrackColors);
                    EffectRemoverTrackAnimations = on;
                    EffectRemoverTrackPositions = on;
                    EffectRemoverTrackMoves = on;
                    EffectRemoverTrackColors = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.trackanim", EffectRemoverTrackAnimations, v => { EffectRemoverTrackAnimations = v; Commit(); });
                // 顺序照上游：move 在 pos 之前（move 是最常见的一类，排前面）。
                OnGui.Row("effects.trackmove", EffectRemoverTrackMoves, v => { EffectRemoverTrackMoves = v; Commit(); });
                OnGui.Row("effects.trackpos", EffectRemoverTrackPositions, v => { EffectRemoverTrackPositions = v; Commit(); });
                OnGui.Row("effects.trackcolor", EffectRemoverTrackColors, v => { EffectRemoverTrackColors = v; Commit(); });
                OnGui.Row("effects.resettrackopacity", EffectRemoverResetTrackOpacity,
                    v => { EffectRemoverResetTrackOpacity = v; Commit(); });
                // 两个「重置」各自从属同一个父项（上游同）：
                // 父项关着时对应事件不会被摘，重置也就没有对象可改。
                OnGui.Row("effects.resettrackanim", EffectRemoverResetTrackAnimation,
                    v => { EffectRemoverResetTrackAnimation = v; Commit(); },
                    EffectRemoverOn && EffectRemoverTrackAnimations);
                OnGui.Row("effects.resettrackcolor", EffectRemoverResetTrackColor,
                    v => { EffectRemoverResetTrackColor = v; Commit(); },
                    EffectRemoverOn && EffectRemoverTrackColors);
            }, EffectRemoverOn);

            Gui.Section("effects.planet", Gui.Accent, () =>
            {
                // 一键全选本组（同上）。
                // **双向**，同轨道组。四项都属于「摘掉一类行星相关事件」，
                // hideicons 摘的是 Hide 事件，同质，留在同一颗按钮里。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverPlanetOrbit || EffectRemoverPlanetScale
                             || EffectRemoverPlanetRadius || EffectRemoverHideIcons);
                    EffectRemoverPlanetOrbit = on;
                    EffectRemoverPlanetScale = on;
                    EffectRemoverPlanetRadius = on;
                    EffectRemoverHideIcons = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.planetorbit", EffectRemoverPlanetOrbit, v => { EffectRemoverPlanetOrbit = v; Commit(); });
                OnGui.Row("effects.planetscale", EffectRemoverPlanetScale, v => { EffectRemoverPlanetScale = v; Commit(); });
                OnGui.Row("effects.planetradius", EffectRemoverPlanetRadius, v => { EffectRemoverPlanetRadius = v; Commit(); });
                OnGui.Row("effects.hideicons", EffectRemoverHideIcons, v => { EffectRemoverHideIcons = v; Commit(); });
            }, EffectRemoverOn);

            Gui.Section("effects.audio", Gui.Accent, () =>
            {
                OnGui.Row("effects.hitsounds", EffectRemoverHitSounds, v => { EffectRemoverHitSounds = v; Commit(); });
                OnGui.Row("effects.holdsounds", EffectRemoverHoldSounds, v => { EffectRemoverHoldSounds = v; Commit(); });
            }, EffectRemoverOn);

            Gui.Section("effects.gameplay", Gui.Accent, () =>
            {
                OnGui.Row("effects.checkpoints", EffectRemoverCheckpoints, v => { EffectRemoverCheckpoints = v; Commit(); });
                OnGui.Row("effects.inputevents", EffectRemoverInputEvents, v => { EffectRemoverInputEvents = v; Commit(); });
                OnGui.Row("effects.framerate", EffectRemoverFrameRate, v => { EffectRemoverFrameRate = v; Commit(); });
                OnGui.Row("effects.repeatevents", EffectRemoverRepeatEvents, v => { EffectRemoverRepeatEvents = v; Commit(); });
                OnGui.Note("effects.gameplay.note");
            }, EffectRemoverOn);
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
                Gui.Divider();
                DrawUpdateRow();
            });
        }

        /// <summary>
        /// 「检查更新」这一行。
        ///
        /// **这里不自己发网络请求。** 更新检查由 UMM 自己做：它读 info.json 的
        /// Repository 下载那个 JSON，把结果写进 ModEntry.NewestVersion，再在
        /// UMM 自己的模组列表里显示「(Outdated)」。我们只是把那个结果**读出来**
        /// 摆在关于页上，让玩家不必去翻 UMM 列表。
        ///
        /// 查证依据（反编译 UnityModManager.dll 的 IL，不是看文档）：
        ///   · CheckModUpdates 读 Info.Repository 去下载、读 Info.HomePage 供 Nexus 路径；
        ///   · ParseRepository 用 Release.Id 调 FindMod，而 FindMod 比的是 **ModInfo.Id**
        ///     ——所以 repo.json 里必须写 Id = com.spectre.replay，不是显示名；
        ///   · NewestVersion 只在「版本确实更新」时才被赋值，否则保持 null。
        ///
        /// 三种态各自说清楚，因为它们的修法完全不同：
        ///   · 有新版 → 告诉版本号，并让玩家能点开仓库下载；
        ///   · 查过且没新版 → 敢说「已是最新」；
        ///   · 还没查过 → **绝不要说「已是最新」**。把「UMM 根本没查」说成「已最新」，
        ///     玩家会永远等不到更新，而且没有任何迹象。
        ///
        /// **⚠️ NewestVersion == null 不能当作「已是最新」用。** 这是反编译 IL 才发现的：
        /// ParseRepository 里 `op_LessThan(current, release)` 之后紧跟 `brfalse`，
        /// 也就是**只有「确实更新」时才执行 stfld NewestVersion**；版本相等或更低时
        /// 直接跳过，字段保持 null。所以 null 同时代表「没新版」与「没查过」。
        /// 要分开这两种，唯一的旁证是 `Params.LastUpdateCheck`——UMM 每次检查都会
        /// 先把它写成 DateTime.Now。它非默认值 ⇒ 查过；此时 NewestVersion 仍为 null
        /// ⇒ 真的是最新。
        /// </summary>
        private static void DrawUpdateRow()
        {
            string homepage = HomePageUrl();
            Version newest = NewestVersionOrNull();
            Version current = CurrentVersionOrNull();
            bool checkedOnce = LastUpdateCheckSeen();

            if (newest != null && current != null && newest > current)
            {
                Gui.KeyValue("about.update.available",
                    Loc.T("about.update.available.value") + newest, Gui.WarnCol);
            }
            else if (checkedOnce)
            {
                Gui.KeyValue("about.update.status", Loc.T("about.update.latest"), Gui.OkCol);
            }
            else
            {
                Gui.KeyValue("about.update.status", Loc.T("about.update.unknown"), Gui.DimCol);
            }

            // 仓库按钮：只在真的填了 HomePage 时才画。没填还画一颗点不动的按钮，
            // 玩家会以为模组坏了。
            if (!string.IsNullOrEmpty(homepage))
            {
                OnGui.Button("about.update.open", () => OpenUrl(homepage), false);
            }
        }

        private static bool LastUpdateCheckSeen()
        {
            try
            {
                var umm = typeof(UnityModManagerNet.UnityModManager);
                var prop = umm.GetProperty("Params",
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                object ps = prop != null ? prop.GetValue(null, null) : null;
                if (ps == null) return false;
                var fld = ps.GetType().GetField("LastUpdateCheck",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                if (fld == null) return false;
                object v = fld.GetValue(ps);
                if (v is DateTime dt)
                {
                    // 默认值 DateTime.MinValue（从没查过）；任何真实时间都远大于它。
                    return dt.Year > 2000;
                }
                return false;
            }
            catch (Exception ex)
            {
                // 反射失败不能把整个设置页带走（OnGUI 抛异常 = 面板被 UMM 收掉）。
                Debug.LogWarning("[SpectreReplay] 读取上次更新检查时间失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>info.json 里填的 HomePage。取不到就返回空串（调用方据此决定不画按钮）。</summary>
        private static string HomePageUrl()
        {
            try
            {
                return Main.mod != null && Main.mod.Info != null
                    ? Main.mod.Info.HomePage ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// UMM 查到的"最新版本"。**可能为 null**——那表示"UMM 没查到"或"就是最新"，
        /// 两种情况在这里分不开，所以调用方不许把它当成"已是最新"。
        /// </summary>
        private static Version NewestVersionOrNull()
        {
            try
            {
                return Main.mod != null ? Main.mod.NewestVersion : null;
            }
            catch
            {
                return null;
            }
        }

        private static Version CurrentVersionOrNull()
        {
            try
            {
                // **ModEntry.Version 是 System.Version，ModInfo.Version 是 string。**
                // 拿错会编译不过——而这条路径只在"比较版本"时才走到，
                // 用 Info.Version 就得自己 Parse，多一层失败可能。
                return Main.mod != null ? Main.mod.Version : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 打开外部链接。包 try：Application.OpenURL 在无浏览器/被系统拦截时会抛，
        /// 而 OnGUI 抛异常的后果是整个设置面板被 UMM 收掉（玩家感受是"点一下就没了"）。
        /// </summary>
        private static void OpenUrl(string url)
        {
            try
            {
                Application.OpenURL(url);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 打开链接失败：" + ex.Message);
            }
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

        private static void DrawAsyncInputRow()
        {
            OnGui.Row("record.asyncinput", GetDataFromAsyncInput,
                v => { GetDataFromAsyncInput = v; Commit(); });

            bool gameOn;
            try
            {
                gameOn = RDC.useAsyncInput;
            }
            catch
            {
                // 游戏还没初始化：不下结论，也不显示可能误导的红字。
                return;
            }
            if (gameOn)
            {
                // 游戏侧已开 ⇒ 本开关是生效的，不再加噪音。
                return;
            }
            // 游戏侧没开：说明"现在不生效"以及去哪儿开。
            // **不替玩家改写设置值**——他可能只是临时在游戏里关了异步输入，
            // 我们不替他做决定，只把事实讲清楚。
            GUILayout.BeginHorizontal();
            GUILayout.Space(Gui.LabelW);
            GUILayout.Label(Loc.T("record.asyncinput.off"), Gui.NoteStyle,
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
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
