using System;
using System.Collections.Generic;
using System.IO;
using SpectreReplay.Ui;
using UnityEngine;
using ADOFAI;
using static SpectreReplay.Settings;
using SpectreReplay.Features.Replay;
using static SpectreReplay.SpectreState;
using static SpectreReplay.Features.Replay.ReplayKeys;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 设置页内容（UMM OnGUI）。移植 Spectre 的 Replay + VideoRender 后，
    /// 页面读的是 SpectreState（录制/回放状态）与 Features.VideoRender.VideoRender（渲染状态）。
    /// </summary>
    internal static class Pages
    {
        internal static void DrawRecord()
        {
            OnGui.Group("page.record");
            OnGui.Note("record.howto");
            OnGui.Row("record.recording", ReplayOn, v => { ReplayOn = v; Commit(); });
            OnGui.Row("record.autosave", AutoSave, v => { AutoSave = v; Commit(); });
            OnGui.Group("record.hotkeys");
            OnGui.RowHotkey("record.savekey", () => SaveButton, k => { SaveButton = k; Commit(); });
            OnGui.RowHotkey("record.cancelsavekey", () => CancelSaveButton, k => { CancelSaveButton = k; Commit(); });
            OnGui.Note("record.note");
        }

        internal static void DrawPlayback()
        {
            OnGui.Group("page.playback");
            OnGui.Note("playback.note");
            DrawPlaybackState();
            var files = ReplayList();
            if (files.Count == 0) { OnGui.Note("playback.empty"); return; }
            OnGui.Group("playback.files");
            foreach (string path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                GUILayout.BeginHorizontal();
                GUILayout.Label(name, GUILayout.Width(260f));
                if (GUILayout.Button(Loc.T("playback.replay"), GUILayout.Width(110f), GUILayout.Height(24f)))
                    LoadReplayFile(path);
                GUILayout.EndHorizontal();
            }
        }

        private static void LoadReplayFile(string path)
        {
            try
            {
                if (!ReplayManager.LoadReplayAuto(out LegacyReplayData loaded, path))
                {
                    Status.Warn("playback.idle_err");
                    return;
                }
                data = loaded;
                PlayActions.SwitchToPlayMode();
                Status.Say("status.loaded", Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex) { Debug.LogWarning("[SpectreReplay] 载入回放失败：" + ex); }
        }

        private static void DrawPlaybackState()
        {
            OnGui.Group("playback.current");
            if (is_playing)
            {
                GUILayout.Label(Loc.F("playback.armed", data?.strings?.TryGetValue(ReplayKeys.SongName, out var sn) == true ? sn : "?"));
                if (GUILayout.Button(Loc.T("playback.stop"), GUILayout.Width(120f), GUILayout.Height(24f)))
                    PlayActions.StopPlaying();
            }
            else if (is_recording)
            {
                GUILayout.Label(Loc.T("status.recording"));
                if (GUILayout.Button(Loc.T("record.cancelsavekey"), GUILayout.Width(140f), GUILayout.Height(24f)))
                    PlayActions.StopRecording();
            }
            else
            {
                GUILayout.Label(Loc.T("playback.idle"));
            }
        }

        internal static void DrawRender()
        {
            OnGui.Group("page.render");
            OnGui.Row("render.on", ReplayOn, v => { ReplayOn = v; Commit(); });;
            GUILayout.Label(DescribeRender());
            DrawRenderProgress();
            if (Features.VideoRender.VideoRender.Active)
                OnGui.Button("render.cancel", () => Features.VideoRender.VideoRender.CancelRender(), false);
            else
                OnGui.Button("render.start", () => Features.VideoRender.VideoRender.StartRender(), true);
            OnGui.Group("render.options");
            OnGui.RowSlider("render.width", RenderWidth, 320f, 7680f, v => ((int)v) + " px", v => { RenderWidth = (int)v; Commit(); });
            OnGui.RowSlider("render.height", RenderHeight, 240f, 4320f, v => ((int)v) + " px", v => { RenderHeight = (int)v; Commit(); });
            OnGui.RowSlider("render.fps", RenderFps, 15f, 480f, v => ((int)v) + " fps", v => { RenderFps = (int)v; Commit(); });
            OnGui.RowDropdown("render.surface", new[] { Loc.T("render.surface.clean"), Loc.T("render.surface.screen") }, RenderCaptureMode, i => { RenderCaptureMode = i; Commit(); });
            OnGui.RowDropdown("render.format", new[] { "mp4", "mkv", "webm", "mov" }, RenderOutputFormat, i => { RenderOutputFormat = i; Commit(); });
            OnGui.RowInput("render.outdir");
            OnGui.Note("render.note.offline");
        }

        private static string DescribeRender()
        {
            var s = Features.VideoRender.VideoRender.Current;
            if (Features.VideoRender.VideoRender.Active)
            {
                string stage = s.Stage ?? "";
                return stage + (s.FramesTotal > 0 ? "  " + s.FramesDone + "/" + s.FramesTotal : "");
            }
            return Loc.T("render.state.done");
        }

        private static void DrawRenderProgress()
        {
            var s = Features.VideoRender.VideoRender.Current;
            if (!Features.VideoRender.VideoRender.Active || s.FramesTotal <= 0) return;
            float fill = Mathf.Clamp01((float)s.FramesDone / s.FramesTotal);
            Rect r = GUILayoutUtility.GetRect(OnGui.LabelW + 200f, 18f, GUILayout.ExpandWidth(false));
            var old = GUI.color;
            GUI.color = new Color(0.24f, 0.62f, 0.92f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * fill, r.height), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Box(r, GUIContent.none, GUI.skin.box);
            GUI.Label(r, (fill * 100f).ToString("F0") + "%");
        }

        internal static void DrawAbout()
        {
            OnGui.Group("page.about");
            OnGui.Note("about.note");
        }

        private static void Commit()
        {
            ConfigManager.SaveConfigs(Path.Combine(Main.mod.Path, "Configs.json"));
            Settings.check(Main.mod.Path);
            SpectrePatchHub.Refresh();
        }

        internal static List<string> ReplayList()
        {
            var list = new List<string>();
            try
            {
                string dir = SavePath;
                if (string.IsNullOrEmpty(dir)) dir = Main.mod.Path;
                if (!Directory.Exists(dir)) return list;
                var files = Directory.GetFiles(dir, "*.sprp");
                Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                list.AddRange(files);
            }
            catch { }
            return list;
        }
    }
}
