using UnityEngine;
using UnityModManagerNet;
using SpectreReplay.Patching;

namespace SpectreReplay
{
    using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

    /// <summary>
    /// UM4 入口。结构照 Spectre 的 Main：Load 只做最轻的事，
    /// 真正要建对象、挂补丁的活全放 OnToggle。
    ///
    /// 为什么这么分：OnToggle 在模组被**启用**时触发，那时游戏场景已经
    /// 起来了；Load 在 UMM 的加载期触发，那时场景还没就绪。原先把
    /// GameObject 创建、Harmony 初始化、补丁挂载全塞在 Load 里，
    /// 目标方法很可能根本还没准备好——这类问题在日志里表现为
    /// "补丁没挂上"，查起来毫无头绪。
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
                mod = modEntry;
                // 配置与文案：纯数据读取，不碰场景，Load 期做是安全的。
                ConfigStore.Load();
                Ui.Loc.RefreshLanguage();

                // 界面全部交给 UMM 自带的 OnGUI。**不再自建 Canvas。**
                //
                // 之前那套自建 UI（Canvas + EventSystem + ScrollRect + TMP）
                // 踩的坑一串接一串：与游戏的 EventSystem 撞车、滚动条归位时机、
                // raycast 标志、裁剪、字体回退……每一个失败方式都长得像
                // "界面坏了"。UMM 把画布、输入派发、字体、布局全管了，
                // 这整类问题直接不存在。
                //
                // OnGUI 只在玩家打开 UMM 窗口并选中本模组时才会被调用，
                // 所以"什么时候显示"由 UMM 决定，不需要自己管可见性。
                modEntry.OnGUI = Ui.OnGui.OnGUI;
                modEntry.OnShowGUI = Ui.OnGui.OnShow;
                modEntry.OnHideGUI = Ui.OnGui.OnHide;
                modEntry.OnSaveGUI = Ui.OnGui.OnHide;

                // 装配在 OnToggle 里做，不在 Load 里做。Spectre 也是这么分的：
                // Load 只读配置、设回调，重活（Harmony 初始化、补丁挂载、
                // 建 GameObject）全在模组被启用、游戏场景就绪之后。
                //
                // 差别不是风格问题：**Load 跑在 UMM 的加载期，那时场景还没
                // 就绪**，补丁目标可能找不到、组件可能建在错误的时机上。
                // 收摊挂在 OnToggle(false) 上：UMM 没有 OnUnload 这个回调，
                // Spectre 也是这么做的（onToggle 里判 isToggled）。
                modEntry.OnToggle = OnToggle;
            }
            catch (System.Exception ex)
            {
                // UMM 只会记一行警告就继续，模组看起来就是"什么都没发生"。
                // 所以异常必须在这里说清楚是哪一类。
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

        /// <summary>
        /// 模组被启用时做真正的装配。与 Spectre 同一个位置。
        /// </summary>
        private static bool OnToggle(ModEntry modEntry, bool isToggled)
        {
            if (isToggled)
            {
                try
                {
                    Boot(isToggled);
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError("[SpectreReplay] 启用失败："
                        + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                    return false;
                }
                return true;
            }
            Shutdown();
            return true;
        }

        private static void Boot(bool isToggled)
        {
            // 常驻对象只挂配置落盘。**不再有面板宿主**——界面全部交给
            // UMM 的 OnGUI，不需要自己建 Canvas、不需要轮询热键。
            //
            // 配置落盘也必须挂上去。ConfigStore.Save() 只把配置标脏，
            // 真正写文件靠 ConfigFlusher 每帧末尾的 Flush()——**它原来
            // 从来没被 AddComponent 过**，于是 Save 标了脏却没人写盘，
            // 用户在界面里改的每个设置重开游戏就丢了。表现是"点得动、
            // 界面也变、就是不生效"。
            var go = new GameObject("SpectreReplayHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ConfigFlusher>();

            // 功能门控：设置页改开关后由 Settings.Commit 触发 PatchLoader.Refresh
            FeatureGate.Declare("recording", () => Settings.RecordingOn);
            FeatureGate.Declare("render", () => Settings.RenderOn);
            // 回放泵只在真的载入回放时才有意义；常驻挂着会每帧做一次
            // songposition 读取与游标比较（虽廉价，但没理由付）。
            FeatureGate.Declare("playback", () => Features.Playback.PlaybackEngine.IsPlaying);
            PatchLoader.Register();

            // 挂失败的补丁除了写日志，还要在界面上说一次。
            // Spectre 同样这么做（ShowApplyFailureSummary），它的注释写得很
            // 直白：挂不上的补丁 = 对应功能静默消失，用户只会觉得"设置没反应"，
            // 原因却只在日志里。
            //
            // 通过 UI 的钩子注入，而不是让 Ui 直接引 PatchLoader：离线预览器
            // 不链接补丁加载器（那要 Harmony 与游戏程序集），引了那边编不过。
            

            // 进关检测常驻（不是补丁，是状态轮询），
            // 否则首次进入关卡这条路径在不同版本里没有稳定的钩子。
            Features.Recording.LevelWatcher.Install();

            // 补丁明细和"5/6"这个计数打在同一行：分开打的话，计数在
            // Load 里、失败原因在 Refresh 里（早了一行），两个窗口里
            // 看到的时间顺序是反的，排查时会对不上。缺哪个补丁、为什么缺，
            // 必须一眼能看完——否则只能靠猜。
            // 界面不再有热键：UMM 窗口由 UMM 自己开（默认 F7），
            // 模组选中即显示。摘要里也就不必再报一个不存在的键。
            mod.Logger.Log("[SpectreReplay] 已加载：补丁 "
                + PatchLoader.AppliedCount + "/" + PatchLoader.DeclaredCount
                + PatchLoader.FailureReport);
            IsReady = true;
        }

        /// <summary>
        /// 模组被关闭时收摊。
        ///
        /// 不用关面板：UMM 的 OnGUI 是自己管的，画布、窗口、输入派发
        /// 全归 UMM。原先那句 PanelHost.Close() 是自建面板时代的残留——
        /// 面板没了，它自然也没了。
        ///
        /// 补丁必须卸：PatchLoader 持有 Harmony 实例的引用，
        /// OnToggle(false) 给的 ModEntry 里拿不到。
        /// </summary>
        private static bool Shutdown()
        {
            PatchLoader.UnpatchAll();
            IsReady = false;
            return true;
        }
    }
}