using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using SpectreReplay.PatchShared;
using SpectreReplay.Features.Replay;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay
{
    /// <summary>
    /// 入口：装配录制/回放/视频渲染三大功能族的补丁与界面回调。
    /// Load 只做最轻的事（配置/文案/界面回调），重活全在 OnToggle。
    /// </summary>
    public static class Main
    {
        public static ModEntry mod;

        public static void Load(ModEntry modEntry)
        {
            UnityEngine.Debug.Log("[SpectreReplay] Load() 进入");
            try
            {
                mod = modEntry;
                ConfigManager.LoadConfigs(Path.Combine(modEntry.Path, "Configs.json"));
                Settings.check(modEntry.Path);
                WavLoader.reset();

                modEntry.OnGUI = Ui.OnGui.OnGUI;
                modEntry.OnShowGUI = Ui.OnGui.OnShow;
                modEntry.OnHideGUI = Ui.OnGui.OnHide;
                modEntry.OnSaveGUI = Ui.OnGui.OnSave;
                // 退出时补上拖动滑条期间被推迟的落盘（见 Pages.Commit）。
                // 少了这一步，拖完滑条直接退游戏会丢掉这次改动。
                Application.quitting -= OnQuitting;
                Application.quitting += OnQuitting;
                modEntry.OnToggle = OnToggle;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[SpectreReplay] 加载失败：" + ex);
                try { modEntry?.Logger?.Log("[SpectreReplay] 加载失败：" + ex); } catch { }
                throw;
            }
        }

        /// <summary>进程退出前的最后一道落盘。只做能做的：写配置文件。</summary>
        private static void OnQuitting()
        {
            try
            {
                // **云同步必须先失能再落盘，顺序不能反。** 退出时序里游戏自己的
                // SteamClient.Shutdown()（scrController.OnApplicationQuit）跑在
                // Application.quitting 之前（编辑器 ExitPlayMode 崩溃栈实证），
                // 此时模组缓存的 RemoteStorage 句柄已经悬空；FlushConfig 会触发
                // 配置上传，拿悬空句柄调 FileWrite 是原生访问违例，try/catch
                // 拦不住，整个进程跟着崩。失能后 OnConfigSaved 见 Ready=false
                // 静默早退，本地照写；退出瞬间的写入本来也赶不上 Steam 客户端
                // 的批量上传，下次会话第一次写盘会全量补传。
                Features.Cloud.CloudReplay.PrepareForExit();
                Ui.OnGui.FlushConfig();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[SpectreReplay] 退出落盘失败：" + ex.Message);
            }
        }

        private static bool OnToggle(ModEntry modEntry, bool isToggled)
        {
            if (!isToggled)
            {
                // 关模组等于设置页没了：把拖动期间攒下的改动先写掉
                Ui.OnGui.FlushConfig();

                // **先把在跑的回放/录制/渲染收场，再拆补丁。** 关模组不是关一个开关：
                // ReplayPlayer.Stop 才是「还原难度与 noFail、把键盘交还玩家」的唯一路径。
                // 跳过它，玩家会被留在录像的判定模式里，而 CachedDifficulty 还攥着
                // 他的原值——下次装弹再 Stop 时会把泄漏值当成原值写回，玩家真正的
                // 设置就永久丢了（与「回放启动失败仍泄漏接管设置」同一类漏洞，只是
                // 入口换成了关模组）。StopRecording 同理：is_recording 翻假后没人收，
                // 这份录制再也不会被保存。CancelRender 让渲染作业自己走收尾，
                // 否则它会带着钉扎时钟继续跑，而它依赖的补丁马上就不在了。
                // 三步都自带「没在跑就早退」的守卫，空闲时关模组只是三次空调用。
                try { SpectreState.PlayActions.StopPlaying(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[SpectreReplay] 关模组时停止回放失败：" + ex.Message); }
                try { SpectreState.PlayActions.StopRecording(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[SpectreReplay] 关模组时停止录制失败：" + ex.Message); }
                try { Features.VideoRender.VideoRender.CancelRender(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[SpectreReplay] 关模组时取消渲染失败：" + ex.Message); }

                SpectreState.SetGetKeyAsyncEnabled(false);
                SpectrePatchHub.UnpatchAll();
                PatchManager.UnpatchAll();
                MainThread.Uninstall();
                AudioRecorder.StopAllMicrophones();
                return true;
            }

            try
            {
                Harmony harmony = new Harmony(modEntry.Info.Id);
                Settings.check(modEntry.Path);
                PatchManager.Initialize(harmony);
                MainThread.Install();

                SpectrePatchHub.LogTag = "SpectreReplay";
                SpectrePatchHub.Configure(typeof(ADOBase).Assembly,
                    PatchShared.GameVersion.Release, 141, 150);
                SpectrePatchHub.Initialize(typeof(Main).Assembly);
                // KeyViewer 桥接是 PatchManual 挂上去的：补丁会随 Initialize 里的
                // UnpatchAll 一并卸掉，但桥接自己的「已挂接」静态标志不会跟着翻假。
                // 不清它，关开一次模组之后键位显示器就永久失联且无日志（见 Reset 注释）。
                Features.Replay.KeyViewerBridge.Reset();
                // Steam 云探测同理：编辑器 Play 会话之间模组域不重载，上一个
                // Play 留下的接口指针指向已随 SteamClient.Shutdown 销毁的
                // Steam 上下文，不清就是拿悬空句柄开路（见 ResetProbe 注释）。
                Features.Cloud.SteamCloud.ResetProbe();

                // 回放功能开关（录制/回放/保存/录像共用这一个功能族）
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.Replay, () => Settings.ReplayOn);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayManualSave, () => Settings.ReplayOn && Settings.ManualSave);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayCancelSave, () => Settings.ReplayOn && Settings.CancelSave);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayAsyncInput, () => Settings.ReplayOn && Settings.GetDataFromAsyncInput);
                // 渲染时钟（实验性，默认关）。**必须注册**：未注册的功能其补丁
                // 按"关闭"处理且日志告警——而这条补丁正是离线渲染拿确定性时钟
                // 的来源，漏注册会表现为"渲染能用但音画偶有抖动"，很难联想到是接线。
                // 与 Replay 相乘：回放系统整个关掉时，渲染时钟也没有存在意义。
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.RenderClock,
                    () => Settings.ReplayOn && Settings.RenderClockOn);
                // 去特效。**必须注册**：未注册的功能其补丁按"关闭"处理且日志告警，
                // 而它的三个补丁分别负责「摘事件 / 禁止存盘 / 刷新按钮」——漏注册时
                // 表现为"开关拨到开、谱面一点没变"，日志里只有一行未注册告警。
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.EffectRemover,
                    () => Settings.EffectRemoverOn);
                // 特殊按键过滤。**必须注册**（同 EffectRemover 的理由）：它的两条补丁
                // 分别接管轮询输入与异步输入两条路径，未注册时按"关闭"处理——
                // 玩家勾了一堆键、回放里照样没有它们，而这个现象与"游戏本来就
                // 不录这些键"一模一样，几乎不可能从界面上追到注册漏了。
                // 谓词要「至少勾了一个键」而不是恒真：这条路径在每帧输入处理上，
                // 没勾键的玩家不该为一个用不到的功能付每帧委托调用的代价。
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.SpecialKeys,
                    () => Settings.ReplayOn
                        && (Settings.AllowedKeyboardSpecialKeys.Count > 0
                            || Settings.AllowedAsyncSpecialKeys.Count > 0));
                SpectrePatchHub.Refresh();

                SpectreState.SetGetKeyAsyncEnabled(Settings.ReplayOn && Settings.GetDataFromAsyncInput);
                SpectreState.data = new ReplayData();
                SpectreState.data.Reset();
                SpectreState.PlayActions.SwitchToRecordMode();

                SpectrePatchHub.ShowApplyFailureSummary();
                // 挂失败的补丁还要在设置页**常驻**说一句：通知弹一次就没了，
                // 而玩家往往是"发现某个开关点了没反应"才去翻设置页。
                Ui.OnGui.PatchFailures = SpectrePatchHub.ApplyFailureCount;

                // Steam 云回填：换机器/重装后把云端有、本地没有的回放写回来。
                // 放在最后一步——它只碰磁盘文件，不依赖任何补丁，失败也只记日志。
                // 内部自带开关判断与 try/catch，关模组后再开也只会再扫一次缺失。
                Features.Cloud.CloudReplay.RestoreOnStartup();
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[SpectreReplay] 装配失败：" + ex);
                return false;
            }
        }

        public static System.Collections.Generic.List<ModEntry> GetLoadedMods()
        {
            try
            {
                return UnityModManager.modEntries;
            }
            catch (Exception arg)
            {
                UnityEngine.Debug.LogError("[" + SpectrePatchHub.LogTag + "] 获取模组列表失败: " + arg);
            }
            return new System.Collections.Generic.List<ModEntry>();
        }

        public static string mod2string(ModEntry mod)
        {
            try
            {
                return "[" + mod.Info.Id + "] " + mod.Info.DisplayName + " v" + mod.Info.Version
                    + " by " + mod.Info.Author + " : " + (mod.Active ? "Enabled" : "Disabled");
            }
            catch (Exception)
            {
                return "[?] " + (mod != null ? mod.Info?.Id ?? "?" : "?");
            }
        }
    }
}
