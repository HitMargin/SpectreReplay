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
            OnGui.Row("record.asyncinput", Settings.AsyncInput,
                v => Commit(() => Settings.AsyncInput = v));

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
            if (!Features.Playback.PlaybackEngine.IsPlaying)
            {
                return;
            }
            string name = System.IO.Path.GetFileNameWithoutExtension(
                Features.Playback.PlaybackEngine.CurrentPath ?? "");
            OnGui.Group("playback.current");
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
        internal static void DrawRender()
        {
            OnGui.Group("page.render");
            OnGui.Row("render.on", Settings.RenderOn,
                v => Commit(() => Settings.RenderOn = v));

            // 状态与操作按钮紧跟在开关后面，不放页面最底下：这一页有
            // 十几行，「开始渲染」是主操作，不该藏在最后。
            OnGui.Group("render.status");
            GUILayout.Label(Features.VideoRender.RenderStatus.Describe());
            OnGui.Button("render.start",
                () => Features.VideoRender.RenderService.StartManual(), true);
            OnGui.Button("render.cancel",
                () => Features.VideoRender.RenderService.Cancel(), false);

            OnGui.Group("render.options");
            OnGui.RowDropdown("render.mode", new[]
            {
                Loc.T("render.mode.live"),
                Loc.T("render.mode.offline"),
            }, (int)Settings.Mode, i => Commit(() => Settings.Mode = (Settings.RenderMode)i));
            OnGui.NoteText(Settings.Mode == Settings.RenderMode.Live
                ? Loc.T("render.note.live")
                : Loc.T("render.note.offline"));

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
            OnGui.RowInput("render.outdir",
                v => Commit(() => Settings.OutputDir = v));
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