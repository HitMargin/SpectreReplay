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
    /// 入口：把 Spectre 的 replay + video render 整体移植进本模组后的装配。
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
                UiConfigManager.Load(Path.Combine(modEntry.Path, "ConfigsUI.json"));
                Settings.check(modEntry.Path);
                WavLoader.reset();

                modEntry.OnGUI = Ui.OnGui.OnGUI;
                modEntry.OnShowGUI = Ui.OnGui.OnShow;
                modEntry.OnHideGUI = Ui.OnGui.OnHide;
                modEntry.OnSaveGUI = Ui.OnGui.OnHide;
                modEntry.OnToggle = OnToggle;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[SpectreReplay] 加载失败：" + ex);
                try { modEntry?.Logger?.Log("[SpectreReplay] 加载失败：" + ex); } catch { }
                throw;
            }
        }

        private static bool OnToggle(ModEntry modEntry, bool isToggled)
        {
            if (!isToggled)
            {
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

                // 回放功能开关（本移植只带 Replay 一个功能族）
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.Replay, () => Settings.ReplayOn);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayManualSave, () => Settings.ReplayOn && Settings.ManualSave);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayCancelSave, () => Settings.ReplayOn && Settings.CancelSave);
                SpectrePatchHub.SetFeatureToggle(FeatureKeys.ReplayAsyncInput, () => Settings.ReplayOn && Settings.GetDataFromAsyncInput);
                SpectrePatchHub.SetMasterToggle(() => Settings.UiPowerOn);
                SpectrePatchHub.Refresh();

                SpectreState.SetGetKeyAsyncEnabled(Settings.ReplayOn && Settings.GetDataFromAsyncInput);
                SpectreState.data = new LegacyReplayData();
                SpectreState.data.reset();
                SpectreState.PlayActions.SwitchToRecordMode();

                SpectrePatchHub.ShowApplyFailureSummary();
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
