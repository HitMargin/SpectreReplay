using System;
using SpectreReplay.Ui;
using UnityEngine;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 设置页内容。每个方法只描述"这一页有哪些行"，控件细节交给 Controls。
    ///
    /// 改设置的统一套路：写字段 → <see cref="Settings.Commit"/>（落盘 + 重挂补丁）。
    /// 不在这里直接调 PatchLoader.Refresh()，免得漏掉落盘。
    /// </summary>
    internal static class SettingsPages
    {
        private static void Bind(Transform parent, string label, bool value,
            Action<bool> set)
        {
            Controls.Toggle(parent, Loc.T(label), value, v =>
            {
                set(v);
                Settings.Commit();
            });
        }

        private static void Number(Transform parent, string label, float value,
            float min, float max, bool integer, Action<float> set,
            Func<float, string> format = null)
        {
            Controls.Slider(parent, Loc.T(label), value, min, max, integer, v =>
            {
                set(v);
                Settings.Commit();
            }, format);
        }

        internal static void BuildRecord(Transform content)
        {
            Bind(content, "record.recording", Settings.RecordingOn,
                v => Settings.RecordingOn = v);
            Bind(content, "record.autosave", Settings.AutoSave,
                v => Settings.AutoSave = v);
            Bind(content, "record.asyncinput", Settings.AsyncInput,
                v => Settings.AsyncInput = v);
            Controls.Note(content, Loc.T("record.note"));
        }

        internal static void BuildPlayback(Transform content)
        {
            Controls.SectionTitle(content, Loc.T("page.playback"));
            Controls.Note(content, Loc.T("playback.note"));

            // 当前状态
            var stateRow = Row.Create(content);
            Row.Label(stateRow, Loc.T("playback.current"), 0.5f);
            Ui.LiveLabel.Create(stateRow,
                () => Features.Playback.PlaybackEngine.Describe(), 0.3f);

            // 回放文件列表（最新在前）
            var files = ReplayList();
            if (files.Count == 0)
            {
                Controls.Note(content, Loc.T("playback.empty"));
            }
            else
            {
                foreach (string f in files)
                {
                    var row = Row.Create(content);
                    string path = f;
                    Controls.Button(row, System.IO.Path.GetFileNameWithoutExtension(path), () =>
                    {
                        Features.Playback.PlaybackEngine.Load(path);
                        UiHost.Refresh();
                    }, width: 420f);
                    Controls.Button(row, Loc.T("playback.stop"), () =>
                    {
                        Features.Playback.PlaybackEngine.Stop();
                        UiHost.Refresh();
                    }, width: 140f);
                }
            }
        }

        /// <summary>回放文件列表，按修改时间倒序。</summary>
        private static System.Collections.Generic.List<string> ReplayList()
        {
            var list = new System.Collections.Generic.List<string>();
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

        internal static void BuildRender(Transform content)
        {
            Bind(content, "render.mode", Settings.RenderOn, v => Settings.RenderOn = v);

            var modeRow = Row.Create(content);
            Controls.DropDown(modeRow, Loc.T("render.mode"), new[]
            {
                Loc.T("render.mode.live"),
                Loc.T("render.mode.offline"),
            }, (int)Settings.Mode, i =>
            {
                Settings.Mode = (Settings.RenderMode)i;
                Settings.Commit();
            });

            Controls.Note(content, Settings.Mode == Settings.RenderMode.Live
                ? Loc.T("render.note.live")
                : Loc.T("render.note.offline"));

            Number(content, "render.fps", Settings.Fps, 24f, 144f, true, v => Settings.Fps = (int)v,
                v => v.ToString("0") + " fps");

            var resRow = Row.Create(content);
            Controls.Slider(resRow, Loc.T("render.width"), Settings.Width, 640f, 3840f, true,
                v => Settings.Width = (int)v, v => v.ToString("0") + " px");

            var resRow2 = Row.Create(content);
            Controls.Slider(resRow2, Loc.T("render.height"), Settings.Height, 360f, 2160f, true,
                v => Settings.Height = (int)v, v => v.ToString("0") + " px");

            var surfRow = Row.Create(content);
            Controls.DropDown(surfRow, Loc.T("render.surface"), new[]
            {
                Loc.T("render.surface.clean"),
                Loc.T("render.surface.screen"),
            }, Settings.Surface, i =>
            {
                Settings.Surface = i;
                Settings.Commit();
            });

            var codecRow = Row.Create(content);
            Controls.DropDown(codecRow, Loc.T("render.codec"), new[]
            {
                "auto", "fastest", "quality", "CPU"
            }, Settings.Encoder, i =>
            {
                Settings.Encoder = i;
                Settings.Commit();
            });

            var qRow = Row.Create(content);
            Controls.DropDown(qRow, Loc.T("render.quality"), new[]
            {
                "low", "standard", "high", "extreme"
            }, Settings.Quality, i =>
            {
                Settings.Quality = i;
                Settings.Commit();
            });

            var fRow = Row.Create(content);
            Controls.DropDown(fRow, Loc.T("render.format"), new[]
            {
                "mp4", "mkv", "webm"
            }, Settings.Container, i =>
            {
                Settings.Container = i;
                Settings.Commit();
            });

            Number(content, "render.tail", Settings.TailSeconds, 0f, 15f, false,
                v => Settings.TailSeconds = v, v => v.ToString("0.#") + " s");

            Bind(content, "render.duplicate_when_late", Settings.DuplicateWhenLate,
                v => Settings.DuplicateWhenLate = v);

            var dirRow = Row.Create(content);
            Controls.Input(dirRow, Loc.T("render.outdir"), Settings.OutputDir, v =>
            {
                Settings.OutputDir = v;
                Settings.Commit();
            }, "Mods\\SpectreReplay\\Videos");

            Controls.SectionTitle(content, Loc.T("page.render"));
            var statusRow = Row.Create(content);
            Row.Label(statusRow, Loc.T("render.progress"), 0.62f);
            Features.VideoRender.RenderStatus.Bind(statusRow);

            var actRow = Row.Create(content);
            Controls.Button(actRow, Loc.T("render.start"), () =>
            {
                Features.VideoRender.RenderService.StartManual();
            }, primary: true, width: 200f);
            Controls.Button(actRow, Loc.T("render.cancel"), () =>
            {
                Features.VideoRender.RenderService.Cancel();
            }, width: 160f);
        }

        internal static void BuildAbout(Transform content)
        {
            Number(content, "about.hotkey", Settings.Hotkey, 256f, 322f, true,
                v =>
                {
                    Settings.Hotkey = (int)v;
                    UiHost.Hotkey = Settings.Hotkey;
                    Settings.Commit();
                },
                v => KeyName((KeyCode)(int)v));
            Number(content, "about.textscale", Settings.TextScale, 0.7f, 1.6f, false,
                v =>
                {
                    Settings.TextScale = v;
                    Settings.Commit();
                },
                v => v.ToString("0.00") + "x");
            Controls.Note(content, Loc.T("about.note"));
        }

        /// <summary>键码 → 可读名字。Input.GetKeyName 在这版 Unity 里不可用，
        /// 而设置页只需要把功能键显示出来，自己算更可控。</summary>
        internal static string KeyName(KeyCode k)
        {
            int n = (int)k;
            if (n >= 282 && n <= 292)   // KeyCode 里 F1..F11 是连续的
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
