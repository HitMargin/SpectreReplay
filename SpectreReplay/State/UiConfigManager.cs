using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SpectreReplay;

// UI 外壳配置独立落盘（ConfigsUI.json）：面板几何、主题色、字号、界面热键、电源钮。
// 与玩法配置（Configs.json）分离——分享/重置任意一边都不会牵动另一边。
// 读取用可空字段语义：文件缺键保持代码默认值，不会被 0 覆盖
internal static class UiConfigManager
{
    private static string _path = "";

    internal static void Load(string filePath)
    {
        _path = filePath;
        if (!File.Exists(filePath)) return;
        try
        {
            var root = JObject.Parse(File.ReadAllText(filePath));
            var sets = root["sets"] as JObject;
            if (sets == null) return;
            Settings.UiPanelWidthFrac = sets.Value<float?>("panel_w_frac") ?? Settings.UiPanelWidthFrac;
            Settings.UiPanelHeightFrac = sets.Value<float?>("panel_h_frac") ?? Settings.UiPanelHeightFrac;
            Settings.UiPanelOpacity = sets.Value<float?>("panel_opacity") ?? Settings.UiPanelOpacity;
            Settings.UiPanelPosX = sets.Value<float?>("panel_pos_x") ?? Settings.UiPanelPosX;
            Settings.UiPanelPosY = sets.Value<float?>("panel_pos_y") ?? Settings.UiPanelPosY;
            Settings.UiAccentR = sets.Value<float?>("accent_r") ?? Settings.UiAccentR;
            Settings.UiAccentG = sets.Value<float?>("accent_g") ?? Settings.UiAccentG;
            Settings.UiAccentB = sets.Value<float?>("accent_b") ?? Settings.UiAccentB;
            Settings.TextSize = sets.Value<int?>("text_size") ?? Settings.TextSize;
            Settings.UiHotkey = sets.Value<int?>("ui_hotkey") ?? Settings.UiHotkey;
            Settings.UiPowerOn = sets.Value<bool?>("ui_power_on") ?? Settings.UiPowerOn;
            Settings.UiFlyDuration = sets.Value<float?>("fly_duration") ?? Settings.UiFlyDuration;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[Spectre] UI 配置读取失败: " + ex.Message);
        }
    }

    /// <summary>把面板位置/尺寸/透明度/主题色/字号/快捷键写入 ConfigsUI.json。
    /// 返回 false 表示未落盘（路径未定、写失败）——原实现只记日志就返回 void，
    /// 调用方无从判断，用户看到的"拖了没反应"没有任何提示。失败原因对用户
    /// 不可查（磁盘满/目录不可写/文件被占用），故失败必须由调用方告知，
    /// 与 ConfigManager.SaveConfigs 的处理保持一致。</summary>
    internal static bool Save()
    {
        if (string.IsNullOrEmpty(_path)) return false;
        try
        {
            var root = new JObject
            {
                ["sets"] = new JObject
                {
                    ["panel_w_frac"] = Settings.UiPanelWidthFrac,
                    ["panel_h_frac"] = Settings.UiPanelHeightFrac,
                    ["panel_opacity"] = Settings.UiPanelOpacity,
                    ["panel_pos_x"] = Settings.UiPanelPosX,
                    ["panel_pos_y"] = Settings.UiPanelPosY,
                    ["accent_r"] = Settings.UiAccentR,
                    ["accent_g"] = Settings.UiAccentG,
                    ["accent_b"] = Settings.UiAccentB,
                    ["text_size"] = Settings.TextSize,
                    ["ui_hotkey"] = Settings.UiHotkey,
                    ["ui_power_on"] = Settings.UiPowerOn,
                    ["fly_duration"] = Settings.UiFlyDuration,
                }
            };
            File.WriteAllText(_path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[Spectre] UI 配置写入失败: " + ex.Message);
            return false;
        }
    }
}
