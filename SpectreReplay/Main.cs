using UnityEngine;
using UnityModManagerNet;
using SpectreReplay.Patching;

namespace SpectreReplay
{
    using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

    /// <summary>
    /// UM4 入口。只做三件事：探测环境、装配 UI 宿主、注册补丁。
    /// 真正的功能代码都在 Features\ 下，不往这里堆。
    /// </summary>
    public static class Main
    {
        public static ModEntry mod;
        public static bool IsReady { get; private set; }

        public static void Load(ModEntry modEntry)
        {
            mod = modEntry;
            ConfigStore.Load();
            Ui.PanelHost.ApplyHotkey(Settings.Hotkey);
            // 常驻输入轮询：热键开关面板、ESC 关闭、驱动会变文字
            var go = new GameObject("SpectreReplayHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Ui.PanelHostBehaviour>();
            // 配置落盘也必须挂上去。ConfigStore.Save() 只把配置标脏，
            // 真正写文件靠 ConfigFlusher 每帧末尾的 Flush()——**它原来
            // 从来没被 AddComponent 过**，于是 Save 标了脏却没人写盘，
            // 用户在界面里改的每个设置重开游戏就丢了。表现是"点得动、
            // 界面也变、就是不生效"。
            go.AddComponent<ConfigFlusher>();
            Pages.Pages.RegisterAll();

            // 功能门控：设置页改开关后由 Settings.Commit 触发 PatchLoader.Refresh
            Patching.FeatureGate.Declare("recording", () => Settings.RecordingOn);
            Patching.FeatureGate.Declare("render", () => Settings.RenderOn);
            // 回放泵只在真的载入回放时才有意义；常驻挂着会每帧做一次
            // songposition 读取与游标比较（虽廉价，但没理由付）。
            Patching.FeatureGate.Declare("playback", () => Features.Playback.PlaybackEngine.IsPlaying);
            Patching.PatchLoader.Register();

            // 进关检测常驻（不是补丁，是状态轮询），
            // 否则首次进入关卡这条路径在不同版本里没有稳定的钩子。
            Features.Recording.LevelWatcher.Install();

            mod.Logger.Log("[SpectreReplay] 已加载：补丁 "
                + Patching.PatchLoader.AppliedCount + "/" + Patching.PatchLoader.DeclaredCount
                + "（界面热键 " + Pages.Pages.KeyName((KeyCode)Settings.Hotkey) + "）");
            IsReady = true;
        }
    }
}
