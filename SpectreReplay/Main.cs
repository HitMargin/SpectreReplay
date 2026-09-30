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
            Ui.UiHost.Hotkey = Settings.Hotkey;
            Ui.UiHost.Install();
            Pages.SettingsPages.RegisterAll();

            // 功能门控：设置页改开关后由 Settings.Commit 触发 PatchLoader.Refresh
            Patching.FeatureGate.Declare("recording", () => Settings.RecordingOn);
            Patching.FeatureGate.Declare("render", () => Settings.RenderOn);
            Patching.PatchLoader.Register();

            mod.Logger.Log("[SpectreReplay] 已加载：补丁 "
                + Patching.PatchLoader.AppliedCount + "/" + Patching.PatchLoader.DeclaredCount
                + "（界面热键 F" + ((Settings.Hotkey - 288) is int f && f > 0 && f < 12 ? f.ToString() : Settings.Hotkey.ToString()) + "）");
            IsReady = true;
        }
    }
}
