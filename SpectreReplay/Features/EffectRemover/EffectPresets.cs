using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using SpectreReplay.Features.Cloud;

namespace SpectreReplay.Features.EffectRemover;

/// <summary>
/// 去特效的「预设」（多配置）：把「摘掉哪些特效」这组开关存成一份 .json，
/// 再一键应用回去。
///
/// <para><b>存与应用是两种语义。</b>保存 = 全量快照（25 个开关全部写入，
/// 拿到别的机器也能完整复现）；应用 = 增量补丁（只改文件里写到的键，
/// 缺的键保持现状）——与 ConfigManager 的「增量读、保默认」同一哲学，
/// 旧预设缺新开关的键时不会把新开关悄悄打回默认。</para>
///
/// <para><b>白名单刻意不含总开关与「允许覆盖存盘」。</b>这两项是授权类
/// 开关（见 DrawEffects 的口径注释）：一键全选刻意排除它们，预设同理——
/// 导入一份别人给的配置不该顺手打开「允许改写玩家关卡文件」。</para>
///
/// <para><b>文件位置：模组目录 \EffectPresets\*.json。</b>玩家把外面拿来的
/// .json 丢进这个目录就算导入（「打开预设目录」按钮直达）。文件名永远走
/// <see cref="Path.GetFileName"/> 校验，拒绝任何带路径的输入。</para>
/// </summary>
internal static class EffectPresets
{
    /// <summary>预设文件在 Steam 云里的目录（与回放/配置的命名空间隔离）。</summary>
    internal const string CloudPrefix = SteamCloud.RemoteRoot + "/EffectPresets/";

    internal static string PresetFolder
        => Main.mod == null || string.IsNullOrEmpty(Main.mod.Path)
            ? ""
            : Path.Combine(Main.mod.Path, "EffectPresets");

    /// <summary>列出预设目录里的 .json 文件名（不含路径），按名称排序。</summary>
    internal static List<string> List()
    {
        var files = new List<string>();
        string dir = PresetFolder;
        if (dir.Length == 0 || !Directory.Exists(dir)) return files;
        foreach (string f in Directory.GetFiles(dir, "*.json"))
            files.Add(Path.GetFileName(f));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// 应用一份预设。返回 (是否成功, 更新的开关数, 失败原因)。
    /// 单个键的值类型坏了只跳过那一键，不连累其余（一个手改坏的值
    /// 不该让整份预设作废）。
    /// </summary>
    internal static (bool Ok, int Applied, string Error) Apply(string fileName)
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0 || !IsSafeFileName(fileName))
                return (false, 0, "bad file name");
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                return (false, 0, "file not found");

            var root = JObject.Parse(File.ReadAllText(path));
            var settings = root["settings"] as JObject;
            if (settings == null)
                return (false, 0, "no settings section");

