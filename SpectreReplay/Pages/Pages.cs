using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using TMPro;
using UnityEngine;

namespace SpectreReplay.Pages
{
    /// <summary>设置页内容。只描述"这一页有哪些行"，尺寸全由 PageBuilder 处理。</summary>
    internal static class Pages
    {
        // ── 录制 ────────────────────────────────────────────────
        internal static void BuildRecord(PageBuilder b)
        {
            b.Group("page.record");
            b.Toggle("record.recording", Settings.RecordingOn,
                v => Commit(() => Settings.RecordingOn = v));
            b.Toggle("record.autosave", Settings.AutoSave,
                v => Commit(() => Settings.AutoSave = v));
            b.Toggle("record.asyncinput", Settings.AsyncInput,
                v => Commit(() => Settings.AsyncInput = v));
            b.Note("record.note");
        }

        // ── 回放 ────────────────────────────────────────────────
        internal static void BuildPlayback(PageBuilder b)
        {
            b.Group("page.playback");
            b.Note("playback.note");

            var files = ReplayList();
            if (files.Count == 0)
            {
                b.Note("playback.empty");
                return;
            }
            b.Group("playback.files");
            foreach (string path in files)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                string captured = path;
                // 一行：文件名 + 回放 + 停止。两个按钮都贴右边，
                // 第一个的宽度就是文件名要让出的空间——行宽由锚点决定，
                // 这里只需要给出"控件一共占多宽"。
                var row = b.BlankRow(150f + 10f + 120f);
                var nameText = Gfx.TextFill(row, "Name", name, Theme.FontSmall, Theme.Text);
                Gfx.Stretch((RectTransform)nameText.transform, Theme.PadX, 0f,
                    150f + 10f + 120f + Theme.PadX + Theme.LabelGap, 0f);

                // 停止按钮贴最右；回放按钮在它左边 130 处（120 宽 + 10 间隙）
                Gfx.Button(row, (Theme.RowH - Theme.CtlButtonH) * 0.5f, 120f,
                    Loc.T("playback.stop"), false,
                    () =>
                    {
                        Features.Playback.PlaybackEngine.Stop();
                        PanelHost.Refresh();
                    });
                Gfx.Button(row, (Theme.RowH - Theme.CtlButtonH) * 0.5f, 150f,
                    Loc.T("playback.replay"), true,
                    () =>
                    {
                        Features.Playback.PlaybackEngine.Load(captured);
                        PanelHost.Refresh();
                    }, offsetFromRight: 130f);
            }
        }

        // ── 视频渲染 ────────────────────────────────────────────
        internal static void BuildRender(PageBuilder b)
        {
            b.Group("page.render");
            b.Toggle("render.on", Settings.RenderOn,
                v => Commit(() => Settings.RenderOn = v));
            b.Drop("render.mode", new[]
            {
                Loc.T("render.mode.live"),
                Loc.T("render.mode.offline"),
            }, (int)Settings.Mode, i => Commit(() => Settings.Mode = (Settings.RenderMode)i));
            b.NoteText(Settings.Mode == Settings.RenderMode.Live
                ? Loc.T("render.note.live")
                : Loc.T("render.note.offline"));

            b.Slider("render.fps", Settings.Fps, 24f, 144f, true,
                v => Commit(() => Settings.Fps = (int)v), v => ((int)v) + " fps");
            b.Slider("render.width", Settings.Width, 640f, 3840f, true,
                v => Commit(() => Settings.Width = (int)v), v => ((int)v) + " px");
            b.Slider("render.height", Settings.Height, 360f, 2160f, true,
                v => Commit(() => Settings.Height = (int)v), v => ((int)v) + " px");
            b.Slider("render.tail", Settings.TailSeconds, 0f, 15f, false,
                v => Commit(() => Settings.TailSeconds = v), v => v.ToString("0.#") + " s");

            b.Drop("render.surface", new[]
            {
                Loc.T("render.surface.clean"),
                Loc.T("render.surface.screen"),
            }, Settings.Surface, i => Commit(() => Settings.Surface = i));
            // 选项文字走文案表：写死 "auto"/"low" 的话，中文界面里
            // 会夹着几个英文单词，看着像没做完。格式名（mp4/mkv/webm）
            // 保持原样——那是实际的文件格式，翻译了反而对不上。
            b.Drop("render.codec", new[]
            {
                Loc.T("render.codec.auto"), Loc.T("render.codec.fastest"),
                Loc.T("render.codec.quality"), Loc.T("render.codec.cpu"),
            }, Settings.Encoder, i => Commit(() => Settings.Encoder = i));
            b.Drop("render.quality", new[]
            {
                Loc.T("render.quality.low"), Loc.T("render.quality.standard"),
                Loc.T("render.quality.high"), Loc.T("render.quality.extreme"),
            }, Settings.Quality, i => Commit(() => Settings.Quality = i));
            b.Drop("render.format", new[] { "mp4", "mkv", "webm" },
                Settings.Container, i => Commit(() => Settings.Container = i));

            b.Toggle("render.duplicate_when_late", Settings.DuplicateWhenLate,
                v => Commit(() => Settings.DuplicateWhenLate = v));
            b.Input("render.outdir", Settings.OutputDir, "Mods\\SpectreReplay\\Videos",
                v => Commit(() => Settings.OutputDir = v));

            b.Group("render.status");
            var statusRow = b.BlankRow(0f);
            var st = Gfx.TextFill(statusRow, "Status", "", Theme.FontSmall, Theme.TextMuted);
            Gfx.Stretch((RectTransform)st.transform, Theme.PadX, 0f, Theme.PadX, 0f);
            PanelHost.RegisterLive(st,
                () => Features.VideoRender.RenderStatus.Describe());

            b.Button("render.start", true,
                () =>
                {
                    Features.VideoRender.RenderService.StartManual();
                    PanelHost.Refresh();
                });
            b.Button("render.cancel", false,
                () =>
                {
                    Features.VideoRender.RenderService.Cancel();
                    PanelHost.Refresh();
                });
        }

        // ── 关于 ────────────────────────────────────────────────
        internal static void BuildAbout(PageBuilder b)
        {
            b.Group("page.about");
            b.Slider("about.hotkey", Settings.Hotkey, 282f, 293f, true, v =>
            {
                Settings.Hotkey = (int)v;
                PanelHost.Hotkey = Settings.Hotkey;
                Commit(null);
            }, v => KeyName((KeyCode)(int)v));
            b.Slider("about.textscale", Settings.TextScale, 0.7f, 1.6f, false,
                v => Commit(() => Settings.TextScale = v), v => v.ToString("0.00") + "x");
            b.Toggle("about.autoopen", Settings.AutoOpenUi,
                v => Commit(() => Settings.AutoOpenUi = v));
            b.Note("about.note");
        }

        // ── 工具 ────────────────────────────────────────────────
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

        internal static void RegisterAll()
        {
            PanelHost.Page("record", "record", "page.record", BuildRecord);
            PanelHost.Page("playback", "playback", "page.playback", BuildPlayback);
            PanelHost.Page("render", "render", "page.render", BuildRender);
            PanelHost.Page("about", "about", "page.about", BuildAbout);
        }
    }
}
