using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine.SceneManagement;

namespace SpectreReplay.Features.EffectRemover;

/// <summary>
/// 去特效：在关卡数据被解码之后、事件真正执行之前，把指定的特效事件从
/// <see cref="LevelData.levelEvents"/> 里摘掉。
///
/// **为什么在 Decode 之后动手，而不是拦截执行。** 关卡事件在运行期是一条条
/// 按时间轴触发的，要拦就得给每一种事件都挂一个补丁——少挂一个就是"这项特效
/// 没去干净"，而且新增事件类型时必然漏。改数据只有一处：解码完成、还没开始
/// 跑之前。一次改完，之后无论谁读这份 levelEvents 都看不到被去掉的事件。
///
/// **这是破坏性操作。** 它改的是玩家磁盘上那关的数据在内存里的副本，所以
/// 默认只在「播放预览」这条路上生效；要让它把改动写回关卡文件必须显式打开 EnableSave，
/// 否则玩家只是想干净地看一遍谱面，结果保存时把原谱弄没了。
/// </summary>
internal static class EffectRemover
{
    internal static void OnLevelDataDecode(LevelData levelData)
    {
        // 每次 Decode 都是全新的内存副本：先把「这份谱面被我们改写过」清掉。
        // 关掉去特效之后重新进关会走到这里，标记归零，保存才重新放行。
        LevelDataStripped = false;
        Remove(levelData);
    }

    internal static bool OnSaveLevelEditorActionPrefix()
    {
        bool allowed = EditorSaveEnabled;
        if (!allowed && !Enabled)
        {
            // 功能已关、内存里却仍是去特效版：拦住它的不是玩家刚打开的开关，
            // 而是上一次改写留下的残留。不写清楚的话「点了保存没反应」无从排查。
            UnityEngine.Debug.Log("[SpectreReplay] 拒绝保存：本次载入的谱面已被去特效改写，"
                + "而功能现在是关闭的。重新进入关卡即可恢复（那时按原始数据重新解码）。");
        }
        return allowed;
    }

    internal static void OnEditorLoadGameScenePostfix(scnEditor instance) => RefreshEditorSaveButtons(instance);

    /// <summary>条件事件里引用的标签。带这些标签的装饰物会被保留——
    /// 否则去完装饰物，谱面会变成"永远不触发的条件分支"，看着像关卡做坏了。</summary>
    private static readonly string[] ConditionalTagKeys =
    {
        "perfectTag", "hitTag", "earlyPerfectTag", "latePerfectTag",
        "barelyTag", "veryEarlyTag", "veryLateTag",
        "missTag", "tooEarlyTag", "tooLateTag", "lossTag"
    };

    private static bool Enabled => Settings.EffectRemoverOn;

    /// <summary>
    /// 本次载入的谱面是否被我们就地改写过。**它比总开关活得久。**
    ///
    /// <para>玩家关掉去特效之后，内存里那份仍然是剥过的（Decode 已经就地改完了）。
    /// 此时如果只看总开关，保存会被放行，再把这份剥过的数据写回原文件——原谱就没了。
    /// 所以存盘判据必须同时看这个标记，直到下一次 Decode（重新进关）把它清掉。</para>
    ///
    /// <para>只在主线程读写（Decode 与保存都在主线程），不需要同步。</para>
    /// </summary>
    internal static bool LevelDataStripped;

    /// <summary>编辑器里还能不能保存。
    /// 去特效开着时：只有显式允许覆盖（EnableSave）才放行——玩家在编辑器里试听后
    /// 存盘，会把去干净的数据写回原文件。
    /// 去特效关着时：**还要看内存里那份是不是已经被改写过**（见 <see cref="LevelDataStripped"/>），
    /// 否则「先开去特效看一眼、关掉、再 Ctrl+S」照样写坏原谱。</summary>
    internal static bool EditorSaveEnabled
        => Enabled ? Settings.EffectRemoverEnableSave : !LevelDataStripped;

    internal static void RefreshEditorSaveButtons()
    {
        RefreshEditorSaveButtons(scnEditor.instance);
    }

    internal static void RefreshEditorSaveButtons(scnEditor editor)
    {
        SetEditorSaveButtons(editor, EditorSaveEnabled);
    }

    /// <summary>离开编辑器或关掉功能时刷新保存按钮。**不能无条件恢复成可用**：
    /// 关掉去特效时内存里可能仍是剥过的那份（见 <see cref="LevelDataStripped"/>），
    /// 那时按钮必须保持禁用，直到玩家重新进关——否则按钮亮着、Prefix 在拦，
    /// 点了没反应且不报错（正是 UiLogicShapeCheck ⑳ 那组判据要防的「两边脱节」）。</summary>
    internal static void RestoreEditorSaveButtons()
    {
        SetEditorSaveButtons(scnEditor.instance, EditorSaveEnabled);
    }

    private static void SetEditorSaveButtons(scnEditor editor, bool enabled)
    {
        if (editor == null) return;
        if (SceneManager.GetActiveScene().name != "scnEditor") return;

        if (editor.popupUnsavedChangesSave != null)
            editor.popupUnsavedChangesSave.interactable = enabled;
        if (editor.buttonSave != null)
            editor.buttonSave.interactable = enabled;
    }

