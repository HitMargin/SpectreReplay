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
                // **Save 不能接 OnHide。** UMM 底部的 Save 走 SaveSettingsAndParams()，
                // 它只调 OnSaveGUI，既不关窗口、也不隐藏我们的设置页
                // （UnityModManager.decompiled.cs:3706-3709 / :6326-6343）。
                // 原先接 OnHide，而 OnHide 第一句是 `_visible = false`——
                // 玩家一点 Save，整块设置内容当场变成空白。
                modEntry.OnSaveGUI = Ui.OnGui.OnSave;
                // 退出时补上拖动滑条期间被推迟的落盘（见 Pages.Commit）。
                // 没有这一下，"拖完滑条直接退游戏"= 这次改动白改，而现象是
                // "设置保存会丢"——正是这一轮要摁死的第三个毛病，不能自己再开一个口子。
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
                SpectreState.data = new LegacyReplayData();
                SpectreState.data.reset();
                SpectreState.PlayActions.SwitchToRecordMode();

                SpectrePatchHub.ShowApplyFailureSummary();
                // 挂失败的补丁还要在设置页**常驻**说一句：通知弹一次就没了，
                // 而玩家往往是"发现某个开关点了没反应"才去翻设置页。
                Ui.OnGui.PatchFailures = SpectrePatchHub.ApplyFailureCount;
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
