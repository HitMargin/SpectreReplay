// 预览用的页面内容。**必须与 SpectreReplay\Pages\Pages.cs 保持同步** ——
// 这里不链接 Pages.cs 是因为它反过来依赖 Features 下的回放/渲染引擎。
// 两者不同步的话，预览就与实际不符，验证也就失去意义。
using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using TMPro;
using UnityEngine;

namespace UiPreview
{
    internal static class Pages
    {
        // 预览不真的读写配置，全部用固定值
        private const bool RecOn = true, AutoSave = true, Async = false;
        private const bool RenderOn = true, DupLate = false;
        private const float Fps = 60, Width = 1920, Height = 1080, Tail = 4.5f;
        private const float Scale = 1f;
        private const int Hotkey = 290;

        internal static void BuildRecord(PageBuilder b)
        {
            b.Group("page.record");
            b.Toggle("record.recording", RecOn, _ => { });
            b.Toggle("record.autosave", AutoSave, _ => { });
            b.Toggle("record.asyncinput", Async, _ => { });
            b.Note("record.note");
        }

        internal static void BuildPlayback(PageBuilder b)
        {
            b.Group("page.playback");
            b.Note("playback.note");
            // 造几条假的回放条目：这一页最复杂的是"文件名 + 两枚按钮"的行内排布，
            // 测空列表的话预览永远验不到它。
            b.Group("playback.files");
            foreach (string f in new[]
            {
                "20261001-143255-a1b2c3d4", "20261001-150102-9f8e7d6c",
            })
            {
                var row = b.BlankRow(150f + 10f + 120f);
                var nm = Gfx.TextFill(row, "Name", f, Theme.FontSmall, Theme.Text);
                Gfx.Stretch((RectTransform)nm.transform, Theme.PadX, 0f,
                    150f + 10f + 120f + Theme.PadX + Theme.LabelGap, 0f);
                Gfx.Button(row, (Theme.RowH - Theme.CtlButtonH) * 0.5f, 120f,
                    Loc.T("playback.stop"), false, () => { });
                Gfx.Button(row, (Theme.RowH - Theme.CtlButtonH) * 0.5f, 150f,
                    Loc.T("playback.replay"), true, () => { }, offsetFromRight: 130f);
            }
        }

        internal static void BuildRender(PageBuilder b)
        {
            b.Group("page.render");
            b.Toggle("render.on", RenderOn, _ => { });
            b.Drop("render.mode", new[] { Loc.T("render.mode.live"), Loc.T("render.mode.offline") },
                0, _ => { });
            b.NoteText(Loc.T("render.note.live"));
            b.Slider("render.fps", Fps, 24, 144, true, _ => { }, v => ((int)v) + " fps");
            b.Slider("render.width", Width, 640, 3840, true, _ => { }, v => ((int)v) + " px");
            b.Slider("render.height", Height, 360, 2160, true, _ => { }, v => ((int)v) + " px");
            b.Slider("render.tail", Tail, 0, 15, false, _ => { }, v => v.ToString("0.#") + " s");
            b.Drop("render.surface", new[]
            { Loc.T("render.surface.clean"), Loc.T("render.surface.screen") }, 0, _ => { });
            b.Drop("render.codec", new[] { "auto", "fastest", "quality", "CPU" }, 0, _ => { });
            b.Drop("render.quality", new[] { "low", "standard", "high", "extreme" }, 0, _ => { });
            b.Drop("render.format", new[] { "mp4", "mkv", "webm" }, 0, _ => { });
            b.Toggle("render.duplicate_when_late", DupLate, _ => { });
            b.Input("render.outdir", "Mods\\SpectreReplay\\Videos",
                "Mods\\SpectreReplay\\Videos", _ => { });
            b.Group("render.status");
            var sr = b.BlankRow(0f);
            var st = Gfx.TextFill(sr, "Status", "", Theme.FontSmall, Theme.TextMuted);
            Gfx.Stretch((RectTransform)st.gameObject.transform, Theme.PadX, 0, Theme.PadX, 0);
            b.Button("render.start", true, () => { });
            b.Button("render.cancel", false, () => { });
        }

        internal static void BuildAbout(PageBuilder b)
        {
            b.Group("page.about");
            b.Slider("about.hotkey", Hotkey, 282, 293, true, _ => { },
                v => "F" + ((int)v - 281));
            b.Slider("about.textscale", Scale, 0.7f, 1.6f, false, _ => { },
                v => v.ToString("0.00") + "x");
            b.Toggle("about.autoopen", false, _ => { });
            b.Note("about.note");
        }
    }
}