    internal static void Remove(LevelData levelData)
    {
        if (!Enabled || levelData == null) return;
        // **从这里往下都会动数据**（摘事件、清装饰、改写 backgroundSettings/cameraSettings、
        // 重置轨道不透明度…），所以先把「已被改写」钉住。不能等 events 收集完再判：
        // RemoveBackgrounds 这类分支即使没往 events 里加东西，也已经改过 levelData 了。
        LevelDataStripped = true;

        List<LevelEventType> events = new List<LevelEventType>();

        if (Settings.EffectRemoverDecorations)
            RemoveDecorations(events, levelData);
        if (Settings.EffectRemoverFilters)
            AddFilterEvents(events);
        if (Settings.EffectRemoverAdvancedFilters)
            events.Add(LevelEventType.SetFilterAdvanced);
        if (Settings.EffectRemoverParticles)
        {
            events.Add(LevelEventType.AddParticle);
            events.Add(LevelEventType.EmitParticle);
            events.Add(LevelEventType.SetParticle);
        }
        if (Settings.EffectRemoverBackgrounds)
            RemoveBackgrounds(events, levelData);
        if (Settings.EffectRemoverCameras)
            RemoveCameras(events, levelData);
        if (Settings.EffectRemoverPlanetOrbit)
            events.Add(LevelEventType.SetPlanetRotation);
        if (Settings.EffectRemoverPlanetScale)
            events.Add(LevelEventType.ScalePlanets);
        if (Settings.EffectRemoverPlanetRadius)
            events.Add(LevelEventType.ScaleRadius);
        if (Settings.EffectRemoverRepeatEvents)
            events.Add(LevelEventType.RepeatEvents);
        if (Settings.EffectRemoverFrameRate)
            events.Add(LevelEventType.SetFrameRate);
        if (Settings.EffectRemoverHitSounds)
        {
            events.Add(LevelEventType.PlaySound);
            events.Add(LevelEventType.SetHitsound);
        }
        if (Settings.EffectRemoverCheckpoints)
            events.Add(LevelEventType.Checkpoint);
        if (Settings.EffectRemoverInputEvents)
            events.Add(LevelEventType.SetInputEvent);
        if (Settings.EffectRemoverHoldSounds)
            events.Add(LevelEventType.SetHoldSound);
        if (Settings.EffectRemoverTrackAnimations)
            RemoveTrackAnimations(events, levelData);
        if (Settings.EffectRemoverTrackPositions)
            events.Add(LevelEventType.PositionTrack);
        if (Settings.EffectRemoverTrackMoves)
            events.Add(LevelEventType.MoveTrack);
        if (Settings.EffectRemoverTrackColors)
            RemoveTrackColors(events, levelData);
        if (Settings.EffectRemoverHideIcons)
            events.Add(LevelEventType.Hide);
        if (Settings.EffectRemoverResetTrackOpacity)
            ResetTrackOpacity(levelData);

        if (events.Count == 0) return;

        HashSet<LevelEventType> eventSet = new HashSet<LevelEventType>(events);
        if (levelData.levelEvents != null)
            levelData.levelEvents.RemoveAll(data => data != null && eventSet.Contains(data.eventType));
    }

    private static void AddFilterEvents(List<LevelEventType> events)
    {
        events.Add(LevelEventType.Flash);
        events.Add(LevelEventType.SetFilter);
        events.Add(LevelEventType.HallOfMirrors);
        events.Add(LevelEventType.ShakeScreen);
        events.Add(LevelEventType.Bloom);
        events.Add(LevelEventType.ScreenTile);
        events.Add(LevelEventType.ScreenScroll);
    }

    private static void RemoveBackgrounds(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.CustomBackground);

