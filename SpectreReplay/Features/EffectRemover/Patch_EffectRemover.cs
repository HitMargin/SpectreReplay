// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using ADOFAI;
using ADOFAI.Editor.Actions;

namespace SpectreReplay.Features.EffectRemover;

// 去特效的三个接入点。**三个都要挂**，少一个就会出现"看着开了、实际没去干净"：
//   · LevelData.Decode      —— 真正把事件从数据里摘掉的地方（唯一的数据入口）
//   · SaveLevelEditorAction —— 禁止把改写过/去干净的数据写回原文件
//   · scnEditor.LoadGameScene —— 进出编辑器时刷新保存按钮的可用态
//
// **后两个刻意不挂 Feature（与 Decode 不同）。** 原因是「改写」这件事比总开关活得久：
// 玩家开去特效进关（Decode 已就地改完内存里的谱面）之后，把总开关关掉——此时内存里
// 仍是剥过的那份，而保存禁令若随 Feature 一起被卸载，Ctrl+S 就会把它写回原文件，
// 原谱直接没了。所以这两个补丁在模组启用期间常开，由 EffectRemover.EditorSaveEnabled
// 自己判断（功能关着且没改写过时它恒为 true，等于放行，零行为变化）。
internal static class Patch_LevelDataDecode
{
    [SpectrePatch(typeof(LevelData), "Decode", PatchType.Postfix, Feature = FeatureKeys.EffectRemover)]
    internal static void Postfix(LevelData __instance) => EffectRemover.OnLevelDataDecode(__instance);
}

internal static class Patch_SaveLevelEditorAction
{
    [SpectrePatch(typeof(SaveLevelEditorAction), "Execute", PatchType.Prefix)]
    internal static bool Prefix() => EffectRemover.OnSaveLevelEditorActionPrefix();
}

internal static class Patch_EditorLoadGameScene
{
    [SpectrePatch(typeof(scnEditor), "LoadGameScene", PatchType.Postfix)]
    internal static void Postfix(scnEditor __instance) => EffectRemover.OnEditorLoadGameScenePostfix(__instance);
}