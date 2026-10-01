using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using UnityEngine;
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
            OnGui.Row("record.recording", Settings.RecordingOn,
                v => Commit(() => Settings.RecordingOn = v));
            OnGui.Row("record.autosave", Settings.AutoSave,
                v => Commit(() => Settings.AutoSave = v));
            OnGui.Row("record.asyncinput", Settings.AsyncInput,
                v => Commit(() => Settings.AsyncInput = v));
            OnGui.Note("record.note");
        }

        // ── 回放 ────────────────────────────────────────────────
        internal static void DrawPlayback()
        {
            OnGui.Group("page.playback");
            OnGui.Note("playback.note");

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
                GUILayout.BeginHorizontal();
                GUILayout.Label(name, GUILayout.Width(260f));
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