using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SpectreReplay
{
    /// <summary>
    /// 全部可配置项。静态字段 + 一次性读写，形态与常见的 UMM 模组一致，
    /// 好处是页面代码可以直接写 <c>() => Settings.AutoSave</c> 不用管理生命周期。
    /// 代价是没法做"改一个值只通知一处"，所以每个 setter 走
    /// <see cref="ConfigStore"/> 的 Save() 统一落盘。
    /// </summary>
    internal static class Settings
    {
        // ── 总开关 ────────────────────────────────────────────
        /// <summary>回放数据录制（记录按键，供回放与渲染使用）。</summary>
        internal static bool RecordingOn = true;

        /// <summary>视频渲染总开关。</summary>
        internal static bool RenderOn = true;

        // ── 录制 ──────────────────────────────────────────────
        /// <summary>打完/中断后自动保存回放文件。</summary>
        internal static bool AutoSave = true;

        /// <summary>用 SkyHook 拿高精度输入（比 Unity 的 Update 轮询准得多）。</summary>
        internal static bool AsyncInput = true;

        // ── 渲染模式 ──────────────────────────────────────────
        internal enum RenderMode
        {
            /// <summary>边玩边录：进关自动开、结算自动收，不用重跑。</summary>
            Live = 0,
            /// <summary>手动离线渲染：重跑一遍回放逐帧捕获。</summary>
            Offline = 1,
        }

        internal static RenderMode Mode = RenderMode.Live;

        // ── 渲染参数 ──────────────────────────────────────────
        /// <summary>画面来源：0 纯净相机（无界面） 1 整屏（含界面）。</summary>
        internal static int Surface = 0;

        internal static int Width = 1920;
        internal static int Height = 1080;
        internal static int Fps = 60;

        /// <summary>编码器：0 自动 1 最快 2 质量 3 纯 CPU。</summary>
        internal static int Encoder = 0;

        /// <summary>画质预设：0 低 1 标准 2 高 3 极致。</summary>
        internal static int Quality = 1;

        /// <summary>输出容器：0 mp4 1 mkv 2 webm。</summary>
        internal static int Container = 0;

        /// <summary>结束后再多录几秒（收尾用）。</summary>
        internal static float TailSeconds = 3f;

        /// <summary>输出目录，空 = Mods\SpectreReplay\Videos\。</summary>
        internal static string OutputDir = "";

        /// <summary>跟不上目标帧率时：true 复制上一帧（流畅），false 丢帧（忠实）。</summary>
        internal static bool DuplicateWhenLate = true;

        // ── 界面 ──────────────────────────────────────────────
        // 原先这里有三项：热键、字号缩放、进入关卡自动开界面。
        // **换成 UMM 自带的 OnGUI 之后全都不起作用了**：UMM 窗口由 UMM 自己
        // 管（默认 F7），字号由 UMM 的皮肤决定，也没有"自动打开"这回事。
        //
        // 留着比删掉更糟——三个滑条摆在那里，拖了纹丝不动，玩家只会
        // 得出"这个模组的设置全是坏的"。设置项必须和实际行为一一对应。
        //
        // 已经在用的旧 Configs.json 里还留着这三个键：读取时忽略未知键
        // （见 Apply），下次保存就不写了，无需迁移。

        // ── 路径 ──────────────────────────────────────────────
        /// <summary>模组根目录（dll 所在处）。UM4 把 dll 放在 Mods\&lt;名&gt;\ 下。</summary>
        internal static string RootDir
        {
            get
            {
                string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(loc))
                {
                    loc = AppDomain.CurrentDomain.BaseDirectory;
                }
                return Path.GetDirectoryName(loc);
            }
        }

        internal static string ReplayDir =>
            string.IsNullOrEmpty(OutputDir)
                ? Path.Combine(RootDir ?? ".", "Replays")
                : Path.Combine(RootDir ?? ".", "Replays");

        internal static string VideoDir =>
            string.IsNullOrEmpty(OutputDir)
                ? Path.Combine(RootDir ?? ".", "Videos")
                : OutputDir;

        internal static string FfmpegPath
        {
            get
            {
                string own = Path.Combine(RootDir ?? ".", "ffmpeg.exe");
                return File.Exists(own) ? own : "ffmpeg";
            }
        }

        /// <summary>改完任一项后调用：落盘 + 通知补丁层重挂。</summary>
        internal static void Commit()
        {
            // 原先这里设 Ui.Theme.TextScale，让自建界面跟着放大字号。
            // 换成 UMM 自带的 OnGUI 之后，全局字号由 UMM 的皮肤决定，
            // 这个设置不再有任何作用——留着会让"改了没反应"。
            ConfigStore.Save();
            Patching.PatchLoader.Refresh();
        }
    }

    /// <summary>
    /// 配置文件读写。
    ///
    /// 用"缺字段补默认值"而不是"整体覆盖"： Newtonsoft 缺字段时会填 0/false，
    /// 直接反序列化会让新增的开关变成 false 而不是我们声明的初始值。
    /// 所以先反序列化到字典，缺键补上，再写回对象。
    /// </summary>
    internal static class ConfigStore
    {
        private static string _path;
        private static bool _dirty;
        private static bool _loaded;

        internal static string FilePath => _path ??= Path.Combine(
            Settings.RootDir ?? ".", "Configs.json");

        internal static void Load()
        {
            _loaded = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    if (dict != null)
                    {
                        Apply(dict);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 读取配置失败，改用默认值：" + ex.Message);
            }
            }

        private static void Apply(Dictionary<string, object> d)
        {
            Settings.RecordingOn = GetBool(d, "RecordingOn", Settings.RecordingOn);
            Settings.RenderOn = GetBool(d, "RenderOn", Settings.RenderOn);
            Settings.AutoSave = GetBool(d, "AutoSave", Settings.AutoSave);
            Settings.AsyncInput = GetBool(d, "AsyncInput", Settings.AsyncInput);
            Settings.Mode = (Settings.RenderMode)GetInt(d, "Mode", (int)Settings.Mode);
            Settings.Surface = GetInt(d, "Surface", Settings.Surface);
            Settings.Width = GetInt(d, "Width", Settings.Width);
            Settings.Height = GetInt(d, "Height", Settings.Height);
            Settings.Fps = GetInt(d, "Fps", Settings.Fps);
            Settings.Encoder = GetInt(d, "Encoder", Settings.Encoder);
            Settings.Quality = GetInt(d, "Quality", Settings.Quality);
            Settings.Container = GetInt(d, "Container", Settings.Container);
            Settings.TailSeconds = GetFloat(d, "TailSeconds", Settings.TailSeconds);
            Settings.OutputDir = GetString(d, "OutputDir", Settings.OutputDir);
            Settings.DuplicateWhenLate = GetBool(d, "DuplicateWhenLate", Settings.DuplicateWhenLate);
        }

        private static bool GetBool(Dictionary<string, object> d, string k, bool def)
            => d.TryGetValue(k, out object v) && v is bool b ? b : def;

        private static int GetInt(Dictionary<string, object> d, string k, int def)
            => d.TryGetValue(k, out object v) && v is long l ? (int)l : def;

        private static float GetFloat(Dictionary<string, object> d, string k, float def)
            => d.TryGetValue(k, out object v) && v is double dd ? (float)dd : def;

        private static string GetString(Dictionary<string, object> d, string k, string def)
            => d.TryGetValue(k, out object v) && v is string s ? s : def;

        /// <summary>标脏后延迟落盘：连续拖动滑条不会每帧写盘。</summary>
        internal static void Save()
        {
            if (!_loaded)
            {
                return;
            }
            _dirty = true;
        }

        internal static void Flush()
        {
            if (!_loaded || !_dirty)
            {
                return;
            }
            _dirty = false;
            try
            {
                var d = new Dictionary<string, object>
                {
                    { "RecordingOn", Settings.RecordingOn },
                    { "RenderOn", Settings.RenderOn },
                    { "AutoSave", Settings.AutoSave },
                    { "AsyncInput", Settings.AsyncInput },
                    { "Mode", (int)Settings.Mode },
                    { "Surface", Settings.Surface },
                    { "Width", Settings.Width },
                    { "Height", Settings.Height },
                    { "Fps", Settings.Fps },
                    { "Encoder", Settings.Encoder },
                    { "Quality", Settings.Quality },
                    { "Container", Settings.Container },
                    { "TailSeconds", Settings.TailSeconds },
                    { "OutputDir", Settings.OutputDir },
                    { "DuplicateWhenLate", Settings.DuplicateWhenLate },
                };
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(d, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 写入配置失败：" + ex.Message);
            }
        }
    }

    /// <summary>把标脏的配置落盘。挂在宿主上，没有它就永远不写。</summary>
    internal sealed class ConfigFlusher : MonoBehaviour
    {
        private void LateUpdate() => ConfigStore.Flush();

        /// <summary>
        /// 退出与销毁时再兜一次底。
        ///
        /// 只靠 LateUpdate 的话，最后一次改动可能落在"改完立刻关游戏"的
        /// 那个窗口里没写出去。OnApplicationQuit 在关卡切换/宿主被销毁时
        /// 不触发，所以 OnDestroy 也要来一遍——重复落盘是幂等的。
        /// </summary>
        private void OnApplicationQuit() => ConfigStore.Flush();

        private void OnDestroy() => ConfigStore.Flush();
    }
}
