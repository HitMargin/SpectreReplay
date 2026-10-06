// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using HarmonyLib;
using SpectreReplay.Features.ResultsPlus;

namespace SpectreReplay.Features.Replay;

// 命中误差采集：本文件是 Spectre 侧两个补丁的移植。
//
// **为什么必须有它。** SpectreReplay 里 HitErrorData（误差样本）、CalibrationAdvisor
// （校准建议）、Settings.CalibSamples/CalibGames（持久化载体）全都随移植搬了过来，
// 而 Patch_Lifecycle 的 Start_Rewind Postfix 每局都在调 HitErrorData.ResetForNewRun() 与
// CalibrationAdvisor.OnNewRun()。但**喂数据的两个补丁没搬**：
//   · CapturePending（SwitchChosen Prefix 缓存判定参数）
//   · Record（AddHit Postfix 落一条样本）
// 全仓对这两个方法零调用 —— 也就是说"每局清零一份永远是空的数据"。
// 表现形式不是报错，而是：校准建议永远攒不够 MinSamples=40 条样本、
// 结算里看不到偏移统计，而玩家以为"再练练就会出建议"。
//
// 两个补丁必须成对启用，同挂 FeatureKeys.ResultsPlus；缺任何一个，
// hasPending 永远为 false / Entries 永远为空（Record 里没有 pending 时 Ms=null，
// 全部样本不被 IsValidSample 认可），最终结果都是空集。
internal static class Patch_HitErrorSwitchChosen
{
    [SpectrePatch(typeof(scrPlanet), "SwitchChosen", PatchType.Prefix, Feature = FeatureKeys.ResultsPlus)]
    internal static void Prefix(scrPlanet __instance) => HitErrorData.CapturePending(__instance);
}

internal static class Patch_HitErrorAddHit
{
    // 唯一的 Postfix：只读记账结果（hitMargins.Count 与本次 margin）。
    // 全仓不得出现任何对 AddHit 的**调用**——那会污染判定记账（见 FeatureParityCheck ④ 的判据）。
    [SpectrePatch(typeof(scrMarginTracker), "AddHit", PatchType.Postfix, Feature = FeatureKeys.ResultsPlus)]
    internal static void Postfix(scrMarginTracker __instance, HitMargin hit)
        => HitErrorData.Record(__instance, hit);
}

// 结算时把本局的有效样本并进滚动窗口。Spectre 这一环在 DetailedResults.Show 的
// Postfix 上（同时负责结算文本追加），SpectreReplay 不移植结算文本，
// 只需保留 CommitRun 这一半——否则上面采到的样本永远没有消费方。
internal static class Patch_ResultsCommitCalib
{
    [SpectrePatch(typeof(DetailedResults), "Show", PatchType.Postfix, Feature = FeatureKeys.CalibAdvice)]
    internal static void Postfix(DetailedResults __instance)
    {
        var controller = scrController.instance;
        var player = controller != null ? controller.playerOne : null;
        if (player == null || player.marginTracker == null)
            return;
        CalibrationAdvisor.CommitRun(player.marginTracker.hitMargins);
    }
}
