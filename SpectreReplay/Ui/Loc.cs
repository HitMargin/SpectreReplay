using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 界面文案。中文/英文两套，按游戏语言自动选。
    ///
    /// 与 Spectre 的 LocalizationManager 不同：这里不建 14 语言大表，
    /// 先把两种做实。选不中时回退到另一套，再不中才露出键名——把键名露给
    /// 玩家是最糟的兜底，只该出现在"真的漏了"的时候。
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
                { "record.howto", "① 在这里设好参数（快捷键：F7 打开本窗口）② 进关卡正常玩，按键会被记下来 ③ 通关后自动存成回放文件 ④ 在「回放」页载入，或在「视频渲染」页出片。" },
                { "record.hotkeys", "快捷键" },
                { "record.savekey", "保存回放" },
                { "record.cancelsavekey", "取消保存" },
                { "ui.presskey", "按下任意键…" },
                { "ui.nokey", "未绑定" },
                { "playback.current", "当前" },
                { "playback.empty", "还没有回放文件。打一局关卡就会自动保存。" },
                { "playback.stop", "停止回放" },
                { "playback.note", "回放由录制时的按键序列驱动，可用于复盘与练习。" },
                { "playback.ready", "已装好：{0}——重新开始这关就会自动播放。" },
                { "playback.armed", "正在播放：{0}" },
                { "playback.restart", "重开这关并播放" },
                { "playback.cancel", "取消待播" },
                { "playback.unusable", "这个回放里有 {0} 个按键，文件多半是空的。重新打一局再试。" },
                { "render.mode", "渲染模式" },
                { "render.mode.live", "边玩边录（推荐）" },
                { "render.mode.offline", "手动离线渲染" },
                { "render.fps", "输出帧率" },
                { "render.width", "宽度" },
                { "render.height", "高度" },
                { "render.surface", "画面来源" },
                { "render.codec.auto", "自动" },
                { "render.codec.fastest", "最快" },
                { "render.codec.quality", "画质优先" },
                { "render.codec.cpu", "仅 CPU" },
                { "render.quality.low", "低" },
                { "render.quality.standard", "标准" },
                { "render.quality.high", "高" },
                { "render.quality.extreme", "极高" },
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
                { "about.note", "SpectreReplay 是独立模组，不依赖 Spectre，两者可同时启用。" },

                // ── 状态提示 ──────────────────────────────────────
                // 之前一个都没有：录制开始了没人知道、回放载入了没人知道、
                // 渲染出片了没人知道。整个模组于是"像没生效"。
                // Spectre 有 TriggerMessage 这一套（见它的 SpectreState.cs），
                // 靠它自己场景里的 Notification 预制体——我们不能依赖那个资源，
                // 改在自己的设置页顶部常驻一行，见 OnGui.DrawStatus。
                { "status.idle", "空闲：没在录制，也没在渲染。" },
                { "status.recording", "正在录制本局按键…" },
                { "status.playing", "回放进行中…" },
                { "status.pending_replay", "回放已装好：{0}。重新开始这关（或从关卡列表再进一次）就会自动播放。" },
                { "status.rendering", "正在渲染…{0}" },
                { "status.saved", "回放已保存：{0}" },
                { "status.saved_none", "本局没有录到按键，未保存（这是正常的，空关不会产生回放）。" },
                { "status.loaded", "已载入回放：{0}" },
                { "status.loaded_none", "没有可载入的回放文件。" },
                { "status.stopped", "回放已停止。" },
                { "status.render_done", "渲染完成：{0}" },
                { "status.render_fail", "渲染失败：{0}" },
                { "status.render_start", "开始渲染…" },
                { "status.cancelled", "已取消。" },

                { "render.duplicate_when_late", "跟不上时复制上一帧" },
                { "playback.files", "回放文件" },
                { "playback.replay", "回放" },
                { "render.on", "启用视频渲染" },
                { "render.status", "状态" },
                { "render.err.ffmpeg_start", "无法启动 ffmpeg：{0}" },
                { "render.err.ffmpeg_timeout", "ffmpeg 超时未退出" },
                { "render.err.encoder_exit", "编码器未正常退出" },
                { "render.err.encoder_gone", "编码器进程已退出" },
                { "render.err.checkpoints", "判定时刻数量异常：{0}" },
                { "render.err.str_len", "字符串长度异常：{0}" },
{ "render.options", "渲染选项" },
                { "playback.structure_mismatch", "SkyHook 结构不匹配，无法回放" },
                { "render.err.no_replay", "没有可用的回放文件" },
                { "render.err.need_level", "请先进入关卡再开始离线渲染" },
                { "replay.err.not_replay", "不是 SpectreReplay 回放文件" },
                { "replay.err.version", "回放文件版本 {0} 高于本版支持的 {1}" },
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
                { "record.howto", "1) Set options here (press F7 to open this window) 2) Play a level normally - your keys are recorded 3) Finishing saves a replay file 4) Load it on the Playback tab, or render it on the Video Render tab." },
                { "record.hotkeys", "Hotkeys" },
                { "record.savekey", "Save replay" },
                { "record.cancelsavekey", "Cancel save" },
                { "ui.presskey", "Press any key..." },
                { "ui.nokey", "Not bound" },
                { "playback.current", "Current" },
                { "playback.empty", "No replays yet. Play a level and one is saved automatically." },
                { "playback.stop", "Stop" },
                { "playback.note", "Playback is driven by the recorded key stream." },
                { "playback.ready", "Ready: {0} — restart this level to play it automatically." },
                { "playback.armed", "Playing: {0}" },
                { "playback.restart", "Restart & play" },
                { "playback.cancel", "Cancel" },
                { "playback.unusable", "This replay holds {0} keys, so the file is most likely empty. Play the level again and retry." },
                { "render.mode", "Render mode" },
                { "render.mode.live", "Record while playing" },
                { "render.mode.offline", "Manual offline render" },
                { "render.fps", "Output FPS" },
                { "render.width", "Width" },
                { "render.height", "Height" },
                { "render.surface", "Frame source" },
                { "render.codec.auto", "Auto" },
                { "render.codec.fastest", "Fastest" },
                { "render.codec.quality", "Quality first" },
                { "render.codec.cpu", "CPU only" },
                { "render.quality.low", "Low" },
                { "render.quality.standard", "Standard" },
                { "render.quality.high", "High" },
                { "render.quality.extreme", "Extreme" },
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
                { "about.note", "SpectreReplay is standalone and does not require Spectre; both can be enabled at once." },

                { "status.idle", "Idle: not recording, not rendering." },
                { "status.recording", "Recording your keys..." },
                { "status.playing", "Replay in progress..." },
                { "status.pending_replay", "Replay loaded: {0}. Restart this level (or re-enter it from the level list) to play it automatically." },
                { "status.rendering", "Rendering... {0}" },
                { "status.saved", "Replay saved: {0}" },
                { "status.saved_none", "No keys were recorded this run, so nothing was saved (normal for an empty level)." },
                { "status.loaded", "Replay loaded: {0}" },
                { "status.loaded_none", "No replay file available." },
                { "status.stopped", "Replay stopped." },
                { "status.render_done", "Render finished: {0}" },
                { "status.render_fail", "Render failed: {0}" },
                { "status.render_start", "Rendering started..." },
                { "status.cancelled", "Cancelled." },

                { "render.duplicate_when_late", "Duplicate last frame when behind" },
                { "playback.files", "Replays" },
                { "playback.replay", "Replay" },
                { "render.on", "Enable video render" },
                { "render.status", "Status" },
                { "render.err.ffmpeg_start", "Cannot start ffmpeg: {0}" },
                { "render.err.ffmpeg_timeout", "ffmpeg timed out and did not exit" },
                { "render.err.encoder_exit", "Encoder did not exit cleanly" },
                { "render.err.encoder_gone", "Encoder process has exited" },
                { "render.err.checkpoints", "Bad checkpoint count: {0}" },
                { "render.err.str_len", "Bad string length: {0}" },
{ "render.options", "Render options" },
                { "playback.structure_mismatch", "SkyHook layout mismatch — cannot replay" },
                { "render.err.no_replay", "No replay file available" },
                { "render.err.need_level", "Enter a level first to start offline render" },
                { "replay.err.not_replay", "Not a SpectreReplay replay file" },
                { "replay.err.version", "Replay file version {0} is newer than supported {1}" },
                { "render.state.live", "Recording" },
                { "render.state.offline", "Offline render" },
                { "render.state.finalizing", "Finalizing" },
                { "render.state.done", "Done" },
                { "render.state.failed", "Failed" },
                { "render.state.cancelled", "Cancelled" },
            };

        internal static string Cat(string category) => T("cat." + category);

        /// <summary>
        /// 界面用哪种语言。<see cref="RefreshLanguage"/> 会按游戏当前语言改它。
        ///
        /// 之前这里是写死的"英文优先"——不管玩家把游戏设成什么语言，
        /// 中文玩家看到的都是英文界面。
        /// </summary>
        internal static bool UseChinese = true;

        /// <summary>
        /// 按游戏的语言设置选中文或英文。游戏把语言放在
        /// <c>RDString.language</c>（public static SystemLanguage）。
        /// 面板每次打开都调一次：玩家在设置里换语言后，模组界���跟着变。
        /// </summary>
        internal static void RefreshLanguage()
        {
            try
            {
                var l = RDString.language;
                UseChinese = l == SystemLanguage.Chinese
                    || l == SystemLanguage.ChineseSimplified
                    || l == SystemLanguage.ChineseTraditional;
            }
            catch
            {
                // 游戏还没初始化：保持中文（用户是中文用户）
                UseChinese = true;
            }
        }

        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            if (UseChinese && Zh.TryGetValue(key, out string zh))
            {
                return zh;
            }
            if (En.TryGetValue(key, out string en))
            {
                return en;
            }
            if (Zh.TryGetValue(key, out string zh2))
            {
                return zh2;      // 另一种语言漏翻时兜底，别把键名露给玩家
            }
            return key;          // 两种语言都漏了才露键名——那是真漏
        }

        /// <summary>
        /// 取文案并填 {0} 之类的占位符。
        ///
        /// 直接 string.Format：文案里有 {0} 是我们自己写的，不含用户输入，
        /// 真出了 FormatException 就退回原文——宁可显示 "{0}"，
        /// 也不能让整个界面在 OnGUI 里抛异常（UMM 会吞掉异常，
        /// 玩家的表现是"面板卡住不动"，比报错更难查）。
        /// </summary>
        internal static string F(string key, params object[] args)
        {
            string t = T(key);
            if (args == null || args.Length == 0 || t.IndexOf('{') < 0)
            {
                return t;
            }
            try
            {
                return string.Format(t, args);
            }
            catch
            {
                return t;
            }
        }

        /// <summary>
        /// 当前语言下界面会显示的全部文案，拼成一段。
        ///
        /// 用来一次性检查"字体里有没有这些字"。游戏只收录自己用到的字形，
        /// 模组写的字只要有一个不在里面，TMP 就画空白——不报错、界面上
        /// 只是那句话缺了几个字。这种毛病靠看是看不出来的。
        /// </summary>
        internal static string AllText()
        {
            var src = UseChinese ? Zh : En;
            var sb = new System.Text.StringBuilder(1024);
            sb.Append(Brand).Append(' ').Append(Hint);
            foreach (var kv in src)
            {
                sb.Append(kv.Key).Append(' ').Append(kv.Value).Append(' ');
            }
            return sb.ToString();
        }
    }
}
