// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using ADOFAI;
using ADOFAI.Editor.Actions;

namespace SpectreReplay.Features.EffectRemover;

// 去特效的三个接入点。**三个都要挂**，少一个就会出现"看着开了、实际没去干净"：
//   · LevelData.Decode      —— 真正把事件从数据里摘掉的地方（唯一的数据入口）
//   · SaveLevelEditorAction —— 去特效开着时禁止存盘，避免把清干净的数据写回原文件
//   · scnEditor.LoadGameScene —— 进出编辑器时刷新保存按钮的可用态
internal static class Patch_LevelDataDecode
{
    [SpectrePatch(typeof(LevelData), "Decode", PatchType.Postfix, Feature = FeatureKeys.EffectRemover)]
    internal static void Postfix(LevelData __instance) => EffectRemover.OnLevelDataDecode(__instance);
}

internal static class Patch_SaveLevelEditorAction
{
    [SpectrePatch(typeof(SaveLevelEditorAction), "Execute", PatchType.Prefix, Feature = FeatureKeys.EffectRemover)]
    internal static bool Prefix() => EffectRemover.OnSaveLevelEditorActionPrefix();
}

internal static class Patch_EditorLoadGameScene
{
    [SpectrePatch(typeof(scnEditor), "LoadGameScene", PatchType.Postfix, Feature = FeatureKeys.EffectRemover)]
    internal static void Postfix(scnEditor __instance) => EffectRemover.OnEditorLoadGameScenePostfix(__instance);
}
