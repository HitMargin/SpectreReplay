using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using UnityEngine;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 设置页内容。每个方法只描述"这一页有哪些行"。
    /// 行的结构是固定的：左侧标签 + 右侧控件，控件尺寸由 Controls 里的常量给定。
    ///
    /// 改设置的统一套路：写字段 → <see cref="Settings.Commit"/>（落盘 + 重挂补丁）。
    /// </summary>
    internal static class SettingsPages
    {
        private static int _rowParity;

        /// <summary>隔行浅色。行数一多，没有交替底色就分不清行边界。</summary>
        private static Color? Zebra()
            => ((_rowParity++ & 1) == 0) ? null : UiSkin.RowAltBg;

        private static RowRef NewRow(Transform content)
            => Row.Create(content, UiSkin.RowHeight, Zebra());

        // ── 快捷构造 ────────────────────────────────────────────
        private static void Toggle(Transform content, string label, bool value, Action<bool> set)
        {
            Controls.Toggle(NewRow(content), Loc.T(label), value, v =>
            {
                set(v);
                Settings.Commit();
            });
        }

        private static void Number(Transform content, string label, float value,
            float min, float max, bool integer, Action<float> set,
            Func<float, string> format = null)
        {
            Controls.Slider(NewRow(content), Loc.T(label), value, min, max, integer, v =>
            {
                set(v);
                Settings.Commit();
            }, format);
        }

        private static void Pick(Transform content, string label, string[] options,
            int selected, Action<int> set)
        {
            Controls.DropDown(NewRow(content), Loc.T(label), options, selected, i =>
            {
                set(i);
                Settings.Commit();
            });
        }

        // ── 页面 ────────────────────────────────────────────────
        internal static void BuildRecord(Transform content)
        {
            _rowParity = 0;
            Controls.SectionTitle(content, Loc.T("page.record"));
            Toggle(content, "record.recording", Settings.RecordingOn,
                v => Settings.RecordingOn = v);
            Toggle(content, "record.autosave", Settings.AutoSave, v => Settings.AutoSave = v);
            Toggle(content, "record.asyncinput", Settings.AsyncInput,
                v => Settings.AsyncInput = v);
            Controls.Note(content, Loc.T("record.note"));
        }

        internal static void BuildPlayback(Transform content)
        {
            _rowParity = 0;
            Controls.SectionTitle(content, Loc.T("page.playback"));

            var stateRow = NewRow(content);
            Row.Label(stateRow, Loc.T("playback.current"));
            LiveLabel.Create(stateRow, () => Features.Playback.PlaybackEngine.Describe(), 0.3f);

            Controls.Note(content, Loc.T("playback.note"));

            var files = ReplayList();
            if (files.Count == 0)
            {
                Controls.Note(content, Loc.T("playback.empty"));
                return;
            }
            Controls.SectionTitle(content, Loc.T("playback.files"));
            foreach (string f in files)
            {
                var row = NewRow(content);
                string path = f;
                // 左侧显示文件名，右侧放播放/停止
                var name = Row.Label(row, System.IO.Path.GetFileNameWithoutExtension(path));
                name.fontSize = UiSkin.FontSmall * UiSkin.TextScale;
                Controls.Button(row, Loc.T("playback.replay"), () =>
                {
                    Features.Playback.PlaybackEngine.Load(path);
                    UiHost.Refresh();
                }, primary: true, width: 150f);
                Controls.Button(row, Loc.T("playback.stop"), () =>
                {
                    Features.Playback.PlaybackEngine.Stop();
                    UiHost.Refresh();
                }, width: 120f);
            }
        }

        internal static void BuildRender(Transform content)
        {
            _rowParity = 0;
            Controls.SectionTitle(content, Loc.T("page.render"));
            Toggle(content, "render.on", Settings.RenderOn, v => Settings.RenderOn = v);
            Pick(content, "render.mode", new[]
            {
                Loc.T("render.mode.live"),
                Loc.T("render.mode.offline"),
            }, (int)Settings.Mode, i => Settings.Mode = (Settings.RenderMode)i);

            Controls.Note(content, Settings.Mode == Settings.RenderMode.Live
                ? Loc.T("render.note.live")
                : Loc.T("render.note.offline"));

            Number(content, "render.fps", Settings.Fps, 24f, 144f, true,
                v => Settings.Fps = (int)v, v => ((int)v) + " fps");
            Number(content, "render.width", Settings.Width, 640f, 3840f, true,
                v => Settings.Width = (int)v, v => ((int)v) + " px");
            Number(content, "render.height", Settings.Height, 360f, 2160f, true,
                v => Settings.Height = (int)v, v => ((int)v) + " px");
            Number(content, "render.tail", Settings.TailSeconds, 0f, 15f, false,
                v => Settings.TailSeconds = v, v => v.ToString("0.#") + " s");

            Pick(content, "render.surface", new[]
            {
                Loc.T("render.surface.clean"),
                Loc.T("render.surface.screen"),
            }, Settings.Surface, i => Settings.Surface = i);
            Pick(content, "render.codec", new[] { "auto", "fastest", "quality", "CPU" },
                Settings.Encoder, i => Settings.Encoder = i);
            Pick(content, "render.quality", new[] { "low", "standard", "high", "extreme" },
                Settings.Quality, i => Settings.Quality = i);
            Pick(content, "render.format", new[] { "mp4", "mkv", "webm" },
                Settings.Container, i => Settings.Container = i);

            Toggle(content, "render.duplicate_when_late", Settings.DuplicateWhenLate,
                v => Settings.DuplicateWhenLate = v);

            var dirRow = NewRow(content);
            Row.Label(dirRow, Loc.T("render.outdir"));
            Controls.Input(dirRow, Settings.OutputDir, v =>
            {
                Settings.OutputDir = v;
                Settings.Commit();
            }, "Mods\\SpectreReplay\\Videos", 420f);
            Controls.SectionTitle(content, Loc.T("render.status"));
            LiveLabel.CreateFullRow(content,
                () => Features.VideoRender.RenderStatus.Describe(), 0.2f);

            var actRow = NewRow(content);
            Row.Label(actRow, "");
            Controls.Button(actRow, Loc.T("render.start"), () =>
            {
                Features.VideoRender.RenderService.StartManual();
                UiHost.Refresh();
            }, primary: true, width: 180f);
            Controls.Button(actRow, Loc.T("render.cancel"), () =>
            {
                Features.VideoRender.RenderService.Cancel();
                UiHost.Refresh();
            }, width: 140f);
        }

        internal static void BuildAbout(Transform content)
        {
            _rowParity = 0;
            Controls.SectionTitle(content, Loc.T("page.about"));
            Number(content, "about.hotkey", Settings.Hotkey, 282f, 293f, true, v =>
            {
                Settings.Hotkey = (int)v;
                UiHost.Hotkey = Settings.Hotkey;
                Settings.Commit();
            }, v => KeyName((KeyCode)(int)v));
            Number(content, "about.textscale", Settings.TextScale, 0.7f, 1.6f, false, v =>
            {
                Settings.TextScale = v;
                Settings.Commit();
            }, v => v.ToString("0.00") + "x");
            Toggle(content, "about.autoopen", Settings.AutoOpenUi, v => Settings.AutoOpenUi = v);
            Controls.Note(content, Loc.T("about.note"));
        }

        /// <summary>回放文件列表，按修改时间倒序。</summary>
        private static List<string> ReplayList()
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
            UiHost.Page("record", "record", "page.record", BuildRecord);
            UiHost.Page("playback", "playback", "page.playback", BuildPlayback);
            UiHost.Page("render", "render", "page.render", BuildRender);
            UiHost.Page("about", "about", "page.about", BuildAbout);
        }
    }
}
