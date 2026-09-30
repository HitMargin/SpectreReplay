namespace SpectreReplay.Ui
{
    /// <summary>
    /// 界面文案的最小实现。
    ///
    /// 与 Spectre 的 LocalizationManager 不同：这里不建 14 语言大表，先把
    /// 中文/英文两种做实（键缺失时回退到键名本身，便于一眼看出漏了哪条）。
    /// 后续要补语言只需往 Dictionaries 里加。
    /// </summary>
    internal static class Loc
    {
        internal const string Brand = "Spectre Replay";
        internal const string Hint = "F9 开关界面 · ESC 关闭";

        private static readonly System.Collections.Generic.Dictionary<string, string> Zh =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "cat.record", "录制" },
                { "cat.playback", "回放" },
                { "cat.render", "视频渲染" },
                { "cat.about", "关于" },

                { "page.record", "录制" },
                { "page.playback", "回放" },
                { "page.render", "视频渲染" },
                { "page.about", "关于" },

                { "record.autosave", "自动保存回放" },
                { "record.asyncinput", "使用高精度输入（SkyHook）" },
                { "record.recording", "录制中" },
                { "record.note", "录制时按下按键即可。回放文件保存在 Mods/SpectreReplay/Replays/。" },
                { "playback.current", "当前" },
                { "playback.empty", "还没有回放文件。打一局关卡就会自动保存。" },
                { "playback.stop", "停止回放" },
                { "playback.note", "回放由录制时的按键序列驱动，可用于复盘与练习。" },
                { "render.mode", "渲染模式" },
                { "render.mode.live", "边玩边录（推荐）" },
                { "render.mode.offline", "手动离线渲染" },
                { "render.fps", "输出帧率" },
                { "render.width", "宽度" },
                { "render.height", "高度" },
                { "render.surface", "画面来源" },
                { "render.surface.clean", "纯净相机（不含界面）" },
                { "render.surface.screen", "游戏画面（含界面）" },
                { "render.codec", "编码器" },
                { "render.quality", "画质" },
                { "render.format", "输出格式" },
                { "render.tail", "结尾多录秒数" },
                { "render.outdir", "输出目录" },
                { "render.start", "开始渲染" },
                { "render.cancel", "取消" },
                { "render.progress", "渲染进度" },
                { "render.note.live", "边玩边录：进入关卡自动开始，结算后自动完成。不需要再跑一遍关卡。" },
                { "render.note.offline", "离线渲染：重跑一遍回放逐帧捕获，耗时更长但画面与原回放完全一致。" },
                { "about.hotkey", "界面热键" },
                { "about.textscale", "界面字号" },
                { "about.note", "SpectreReplay 是独立模组，不依赖 Spectre，两者可同时启用。" },

                { "render.duplicate_when_late", "跟不上时复制上一帧" },
                { "playback.files", "回放文件" },
                { "playback.replay", "回放" },
                { "render.on", "启用视频渲染" },
                { "render.status", "状态" },
                { "about.autoopen", "进关时自动打开界面" },
                { "render.state.live", "录制中" },
                { "render.state.offline", "离线渲染中" },
                { "render.state.finalizing", "合成中" },
                { "render.state.done", "完成" },
                { "render.state.failed", "失败" },
                { "render.state.cancelled", "已取消" },
            };

        private static readonly System.Collections.Generic.Dictionary<string, string> En =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "cat.record", "Recording" },
                { "cat.playback", "Playback" },
                { "cat.render", "Video" },
                { "cat.about", "About" },

                { "page.record", "Recording" },
                { "page.playback", "Playback" },
                { "page.render", "Video Render" },
                { "page.about", "About" },

                { "record.autosave", "Auto-save replays" },
                { "record.asyncinput", "High-precision input (SkyHook)" },
                { "record.recording", "Recording" },
                { "record.note", "Keys pressed while playing are captured. Replays are stored in Mods/SpectreReplay/Replays/." },
                { "playback.current", "Current" },
                { "playback.empty", "No replays yet. Play a level and one is saved automatically." },
                { "playback.stop", "Stop" },
                { "playback.note", "Playback is driven by the recorded key stream." },
                { "render.mode", "Render mode" },
                { "render.mode.live", "Record while playing (recommended)" },
                { "render.mode.offline", "Manual offline render" },
                { "render.fps", "Output FPS" },
                { "render.width", "Width" },
                { "render.height", "Height" },
                { "render.surface", "Frame source" },
                { "render.surface.clean", "Clean camera (no UI)" },
                { "render.surface.screen", "Game screen (with UI)" },
                { "render.codec", "Encoder" },
                { "render.quality", "Quality" },
                { "render.format", "Output format" },
                { "render.tail", "Extra tail seconds" },
                { "render.outdir", "Output folder" },
                { "render.start", "Render" },
                { "render.cancel", "Cancel" },
                { "render.progress", "Progress" },
                { "render.note.live", "Record-while-playing: starts automatically when you enter a level and finishes on results. The level is not replayed." },
                { "render.note.offline", "Offline: replays the level frame by frame. Slower, but pixel-identical to the replay." },
                { "about.hotkey", "UI hotkey" },
                { "about.textscale", "UI font scale" },
                { "about.note", "SpectreReplay is standalone and does not require Spectre; both can be enabled at once." },

                { "render.duplicate_when_late", "Duplicate last frame when behind" },
                { "playback.files", "Replays" },
                { "playback.replay", "Replay" },
                { "render.on", "Enable video render" },
                { "render.status", "Status" },
                { "about.autoopen", "Open panel on level start" },
                { "render.state.live", "Recording" },
                { "render.state.offline", "Offline render" },
                { "render.state.finalizing", "Finalizing" },
                { "render.state.done", "Done" },
                { "render.state.failed", "Failed" },
                { "render.state.cancelled", "Cancelled" },
            };

        internal static string Cat(string category) => T("cat." + category);
        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            if (En.TryGetValue(key, out string en))
            {
                return en;
            }
            if (Zh.TryGetValue(key, out string zh))
            {
                return zh;
            }
            return key;      // 漏翻时把键名露出来，比空白好排查
        }
    }
}