        if (GCS.settingsInfo.TryGetValue("BackgroundSettings", out var bgSettings))
            levelData.backgroundSettings = new LevelEvent(0, LevelEventType.BackgroundSettings, bgSettings);
        if (levelData.miscSettings != null)
            levelData.miscSettings["bgVideo"] = "";
    }

    private static void RemoveCameras(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.MoveCamera);

        if (!Settings.EffectRemoverSetCameraZoom) return;

        float zoom = Settings.EffectRemoverCameraZoomScale;
        if (zoom < 100f) zoom = 100f;
        if (zoom > 1000f) zoom = 1000f;

        if (GCS.settingsInfo.TryGetValue("CameraSettings", out var camSettings))
        {
            levelData.cameraSettings = new LevelEvent(0, LevelEventType.CameraSettings, camSettings);
            levelData.cameraSettings["zoom"] = zoom;
        }
    }

    private static void RemoveDecorations(List<LevelEventType> events, LevelData levelData)
    {
        if (Settings.EffectRemoverRemoveAllDecorations)
        {
            levelData.decorations?.Clear();
            if (GCS.settingsInfo.TryGetValue("DecorationSettings", out var decSettings))
                levelData.decorationSettings = new LevelEvent(0, LevelEventType.DecorationSettings, decSettings);

            events.Add(LevelEventType.DecorationSettings);
            events.Add(LevelEventType.AddDecoration);
            events.Add(LevelEventType.AddText);
            events.Add(LevelEventType.SetText);
            events.Add(LevelEventType.SetDefaultText);
            events.Add(LevelEventType.MoveDecorations);
            events.Add(LevelEventType.AddObject);
            events.Add(LevelEventType.SetObject);
            return;
        }

        HashSet<string> conditionalEventTags = GetConditionalEventTags(levelData);
        HashSet<string> preservedDecorationTags = GetPreservedDecorationTags(levelData, conditionalEventTags);

        levelData.decorations.RemoveAll(data =>
            IsDecorationData(data) && !ShouldPreserve(data, conditionalEventTags, preservedDecorationTags));
        levelData.levelEvents.RemoveAll(data =>
            IsDecorationData(data) && !ShouldPreserve(data, conditionalEventTags, preservedDecorationTags));
    }

    private static HashSet<string> GetConditionalEventTags(LevelData levelData)
    {
        HashSet<string> tags = new HashSet<string>();

        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (eventData == null || eventData.eventType != LevelEventType.SetConditionalEvents) continue;

            foreach (string key in ConditionalTagKeys)
            {
                if (!eventData.ContainsKey(key)) continue;

                string tag = eventData.GetString(key);
                if (!string.IsNullOrWhiteSpace(tag) && tag != "None" && tag != "없음")
                    tags.Add(tag);
            }
        }

        return tags;
    }

    private static HashSet<string> GetPreservedDecorationTags(LevelData levelData, HashSet<string> conditionalEventTags)
    {
        HashSet<string> tags = new HashSet<string>();

        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (!IsDecorationData(eventData) || !HasAnyEventTag(eventData, conditionalEventTags)) continue;

            foreach (string tag in GetTags(eventData, "tag"))
                tags.Add(tag);
        }

        return tags;
    }

    private static bool ShouldPreserve(LevelEvent eventData, HashSet<string> conditionalEventTags, HashSet<string> preservedDecorationTags)
    {
        return HasAnyEventTag(eventData, conditionalEventTags) || HasAnyTag(eventData, preservedDecorationTags);
    }

    private static bool IsDecorationData(LevelEvent eventData)
    {
        if (eventData == null) return false;

        LevelEventType type = eventData.eventType;
        return type == LevelEventType.DecorationSettings
            || type == LevelEventType.AddDecoration
            || type == LevelEventType.AddText
            || type == LevelEventType.SetText
            || type == LevelEventType.SetDefaultText
            || type == LevelEventType.MoveDecorations
            || type == LevelEventType.AddObject
            || type == LevelEventType.SetObject;
    }

    private static bool HasAnyEventTag(LevelEvent eventData, HashSet<string> tags)
    {
        foreach (string eventTag in GetTags(eventData, "eventTag"))
        {
            if (tags.Contains(eventTag)) return true;
        }
        return false;
    }

    private static bool HasAnyTag(LevelEvent eventData, HashSet<string> tags)
    {
        foreach (string tag in GetTags(eventData, "tag"))
        {
            if (tags.Contains(tag)) return true;
        }
        return false;
    }

    private static IEnumerable<string> GetTags(LevelEvent eventData, string key)
    {
        if (eventData == null || !eventData.ContainsKey(key)) yield break;

        string tags = eventData.GetString(key);
        if (string.IsNullOrWhiteSpace(tags)) yield break;

        string[] split = tags.Split(' ');
        for (int i = 0; i < split.Length; i++)
        {
            string tag = split[i];
            if (!string.IsNullOrWhiteSpace(tag)) yield return tag;
        }
    }

    private static void RemoveTrackAnimations(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.AnimateTrack);

        if (Settings.EffectRemoverResetTrackAnimation && levelData.trackSettings != null)
        {
            levelData.trackSettings["trackAppearAnimation"] = TrackAnimationType.Fade;
            levelData.trackSettings["trackDisappearAnimation"] = TrackAnimationType.Fade;
            levelData.trackSettings["beatsAhead"] = 8.0f;
            levelData.trackSettings["beatsBehind"] = 0.0f;
        }
    }

    private static void RemoveTrackColors(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.ColorTrack);
        events.Add(LevelEventType.RecolorTrack);

        if (Settings.EffectRemoverResetTrackColor && levelData.trackSettings != null)
        {
            levelData.trackSettings["trackStyle"] = TrackStyle.Standard;
            levelData.trackSettings["trackColor"] = "debb7bff";
            levelData.trackSettings["trackColorType"] = TrackColorType.Single;
        }
    }

    private static void ResetTrackOpacity(LevelData levelData)
    {
        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (eventData == null) continue;
            if (eventData.eventType != LevelEventType.MoveTrack && eventData.eventType != LevelEventType.PositionTrack) continue;
            if (eventData.ContainsKey("opacity"))
                eventData["opacity"] = 100.0f;
        }
    }
}