            int applied = 0;
            foreach (var prop in settings.Properties())
            {
                try
                {
                    switch (prop.Name)
                    {
                        case "EffectRemoverFilters": Settings.EffectRemoverFilters = (bool)prop.Value; applied++; break;
                        case "EffectRemoverAdvancedFilters": Settings.EffectRemoverAdvancedFilters = (bool)prop.Value; applied++; break;
                        case "EffectRemoverParticles": Settings.EffectRemoverParticles = (bool)prop.Value; applied++; break;
                        case "EffectRemoverDecorations": Settings.EffectRemoverDecorations = (bool)prop.Value; applied++; break;
                        case "EffectRemoverBackgrounds": Settings.EffectRemoverBackgrounds = (bool)prop.Value; applied++; break;
                        case "EffectRemoverCameras": Settings.EffectRemoverCameras = (bool)prop.Value; applied++; break;
                        case "EffectRemoverPlanetOrbit": Settings.EffectRemoverPlanetOrbit = (bool)prop.Value; applied++; break;
                        case "EffectRemoverPlanetScale": Settings.EffectRemoverPlanetScale = (bool)prop.Value; applied++; break;
                        case "EffectRemoverPlanetRadius": Settings.EffectRemoverPlanetRadius = (bool)prop.Value; applied++; break;
                        case "EffectRemoverRepeatEvents": Settings.EffectRemoverRepeatEvents = (bool)prop.Value; applied++; break;
                        case "EffectRemoverFrameRate": Settings.EffectRemoverFrameRate = (bool)prop.Value; applied++; break;
                        case "EffectRemoverHitSounds": Settings.EffectRemoverHitSounds = (bool)prop.Value; applied++; break;
                        case "EffectRemoverCheckpoints": Settings.EffectRemoverCheckpoints = (bool)prop.Value; applied++; break;
                        case "EffectRemoverInputEvents": Settings.EffectRemoverInputEvents = (bool)prop.Value; applied++; break;
                        case "EffectRemoverHoldSounds": Settings.EffectRemoverHoldSounds = (bool)prop.Value; applied++; break;
                        case "EffectRemoverTrackAnimations": Settings.EffectRemoverTrackAnimations = (bool)prop.Value; applied++; break;
                        case "EffectRemoverTrackPositions": Settings.EffectRemoverTrackPositions = (bool)prop.Value; applied++; break;
                        case "EffectRemoverTrackMoves": Settings.EffectRemoverTrackMoves = (bool)prop.Value; applied++; break;
                        case "EffectRemoverTrackColors": Settings.EffectRemoverTrackColors = (bool)prop.Value; applied++; break;
                        case "EffectRemoverHideIcons": Settings.EffectRemoverHideIcons = (bool)prop.Value; applied++; break;
                        case "EffectRemoverRemoveAllDecorations": Settings.EffectRemoverRemoveAllDecorations = (bool)prop.Value; applied++; break;
                        case "EffectRemoverResetTrackOpacity": Settings.EffectRemoverResetTrackOpacity = (bool)prop.Value; applied++; break;
                        case "EffectRemoverResetTrackAnimation": Settings.EffectRemoverResetTrackAnimation = (bool)prop.Value; applied++; break;
                        case "EffectRemoverResetTrackColor": Settings.EffectRemoverResetTrackColor = (bool)prop.Value; applied++; break;
                        case "EffectRemoverCameraZoomScale":
                            double? zoom = (double?)prop.Value;
                            if (zoom.HasValue)
                            {
                                Settings.EffectRemoverCameraZoomScale = Mathf.Clamp((float)zoom.Value, 100f, 1000f);
                                applied++;
                            }
                            break;
                    }
                }
                catch
                {
                    // 单键坏值：跳过，不让一个手改坏的 true/"abc" 毁掉整份预设
                }
            }
            return (true, applied, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// 把当前开关存成一份全量快照。返回 (是否成功, 文件名, 失败原因)。
    /// 名字非法字符替换成下划线；留空则用时间戳兜底。
    /// </summary>
    internal static (bool Ok, string FileName, string Error) Save(string name)
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0)
                return (false, null, "mod path unavailable");
            Directory.CreateDirectory(dir);

            name = SanitizeName(name);
            if (name.Length == 0)
                name = "preset_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = name + ".json";

