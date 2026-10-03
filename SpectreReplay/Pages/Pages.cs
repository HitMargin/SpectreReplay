using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using UnityEngine;
using ADOFAI;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 设置页内容。全部用 GUILayout 画（UMM 的 OnGUI）。
    ///
    /// 与之前那套自建 Canvas 的区别：这里没有 RectTransform、没有锚点、
    /// 没有 EventSystem、没有 ScrollRect。控件的排布交给 GUILayout，
    /// 点击与滚轮交给 IMGUI——**踩不到那些坑，因为那些东西根本不存在。**
    ///
    /// 数据源与文案键与旧版完全一致，改动设置的效果也一致。
    /// </summary>
    internal static class Pages
    {
        // ── 录制 ────────────────────────────────────────────────
        internal static void DrawRecord()
        {
            OnGui.Group("page.record");
            OnGui.Note("record.howto");
            OnGui.Row("record.recording", Settings.RecordingOn,
                v => Commit(() => Settings.RecordingOn = v));
            OnGui.Row("record.autosave", Settings.AutoSave,
                v => Commit(() => Settings.AutoSave = v));

            // 原先这里有一行 AsyncInput（"使用高精度输入（SkyHook）"）。已删：
            // 它只被存被读、没有任何一处影响录制行为，勾它等于没勾。
            // 详见 Config\Settings.cs 里那段注释。

            OnGui.Group("record.hotkeys");
            // 按任意键改键。做这一段是因为之前把热键全删了之后，
            // 玩家想主动存一局只能等通关自动保存——练到一半的短局根本存不下来。
            // 做法照 Spectre（Features/QuartzUi/SpectreUiPages.cs）。
            OnGui.RowHotkey("record.savekey", () => Settings.SaveKey,
                k => Settings.SaveKey = k);
            OnGui.RowHotkey("record.cancelSaveKey", () => Settings.CancelSaveKey,
                k => Settings.CancelSaveKey = k);
            OnGui.Note("record.note");
        }

        // ── 回放 ────────────────────────────────────────────────
        internal static void DrawPlayback()
        {
            OnGui.Group("page.playback");
            OnGui.Note("playback.note");

            DrawPlaybackState();

            var files = ReplayList();
            if (files.Count == 0)
            {
                OnGui.Note("playback.empty");
                return;
            }
            OnGui.Group("playback.files");
            foreach (string path in files)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                string captured = path;
                bool loaded = string.Equals(path,
                    Features.Playback.PlaybackEngine.CurrentPath,
                    StringComparison.OrdinalIgnoreCase);
                GUILayout.BeginHorizontal();
                GUILayout.Label(loaded ? name + "  ✔" : name, GUILayout.Width(260f));
                if (GUILayout.Button(Loc.T("playback.replay"),
                        GUILayout.Width(110f), GUILayout.Height(24f)))
                {
                    Features.Playback.PlaybackEngine.Load(captured);
                }
                if (GUILayout.Button(Loc.T("playback.stop"),
                        GUILayout.Width(90f), GUILayout.Height(24f)))
                {
                    Features.Playback.PlaybackEngine.Stop();
                }
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// 回放当前状态 + 待播时的重开入口。
        ///
        /// **这一段是"点了回放没反应"的另一半原因。** 就算 Begin() 那一段
        /// 全对，回放仍然要等关卡重开才开播——不给出这个提示，玩家只会
        /// 看到"点了回放，什么也没发生"。
        ///
        /// 提示条不是纯文字：直接给一个重开按钮。Spectre 靠
        /// PlayActions.StartPlaying + 玩家自己重开来绕过这件事，而这里能
        /// 调 scrController.Restart，少一次"我知道要重开"的认知负担。
        /// </summary>
        private static void DrawPlaybackState()
        {
            // PlaybackEngine 是静态类：只能按类型名访问，不能 var 出来。
            //
            // **这一段以前在 !IsPlaying 时直接 return。** 后果是：没载入回放时
            // 「当前」这一组整个不画，玩家看到的是"这页上面什么都没有"——
            // 分不清是回放没载入、载入了没生效、还是校验把它拒了。而这三件事
            // 的处理方式完全不同（前者去回放页点载入，后者去开那关，后者
            // 只能重录）。
            //
            // 现在**任何时候都画**：没载入时明说"还没载入"，载入失败时把
            // PlaybackEngine.LastError 摆出来（它之前只进日志）。
            bool playing = Features.Playback.PlaybackEngine.IsPlaying;
            OnGui.Group("playback.current");
            if (!playing)
            {
                string err = Features.Playback.PlaybackEngine.LastError;
                GUILayout.Label(string.IsNullOrEmpty(err)
                    ? Loc.T("playback.idle")
                    : Loc.F("playback.idle_err", err));
                return;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(
                Features.Playback.PlaybackEngine.CurrentPath ?? "");
            GUILayout.Label(Features.Playback.PlaybackEngine.IsPending
                ? Loc.F("playback.ready", name)
                : Loc.F("playback.armed", name));

            if (!Features.Playback.PlaybackEngine.IsPending)
            {
                return;
            }
            // 待播状态给两个出口：重开当前关卡（马上能看），或放弃。
            // 只有一个按钮的话，玩家点了"取消"就只能重新载入。
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("playback.restart"),
                    GUILayout.Width(180f), GUILayout.Height(24f)))
            {
                RestartLevel();
            }
            if (GUILayout.Button(Loc.T("playback.cancel"),
                    GUILayout.Width(120f), GUILayout.Height(24f)))
            {
                Features.Playback.PlaybackEngine.CancelPending();
            }
            GUILayout.EndHorizontal();
            DrawTiming();
        }

        /// <summary>
        /// 重开当前关卡，让待播的回放接管。
        ///
        /// 用反射调 scrController.Restart 而不是 scnGame 之类的菜单入口：
        /// Restart 正是 Start_Rewind 的上游（Start_Rewind 会被它调到），
        /// 挂在那里的 Postfix 因此必定触发。
        ///
        /// 拿不到方法就只提示不动作——绝不能在这里抛异常：
        /// OnGUI 里抛异常会被 UMM 吞掉，玩家看见的只是按钮"点了没反应"。
        /// </summary>
        private static void RestartLevel()
        {
            try
            {
                var controller = scrController.instance;
                if (controller == null)
                {
                    return;
                }
                var m = typeof(scrController).GetMethod("Restart",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
                if (m == null)
                {
                    Debug.LogWarning("[SpectreReplay] 找不到 scrController.Restart，无法重开关卡");
                    return;
                }
                // fromBeginning = false：让它照常从当前进度续，而不是回到开头。
                // Start_Rewind 两种情况都会走，回放照样能接管。
                m.Invoke(controller, new object[] { false });
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 重开关卡失败：" + ex);
            }
        }

        // ── 视频渲染 ────────────────────────────────────────────
        /// <summary>
        /// 已载入回放的命中误差统计（照 Spectre 的 ReplayAnalyzer）。
        ///
        /// 数据来源就是偏差 1/1b 补的状态轨：每条命中都记了
        /// <c>CachedAngle − TargetExitAngle</c>，此前那个差值**只被用来
        /// 推进行星、从没被读出来看过**。它其实是"这一击偏了多少"，
        /// 也正是玩家最想知道的：整体偏早、还是某几处单独拖后腿。
        ///
        /// **要求关卡已加载**：误差换算需要 bpm、pitch 与逐砖 speed，
        /// 它们只存在于当前加载的谱面里（Spectre 同此，见 ReplayAnalyzer.cs:12-13）。
        /// 不满足就整块不画——画一个空分组会让人以为"分析结果是 0"。
        /// </summary>
        private static void DrawTiming()
        {
            var report = Features.Playback.PlaybackEngine.TimingReport();
            if (report == null)
            {
                return;
            }
            OnGui.Group("playback.timing");
            GUILayout.Label(Loc.F("playback.timing_summary",
                (report.AnalyzedHits).ToString(),
                report.EarlyCount.ToString(),
                report.LateCount.ToString(),
                Fmt(report.MedianMs)));
            GUILayout.Label(Loc.F("playback.timing_detail",
                Fmt(report.MeanMs), Fmt(report.SigmaMs),
                Fmt(report.MinMs), Fmt(report.MaxMs)));
            // 跳过的那些击要说出来，否则玩家会以为分析漏了数据，
            // 或者更糟——以为那些击的误差是 0。
            int skipped = report.TotalHits - report.AnalyzedHits;
            if (skipped > 0)
            {
                OnGui.NoteText(Loc.F("playback.timing_skipped",
                    skipped.ToString()));
            }
        }

        /// <summary>毫秒数的统一写法。固定一位小数 + 正负号。</summary>
        private static string Fmt(float ms)
            => (ms >= 0 ? "+" : "") + ms.ToString("0.0") + " ms";

        internal static void DrawRender()
        {
            OnGui.Group("page.render");
            OnGui.Row("render.on", Settings.RenderOn,
                v => Commit(() => Settings.RenderOn = v));

            OnGui.Group("render.status");
            // 状态行在**选项之上**：这一页有十行多，「现在在干什么」「怎么
            // 触发」是玩家进这一页要问的第一个问题，不该排在最后。
            GUILayout.Label(Features.VideoRender.RenderStatus.Describe());
            DrawRenderProgress();

            // **实时模式下不画「开始渲染」按钮。**
            //
            // 这一条改动看着小，其实修的是一个语义 bug：RenderService 里
            // OnLevelStart() 在**进关那一刻**就已经 Begin() 了，所以正常流程
            // 里这个按钮永远是个空操作（IsRunning 已经为真，Begin 直接返回）。
            // 玩家点它一下什么也没发生，只会当成"渲染坏了"。而"边玩边录"
            // 根本不需要手动开始——进关就开、结算就收，这是它唯一的卖点。
            // 画一个恒定无效的按钮比不画更糟。
            bool live = Settings.Mode == Settings.RenderMode.Live;
            bool running = Features.VideoRender.LiveSession.IsRunning
                || Features.VideoRender.OfflineSession.IsRunning;
            if (live)
            {
                OnGui.Note("render.note.live");
            }
            else
            {
                // 离线渲染需要一个回放文件。原来这个文件是**静默取的**：
                // LatestReplay() 按修改时间挑最新的那一个，玩家无从知道
                // 自己点下去渲染的是哪一局——渲染完发现"这不是我要的那局"
                // 已经浪费了几分钟。现在把目标文件名明写出来。
                string target = Features.VideoRender.RenderService.LatestReplay();
                if (string.IsNullOrEmpty(target))
                {
                    OnGui.Note("render.offline_noreplay");
                }
                else
                {
                    OnGui.NoteText(Loc.F("render.offline_target",
                        System.IO.Path.GetFileNameWithoutExtension(target)));
                    OnGui.Button("render.start",
                        () => Features.VideoRender.RenderService.StartManual(), true);
                }
                OnGui.Note("render.note.offline");
            }

            // 取消只在真的在跑的时候给。常年摆着一个按不动的「取消」，
            // 和上面那个恒定无效的「开始」是同一类毛病。
            if (running)
            {
                OnGui.Button("render.cancel",
                    () => Features.VideoRender.RenderService.Cancel(), false);
            }

            OnGui.Group("render.options");
            OnGui.RowDropdown("render.mode", new[]
            {
                Loc.T("render.mode.live"),
                Loc.T("render.mode.offline"),
            }, (int)Settings.Mode, i => Commit(() => Settings.Mode = (Settings.RenderMode)i));

            OnGui.RowSlider("render.fps", Settings.Fps, 24f, 144f,
                v => ((int)v) + " fps",
                v => Commit(() => Settings.Fps = (int)v));
            OnGui.RowSlider("render.width", Settings.Width, 640f, 3840f,
                v => ((int)v) + " px",
                v => Commit(() => Settings.Width = (int)v));
            OnGui.RowSlider("render.height", Settings.Height, 360f, 2160f,
                v => ((int)v) + " px",
                v => Commit(() => Settings.Height = (int)v));
            OnGui.RowSlider("render.tail", Settings.TailSeconds, 0f, 15f,
                v => v.ToString("0.#") + " s",
                v => Commit(() => Settings.TailSeconds = v));

            OnGui.RowDropdown("render.surface", new[]
            {
                Loc.T("render.surface.clean"),
                Loc.T("render.surface.screen"),
            }, Settings.Surface, i => Commit(() => Settings.Surface = i));
            // 选项文字走文案表：写死 "auto"/"low" 的话，中文界面里
            // 会夹着几个英文单词。格式名（mp4/mkv/webm）保持原样——
            // 那是实际的文件格式，翻译了反而对不上。
            OnGui.RowDropdown("render.codec", new[]
            {
                Loc.T("render.codec.auto"), Loc.T("render.codec.fastest"),
                Loc.T("render.codec.quality"), Loc.T("render.codec.cpu"),
            }, Settings.Encoder, i => Commit(() => Settings.Encoder = i));
            OnGui.RowDropdown("render.quality", new[]
            {
                Loc.T("render.quality.low"), Loc.T("render.quality.standard"),
                Loc.T("render.quality.high"), Loc.T("render.quality.extreme"),
            }, Settings.Quality, i => Commit(() => Settings.Quality = i));
            OnGui.RowDropdown("render.format", new[] { "mp4", "mkv", "webm" },
                Settings.Container, i => Commit(() => Settings.Container = i));

            OnGui.Row("render.duplicate_when_late", Settings.DuplicateWhenLate,
                v => Commit(() => Settings.DuplicateWhenLate = v));
            OnGui.RowInput("render.outdir");
        }

        /// <summary>
        /// 离线渲染的进度条。
        ///
        /// **只在这一种情况下画**：离线渲染的总帧数事先已知
        /// （回放长度 × 帧率），才谈得上"还剩多少"。
        ///
        /// 实时录制**故意不画**。它那边的 `RenderStatus.Progress` 不是进度，
        /// 是"出帧有没有跟上目标帧率"（`_frameIndex / (elapsed * fps)`，
        /// 见 LiveSession.UpdateStatus）——正常跟随时恒等于 1。拿它填进度条
        /// 会得到一条**永远是满的**横杠，比没有更误导。实时录制用
        /// RenderStatus.Describe() 里的"已录 N 秒"就够说明白了。
        ///
        /// （原先 RenderStatus 里有个 BarFill 属性负责这个二选一，但零调用
        /// 点；这里直接用 FramesTotal 判空，判据和用途写在代码旁边。）
        /// </summary>
        private static void DrawRenderProgress()
        {
            // 判空必须先看总帧数，不能靠"算出来的 fill 是不是 0"。
            //
            // **这里第一版写漏了，闸门 ⑥c 抓出来的。** FramesTotal 是 int，
            // 除法会把它提升成 float，于是 0/0 得到 NaN 而不是抛异常；
            // 而 Mathf.Clamp01 用的是 `<` / `>` 比较，NaN 两边都假，
            // 于是 NaN 被原样返回，`fill <= 0f` 同样为假——
            // 结果是拿 NaN 宽度去 GUI.DrawTexture，Unity 会抛
            // ArgumentException 打断整个 OnGUI，一帧就把窗口画崩。
            // 表现是"点开渲染页窗口直接没了"，且只在总帧数为 0 时发生，
            // 也就是**恰好是最容易复现的那一次**（还没开始渲染就翻到这页）。
            int total = Features.VideoRender.RenderStatus.FramesTotal;
            if (total <= 0)
            {
                return;
            }
            float fill = Mathf.Clamp01((float)
                Features.VideoRender.RenderStatus.FramesDone / total);
            Rect r = GUILayoutUtility.GetRect(OnGui.LabelW + 200f, 18f,
                GUILayout.ExpandWidth(false));
            var old = GUI.color;
            GUI.color = new Color(0.24f, 0.62f, 0.92f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height),
                Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Box(r, GUIContent.none, GUI.skin.box);
            GUI.Label(r, (fill * 100f).ToString("F0") + "%");
        }

        // ── 关于 ────────────────────────────────────────────────
        internal static void DrawAbout()
        {
            OnGui.Group("page.about");
            // 字号与热键两栏都不在 UMM 窗口里生效了：UMM 的字体与窗口
            // 大小是它自己管的。留着这两行只会让人以为改了有用。
            OnGui.Note("about.note");
        }

        // ── 工具 ────────────────────────────────────────────────
        /// <summary>
        /// 改一个设置。
        ///
        /// IMGUI 每帧重画，**不需要 Refresh**——之前那套 Canvas 里不重画
        /// 就什么都看不见（用户拖滑条、面板纹丝不动），那是自建 UI 的
        /// 负担，UMM 这边不存在。
        /// </summary>
        private static void Commit(Action mutate)
        {
            mutate?.Invoke();
            Settings.Commit();
        }

        /// <summary>回放文件列表，按修改时间倒序。</summary>
        internal static List<string> ReplayList()
        {
            var list = new List<string>();
            try
            {
                string dir = Settings.ReplayDir;
                if (!System.IO.Directory.Exists(dir))
                {
                    return list;
                }
                var files = System.IO.Directory.GetFiles(dir, "*.srp");
                System.Array.Sort(files, (a, b) =>
                    System.IO.File.GetLastWriteTimeUtc(b).CompareTo(
                        System.IO.File.GetLastWriteTimeUtc(a)));
                list.AddRange(files);
            }
            catch
            {
                // 目录不可读就当作没有回放
            }
            return list;
        }

        /// <summary>键码 → 可读名字。Input.GetKeyName 在这版 Unity 里不可用。</summary>
        internal static string KeyName(KeyCode k)
        {
            int n = (int)k;
            if (n >= 282 && n <= 293)   // KeyCode 里 F1..F12 是连续的
            {
                return "F" + (n - 281);
            }
            return k.ToString();
        }
    }
}