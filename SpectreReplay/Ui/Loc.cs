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
        /// <summary>头部副标题。**走文案表**：原先写死成中文常量，英文界面下会直接显示
        /// 中文，而 Loc.cs 在硬编码检查里整文件豁免——这条永远不会被闸门抓到。
        /// 另外那句话本身也是错的：写的是 F9，而 F9 是「手动保存回放」的键，
        /// UMM 的开关窗口热键默认是 Ctrl+F10。</summary>
        internal static string Hint => T("ui.hint");

        private static readonly System.Collections.Generic.Dictionary<string, string> Zh =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "page.record", "录制" },
                { "page.playback", "回放" },
                { "page.render", "视频渲染" },
                { "page.about", "关于" },

                { "record.autosave", "自动保存回放" },
                { "record.recording", "录制中" },
                { "record.note", "录制时按下按键即可。回放文件保存在下方「回放目录」所示的文件夹里。" },
                { "record.howto", "① 在这里设好参数（快捷键：Ctrl+F10 打开本窗口）② 进关卡正常玩，按键会被记下来 ③ 通关后自动存成回放文件 ④ 在「回放」页载入，或在「视频渲染」页出片。" },
                { "record.hotkeys", "快捷键" },
                { "record.savekey", "保存回放" },
                { "record.cancelsavekey", "取消保存" },
                { "record.completesave", "只保存完整通关" },
                { "record.dontsavewhenauto", "全自动局不保存" },
                { "record.dontsavewhenmiss", "有失误不保存" },
                { "record.latesave", "出关时再保存" },
                { "record.failsave", "失败时也保存" },
                { "record.backup", "额外存一份备份" },
                { "record.limitkey", "限制按键数" },
                { "record.manualsave", "F9 手动保存" },
                { "record.asyncinput", "从异步输入取按键" },
                { "record.asyncinput.off", "游戏里没开「异步输入」，这一项现在不生效。请在游戏设置 → 输入里打开异步输入。" },
                { "record.keybd", "键盘音" },
                { "record.keybd.record", "录制键盘音" },
                { "record.keybd.volume", "键盘音音量" },
                { "record.keybd.offset", "时间偏移" },
                { "record.keybd.note", "键盘音就是回放时按下去的“嗒嗒”声。时间偏移用于校准它整体的早晚：声音偏早就往负调，偏晚就往正调，单位毫秒。" },
                { "record.stop", "停止录制" },
                { "ui.presskey", "按下任意键…" },
                { "ui.nokey", "未绑定" },
                { "ui.patchfailed", "补丁未挂载（对应功能可能失效）" },
                { "ui.on", "开" },
                { "ui.off", "关" },
                { "ui.hint", "Ctrl+F10 开关界面 · ESC 关闭" },
                { "playback.current", "当前" },
                { "playback.idle", "还没载入回放。在下面的列表里点「回放」即可。" },
                { "playback.idle_err", "上一次的载入失败了：{0}" },
                { "playback.empty", "还没有回放文件。打一局关卡就会自动保存。" },
                { "playback.drive", "回放驱动" },
                { "playback.simulate", "模拟按键" },
                { "playback.drive.note", "关掉后回放仍然会播放、但不再往游戏里送按键——星球不会动。停止回放时也不会松开已按住的键，所以只在排查「按键冲突」时关它。" },
                { "playback.stop", "停止回放" },
                { "playback.unload", "卸载回放" },
                { "playback.note", "回放由录制时的按键序列驱动，可用于复盘与练习。" },
                { "playback.ready", "已装好：{0}——重新开始这关就会自动播放。" },
                { "playback.armed", "正在播放：{0}" },
                { "playback.restart", "重开这关并播放" },
                { "playback.cancel", "取消待播" },
                { "playback.timing", "命中误差" },
                { "playback.timing_summary", "已分析 {0} 击（偏早 {1} / 偏晚 {2}），中位数 {3}" },
                { "playback.timing_detail", "均值 {0}，标准差 {1}，范围 {2} ~ {3}" },
                { "playback.timing_skipped", "另有 {0} 击未计入（自动命中或取不到谱面数据）。" },
                { "playback.openlevel", "打开这一关" },
                { "playback.openlevel.editor", "打开编辑器" },
                { "playback.openlevel.official", "该回放没有记录内部关卡名（旧版本录制），无法直达：" },
                { "playback.openlevel.official_go", "官方关卡「{0}」，可直接打开。" },
                { "playback.openlevel.official.go", "进入官方关卡" },
                { "playback.openlevel.missing", "谱面文件不在了：" },
                { "playback.openlevel.busy", "正在跳转，请稍候。" },
                { "playback.analyze.hint", "把每一击的偏差画成柱状图：绿=Perfect 窗口内，黄=Pure 窗口内，红=超出。" },
                { "playback.analyze.none", "没有可画的命中数据。" },
                { "playback.err.no_data", "没有可校验的回放数据。" },
                { "playback.err.wrong_level", "关卡不匹配：这份回放录自「{0}」。请打开那一关。" },
                { "playback.err.floor_changed", "谱面已被修改：这份回放是在旧版谱面上录的，按键位置会全部错位。" },
                { "playback.err.chart_mismatch", "谱面参数不匹配（移调或 BPM 变了）：「{0}」。" },
                { "playback.err.wrong_difficulty", "难度不匹配：这份回放录于「{0}」。请切换难度后重开。" },
                { "playback.err.wrong_tile", "起点不匹配：这份回放从第 {0} 块开始，请先退到那个位置。" },
                { "playback.err.wrong_speed", "倍速不匹配：这份回放录于 {0} 倍速。" },
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
                { "render.surface.screen_note", "包含进度条等界面 UI；分辨率跟随游戏窗口，不可超分辨率；渲染时请收起模组窗口，否则会被录入画面。" },
                { "render.surface.clean_note", "不含任何界面元素，输出分辨率任意（可超采样）；适合做成纯画面展示或二次剪辑素材。" },
                { "render.clock.experimental", "确定性渲染时钟（实验性）" },
                { "render.clock.hint", "打开后把游戏读到的时间源换成「帧号驱动」的虚拟时钟，游戏自己的节拍推进、相机事件、倒计时、按键音调度全部自动确定性化，成片帧率恒定、不受机器繁忙影响。关闭则走音频游标兜底：画面仍能出片，但时间轴由音频游标驱动，可能有毫秒级抖动，且视频背景不再随渲染速率调速。注意：它改的是游戏时间源本身，影响面较大；与其他视频渲染类模组同时启用时会争抢同一个补丁点（先加载者生效），此时本项会自动让位。若渲染报错或音画不同步，请先关掉这一项再试。" },
                { "render.ffmpeg", "ffmpeg" },
                { "render.ffmpeg.ready", "ffmpeg 已就绪" },
                { "render.ffmpeg.missing", "未找到 ffmpeg.exe，请放到模组目录或游戏根目录，或加入系统 PATH" },
                { "render.codec", "编码器" },
                { "render.codec.current", "实际编码器" },
                { "render.quality", "画质" },
                { "render.format", "输出格式" },
                { "render.tail", "结尾多录秒数" },
                { "render.outdir", "输出目录" },
                { "render.outdir.empty", "留空 = 模组目录下的 RenderVideos" },
                { "convert.title", "转换已有视频" },
                { "convert.hint", "把上面输出目录里的视频转成所选格式。已在目标格式的文件会跳过；mp4/mov 之间通常秒级完成，转 webm 需要重编码（较慢）。" },
                { "convert.button", "开始转换" },
                { "convert.target", "目标格式" },
                { "convert.running", "正在转换 {0}/{1}（已转 {2}，跳过 {3}，失败 {4}）" },
                { "convert.done", "转换完成：{0} 个已转，{1} 个跳过，{2} 个失败。" },
                { "convert.err.no_ffmpeg", "找不到 ffmpeg，无法转换。" },
                { "convert.err.no_dir", "输出目录不存在或还没有视频。" },
                { "render.start", "开始渲染" },
                { "render.cancel", "取消" },
                { "render.note.live", "边玩边录：进入关卡自动开始，结算后自动完成。不需要再跑一遍关卡。" },
                { "render.note.offline", "离线渲染：重跑一遍回放逐帧捕获，耗时更长但画面与原回放完全一致。" },
                { "render.offline_target", "将渲染这份回放：{0}" },
                { "render.offline_noreplay", "没有可用的回放文件：先打一局关卡（会自动保存），或到「回放」页确认目录里有 .srp。" },
                { "about.note", "SpectreReplay 是独立模组，不依赖 Spectre，两者可同时启用。" },
                { "about.version", "版本" },
                { "about.path", "保存目录" },
                { "about.engine", "回放引擎" },
                { "about.engine.value", "Spectre 回放（录制 / 回放 / 视频渲染）" },
                { "record.savedir", "回放目录" },
                { "record.changedir", "更改…" },
                { "record.savedir.pick", "选择回放保存目录" },
                { "record.savedir.busy", "录制中不能改保存目录：本局键盘音写在旧目录下，换了就找不回。先停止录制。" },
                { "render.outdir.pick", "选择渲染输出目录" },
                { "render.active", "渲染中" },
                { "render.eta", "预计剩余 {0}" },
                { "render.progress", "进度" },

                // ── 状态提示 ──────────────────────────────────────
                // 之前一个都没有：录制开始了没人知道、回放载入了没人知道、
                // 渲染出片了没人知道。整个模组于是"像没生效"。
                // Spectre 有 TriggerMessage 这一套（见它的 SpectreState.cs），
                // 靠它自己场景里的 Notification 预制体——我们不能依赖那个资源，
                // 改在自己的设置页顶部常驻一行，见 OnGui.DrawStatus。
                { "status.idle", "空闲：没在录制，也没在渲染。" },
                { "status.all_off", "录制与渲染都已关闭：进关也不会记录任何东西。" },
                { "status.recording", "正在录制本局按键…" },
                { "status.playing", "回放进行中…" },
                // 顶栏用的是这条：它没有 {0}，顶栏不该去拼一个文件名出来。
                { "status.pending_short", "回放已装好，重开关卡就会播放。" },
                { "status.pending_replay", "回放已装好：{0}。重新开始这关（或从关卡列表再进一次）就会自动播放。" },
                { "status.rendering", "正在渲染…{0}" },
                { "status.saved", "回放已保存：{0}" },
                { "status.saved_none", "本局没有录到按键，未保存（这是正常的，空关不会产生回放）。" },
                { "status.loaded", "已载入回放：{0}" },
                { "status.loaded_none", "没有可载入的回放文件。" },
                { "status.stopped", "回放已停止。" },
                { "status.render_done", "渲染完成：{0}" },
                { "status.render_start", "开始渲染…" },

                { "render.duplicate_when_late", "跟不上时复制上一帧" },
                { "playback.files", "回放文件" },
                { "playback.replay", "回放" },
                { "playback.loaded", "已载入" },
                { "render.on", "启用视频渲染" },
                { "render.enable", "视频渲染" },
                { "render.status", "状态" },
                { "render.err.ffmpeg_start", "无法启动 ffmpeg：{0}" },
                { "render.err.ffmpeg_timeout", "ffmpeg 超时未退出" },
                { "render.err.encoder_exit", "编码器未正常退出" },
                { "render.err.encoder_gone", "编码器进程已退出" },
                { "render.err.audio_start", "无法启动混音采集（{0}）" },
                { "render.err.audio_count", "读取混音样本数失败（{0}）" },
                { "render.err.audio_render", "混音渲染失败（{0}）" },
                { "render.err.audio_silent", "混音持续无输出，已重启 {0} 次仍无效" },
                { "render.err.audio_seal", "音频文件封口失败（{0}）" },
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
                { "page.record", "Recording" },
                { "page.playback", "Playback" },
                { "page.render", "Video Render" },
                { "page.about", "About" },

                { "record.autosave", "Auto-save replays" },
                { "record.recording", "Recording" },
                { "record.note", "Keys pressed while playing are captured. Replays are saved in the folder shown under \"Replay folder\" below." },
                { "record.howto", "1) Set options here (press Ctrl+F10 to open this window) 2) Play a level normally - your keys are recorded 3) Finishing saves a replay file 4) Load it on the Playback tab, or render it on the Video Render tab." },
                { "record.hotkeys", "Hotkeys" },
                { "record.savekey", "Save replay" },
                { "record.cancelsavekey", "Cancel save" },
                { "record.completesave", "Only save full clears" },
                { "record.dontsavewhenauto", "Skip auto-played levels" },
                { "record.dontsavewhenmiss", "Skip runs with a miss" },
                { "record.latesave", "Save when leaving the level" },
                { "record.failsave", "Also save on failure" },
                { "record.backup", "Keep an extra backup copy" },
                { "record.limitkey", "Limit key count" },
                { "record.manualsave", "F9 manual save" },
                { "record.asyncinput", "Read keys from async input" },
                { "record.asyncinput.off", "Async input is off in the game, so this option has no effect. Enable async input in the game's settings → input." },
                { "record.keybd", "Keyboard sound" },
                { "record.keybd.record", "Record keyboard sound" },
                { "record.keybd.volume", "Keyboard sound volume" },
                { "record.keybd.offset", "Time offset" },
                { "record.keybd.note", "Keyboard sound is the tap played back when a key was pressed. Time offset calibrates it as a whole: shift negative if it is early, positive if late, in milliseconds." },
                { "record.stop", "Stop recording" },
                { "ui.presskey", "Press any key..." },
                { "ui.nokey", "Not bound" },
                { "ui.patchfailed", "Some patches failed to apply (features may be off)" },
                { "ui.on", "On" },
                { "ui.off", "Off" },
                { "ui.hint", "Ctrl+F10 opens this panel · ESC closes" },
                { "playback.current", "Current" },
                { "playback.idle", "No replay loaded. Press \"Replay\" on a file below." },
                { "playback.idle_err", "The last load failed: {0}" },
                { "playback.empty", "No replays yet. Play a level and one is saved automatically." },
                { "playback.drive", "Playback driver" },
                { "playback.simulate", "Simulate key presses" },
                { "playback.drive.note", "With this off, playback still runs but no keys are sent to the game, so the planet will not move. Held keys are also not released when playback stops, so keep it on unless you are debugging a key conflict." },
                { "playback.stop", "Stop" },
                { "playback.unload", "Unload replay" },
                { "playback.note", "Playback is driven by the recorded key stream." },
                { "playback.ready", "Ready: {0} — restart this level to play it automatically." },
                { "playback.armed", "Playing: {0}" },
                { "playback.restart", "Restart & play" },
                { "playback.cancel", "Cancel" },
                { "playback.timing", "Hit timing" },
                { "playback.timing_summary", "Analyzed {0} hits (early {1} / late {2}), median {3}" },
                { "playback.timing_detail", "Mean {0}, sigma {1}, range {2} ~ {3}" },
                { "playback.timing_skipped", "{0} more hits not counted (auto hits or missing chart data)." },
                { "playback.openlevel", "Open this level" },
                { "playback.openlevel.editor", "Open editor" },
                { "playback.openlevel.official", "This replay has no internal level name recorded (saved by an older version); cannot jump: " },
                { "playback.openlevel.official_go", "Official level \"{0}\" — can be opened directly." },
                { "playback.openlevel.official.go", "Enter official level" },
                { "playback.openlevel.missing", "Chart file is gone: " },
                { "playback.openlevel.busy", "Switching, please wait." },
                { "playback.analyze.hint", "Draws each hit's offset as a bar: green = inside Perfect window, yellow = inside Pure, red = outside." },
                { "playback.analyze.none", "No hit data to plot." },
                { "playback.err.no_data", "No replay data to validate." },
                { "playback.err.wrong_level", "Wrong level: this replay was recorded on \"{0}\". Open that one." },
                { "playback.err.floor_changed", "The chart has changed since this replay was recorded; every key would land on the wrong tile." },
                { "playback.err.chart_mismatch", "Chart parameters differ (pitch or BPM changed): \"{0}\"." },
                { "playback.err.wrong_difficulty", "Wrong difficulty: this replay was recorded on \"{0}\". Switch difficulty and restart." },
                { "playback.err.wrong_tile", "Wrong start tile: this replay starts at tile {0}; rewind there first." },
                { "playback.err.wrong_speed", "Wrong speed: this replay was recorded at {0}x." },
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
                { "render.surface.screen_note", "Includes screen-space UI such as the progress bar; resolution follows the game window (no supersampling); collapse the mod window while rendering or it will be captured." },
                { "render.surface.clean_note", "No UI at all, any output resolution (supersampling allowed); good for pure footage or editing material." },
                { "render.clock.experimental", "Deterministic render clock (experimental)" },
                { "render.clock.hint", "Replaces the time source the game reads with a frame-index-driven virtual clock, making beat advancement, camera events, countdowns and keysound scheduling deterministic — output framerate stays constant regardless of machine load. Off = audio-cursor fallback: rendering still works, but the timeline is driven by the audio cursor (millisecond-level jitter) and video backgrounds are no longer speed-matched to the render rate. Note: this alters the game's own time source, so its blast radius is wider than other options; when another video-rendering mod is enabled at the same time, both contend for the same patch point (first to load wins) and this option yields automatically. If rendering errors or audio/video drift appears, turn this off first." },
                { "render.ffmpeg", "ffmpeg" },
                { "render.ffmpeg.ready", "ffmpeg ready" },
                { "render.ffmpeg.missing", "ffmpeg.exe not found; place it in the mod folder or game root, or add it to PATH" },
                { "render.codec", "Encoder" },
                { "render.codec.current", "Actual encoder" },
                { "render.quality", "Quality" },
                { "render.format", "Output format" },
                { "render.tail", "Extra tail seconds" },
                { "render.outdir", "Output folder" },
                { "render.outdir.empty", "Empty = RenderVideos under the mod folder" },
                { "convert.title", "Convert existing videos" },
                { "convert.hint", "Converts videos in the output folder above to the selected format. Files already in the target format are skipped; mp4/mov swaps are usually instant, while webm requires re-encoding (slower)." },
                { "convert.button", "Start converting" },
                { "convert.target", "Target format" },
                { "convert.running", "Converting {0}/{1} (done {2}, skipped {3}, failed {4})" },
                { "convert.done", "Conversion finished: {0} converted, {1} skipped, {2} failed." },
                { "convert.err.no_ffmpeg", "ffmpeg not found; cannot convert." },
                { "convert.err.no_dir", "Output folder does not exist or has no videos yet." },
                { "render.start", "Render" },
                { "render.cancel", "Cancel" },
                { "render.progress", "Progress" },
                { "render.note.live", "Record-while-playing: starts automatically when you enter a level and finishes on results. The level is not replayed." },
                { "render.note.offline", "Offline: replays the level frame by frame. Slower, but pixel-identical to the replay." },
                { "render.offline_target", "Will render this replay: {0}" },
                { "render.offline_noreplay", "No replay file to render. Play a level first (it saves automatically), or check the Playback tab for .srp files." },
                { "about.note", "SpectreReplay is standalone and does not require Spectre; both can be enabled at once." },
                { "about.version", "Version" },
                { "about.path", "Save folder" },
                { "about.engine", "Replay engine" },
                { "about.engine.value", "Spectre replay (record / playback / video render)" },
                { "record.savedir", "Replay folder" },
                { "record.changedir", "Change..." },
                { "record.savedir.pick", "Choose replay folder" },
                { "record.savedir.busy", "Cannot change the folder while recording: this run's keyboard sound is being written to the old folder. Stop recording first." },
                { "render.outdir.pick", "Choose render output folder" },
                { "render.active", "Rendering" },
                { "render.eta", "About {0} left" },

                { "status.idle", "Idle: not recording, not rendering." },
                { "status.all_off", "Recording and rendering are both off: playing a level records nothing." },
                { "status.recording", "Recording your keys..." },
                { "status.playing", "Replay in progress..." },
                { "status.pending_short", "Replay is loaded. Restart the level to play it." },
                { "status.pending_replay", "Replay loaded: {0}. Restart this level (or re-enter it from the level list) to play it automatically." },
                { "status.rendering", "Rendering... {0}" },
                { "status.saved", "Replay saved: {0}" },
                { "status.saved_none", "No keys were recorded this run, so nothing was saved (normal for an empty level)." },
                { "status.loaded", "Replay loaded: {0}" },
                { "status.loaded_none", "No replay file available." },
                { "status.stopped", "Replay stopped." },
                { "status.render_done", "Render finished: {0}" },
                { "status.render_start", "Rendering started..." },

                { "render.duplicate_when_late", "Duplicate last frame when behind" },
                { "playback.files", "Replays" },
                { "playback.replay", "Replay" },
                { "playback.loaded", "Loaded" },
                { "render.on", "Enable video render" },
                { "render.enable", "Video render" },
                { "render.status", "Status" },
                { "render.err.ffmpeg_start", "Cannot start ffmpeg: {0}" },
                { "render.err.ffmpeg_timeout", "ffmpeg timed out and did not exit" },
                { "render.err.encoder_exit", "Encoder did not exit cleanly" },
                { "render.err.encoder_gone", "Encoder process has exited" },
                { "render.err.audio_start", "Cannot start mixdown capture ({0})" },
                { "render.err.audio_count", "Cannot read mixdown sample count ({0})" },
                { "render.err.audio_render", "Mixdown render failed ({0})" },
                { "render.err.audio_silent", "Mixdown silent after {0} restarts" },
                { "render.err.audio_seal", "Cannot finalise the audio file ({0})" },
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
        /// 面板每次打开都调一次：玩家在设置里换语言后，模组界面跟着变。
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
    }
}