            var settings = new JObject
            {
                ["EffectRemoverFilters"] = Settings.EffectRemoverFilters,
                ["EffectRemoverAdvancedFilters"] = Settings.EffectRemoverAdvancedFilters,
                ["EffectRemoverParticles"] = Settings.EffectRemoverParticles,
                ["EffectRemoverDecorations"] = Settings.EffectRemoverDecorations,
                ["EffectRemoverBackgrounds"] = Settings.EffectRemoverBackgrounds,
                ["EffectRemoverCameras"] = Settings.EffectRemoverCameras,
                ["EffectRemoverPlanetOrbit"] = Settings.EffectRemoverPlanetOrbit,
                ["EffectRemoverPlanetScale"] = Settings.EffectRemoverPlanetScale,
                ["EffectRemoverPlanetRadius"] = Settings.EffectRemoverPlanetRadius,
                ["EffectRemoverRepeatEvents"] = Settings.EffectRemoverRepeatEvents,
                ["EffectRemoverFrameRate"] = Settings.EffectRemoverFrameRate,
                ["EffectRemoverHitSounds"] = Settings.EffectRemoverHitSounds,
                ["EffectRemoverCheckpoints"] = Settings.EffectRemoverCheckpoints,
                ["EffectRemoverInputEvents"] = Settings.EffectRemoverInputEvents,
                ["EffectRemoverHoldSounds"] = Settings.EffectRemoverHoldSounds,
                ["EffectRemoverTrackAnimations"] = Settings.EffectRemoverTrackAnimations,
                ["EffectRemoverTrackPositions"] = Settings.EffectRemoverTrackPositions,
                ["EffectRemoverTrackMoves"] = Settings.EffectRemoverTrackMoves,
                ["EffectRemoverTrackColors"] = Settings.EffectRemoverTrackColors,
                ["EffectRemoverHideIcons"] = Settings.EffectRemoverHideIcons,
                ["EffectRemoverRemoveAllDecorations"] = Settings.EffectRemoverRemoveAllDecorations,
                ["EffectRemoverResetTrackOpacity"] = Settings.EffectRemoverResetTrackOpacity,
                ["EffectRemoverResetTrackAnimation"] = Settings.EffectRemoverResetTrackAnimation,
                ["EffectRemoverResetTrackColor"] = Settings.EffectRemoverResetTrackColor,
                ["EffectRemoverCameraZoomScale"] = Settings.EffectRemoverCameraZoomScale,
            };
            var root = new JObject
            {
                ["version"] = 1,
                ["name"] = name,
                ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["settings"] = settings,
            };
            // UTF-8 无 BOM：与 Configs.json 同一口径，方便手工编辑/跨机器分享
            File.WriteAllText(Path.Combine(dir, fileName),
                root.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
            return (true, fileName, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>删除一份预设。文件名必须是不带路径的纯文件名。</summary>
    internal static (bool Ok, string Error) Delete(string fileName)
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0 || !IsSafeFileName(fileName))
                return (false, "bad file name");
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path))
                File.Delete(path);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 把选中的预设改成新名字。目标同名已存在时拒绝——重命名覆盖别人
    /// 辛苦调好的预设，比删除还隐蔽，必须显式先删再改。
    /// </summary>
    internal static (bool Ok, string FileName, string Error) Rename(string oldFile, string newName)
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0 || !IsSafeFileName(oldFile))
                return (false, null, "bad file name");
            string source = Path.Combine(dir, oldFile);
            if (!File.Exists(source))
                return (false, null, "file not found");
            newName = SanitizeName(newName);
            if (newName.Length == 0)
                return (false, null, "empty name");
            string target = newName + ".json";
            if (string.Equals(target, oldFile, StringComparison.OrdinalIgnoreCase))
                return (true, target, null);
            if (File.Exists(Path.Combine(dir, target)))
                return (false, null, "exists");
            File.Move(source, Path.Combine(dir, target));
            return (true, target, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// 把本地预设上传到 Steam 云（云端同名会被覆盖——「上传」就是以本地为准）。
    /// </summary>
    internal static (bool Ok, string Error) UploadToCloud(string fileName)
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0 || !IsSafeFileName(fileName))
                return (false, "bad file name");
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                return (false, "file not found");
            if (!SteamCloud.Ready)
                return (false, "not_available");
            return SteamCloud.Upload(CloudPrefix + fileName, File.ReadAllBytes(path), out string err)
                ? (true, null)
                : (false, err);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 从 Steam 云拉取所有本地没有的预设。**本地已有的同名文件不覆盖**——
    /// 与回放找回同一口径：云端是补给线，不是权威副本。
    /// 单个文件下载失败只跳过该文件，不中断其余。
    /// </summary>
    internal static (int Pulled, int Skipped, string Error) PullFromCloud()
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0)
                return (0, 0, "mod path unavailable");
            if (!SteamCloud.Ready)
                return (0, 0, "not_available");
            Directory.CreateDirectory(dir);
            int pulled = 0, skipped = 0;
            foreach (var e in SteamCloud.List(CloudPrefix))
            {
                string name = Path.GetFileName(e.Name);
                if (!IsSafeFileName(name))
                {
                    skipped++;
                    continue;
                }
                string local = Path.Combine(dir, name);
                if (File.Exists(local))
                {
                    skipped++;
                    continue;
                }
                if (!SteamCloud.Download(e.Name, out byte[] data, out _))
                {
                    skipped++;
                    continue;
                }
                File.WriteAllBytes(local, data);
                pulled++;
            }
            return (pulled, skipped, null);
        }
        catch (Exception ex)
        {
            return (0, 0, ex.Message);
        }
    }

    /// <summary>在资源管理器里打开预设目录（没有就先创建），方便手工放 .json 导入。</summary>
    internal static void OpenFolder()
    {
        try
        {
            string dir = PresetFolder;
            if (dir.Length == 0) return;
            Directory.CreateDirectory(dir);
            Application.OpenURL(dir);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 打开预设目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 只接受「纯文件名」：带任何路径成分（..\、绝对路径、子目录）一律拒绝。
    /// 应用/删除的输入来自 UI 下拉框，但目录是玩家可写的——不设防的话
    /// 一份恶意文件名就能删到模组目录之外。
    /// </summary>
    private static bool IsSafeFileName(string fileName)
        => !string.IsNullOrEmpty(fileName)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && fileName.Length > 5
            && fileName == Path.GetFileName(fileName);

    /// <summary>名字清洗：去空白、非法字符换下划线。空串交给调用方兜底。</summary>
    private static string SanitizeName(string name)
    {
        name = (name ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
