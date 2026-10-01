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
            // 入口第一件事就是记一行。这不是多余的日志：
            //
            // **UMM 加载期抛异常时只会记一行警告就继续**，模组在现象上
            // 完全等同于"什么都没发生"——没有 [SpectreReplay] 任何一行，
            // 界面也不会出现。以前把"零行"当成"面板还没开"，其实也可能是
            // Load 在第一行之后立刻就炸了。两种情况长得很像，日志能分开。
            UnityEngine.Debug.Log("[SpectreReplay] Load() 进入");
            try
            {
                Load2(modEntry);
            }
            catch (System.Exception ex)
            {
                // 到这里才算真加载了，所以 mod 可能还没建好——
                // 两边都记，别让"记录失败"本身把异常吞掉。
                UnityEngine.Debug.LogError("[SpectreReplay] 加载失败："
                    + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                try
                {
                    modEntry?.Logger?.Log("[SpectreReplay] 加载失败：" + ex);
                }
                catch
                {
                    // 记录也失败就算了，至少上面那条已经出去了
                }
                throw;
            }
        }

        private static void Load2(ModEntry modEntry)
        {
            mod = modEntry;
            ConfigStore.Load();
            // 语言要先定下来：顶栏提示、热键名等都是按当前语言现生成的。
            // 放在 ApplyHotkey 之前，顺序反了就会按默认语言生成。
            Ui.Loc.RefreshLanguage();
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

            // 补丁明细和"5/6"这个计数打在同一行：分开打的话，计数在
            // Load 里、失败原因在 Refresh 里（早了一行），两个窗口里
            // 看到的时间顺序是反的，排查时会对不上。缺哪个补丁、为什么缺，
            // 必须一眼能看完——否则只能靠猜。
            mod.Logger.Log("[SpectreReplay] 已加载：补丁 "
                + Patching.PatchLoader.AppliedCount + "/" + Patching.PatchLoader.DeclaredCount
                + "（界面热键 " + Pages.Pages.KeyName((KeyCode)Settings.Hotkey) + "）"
                + Patching.PatchLoader.FailureSummary);
            IsReady = true;
        }
    }
}
