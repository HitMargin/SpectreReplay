using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 命中去重的**纯逻辑**，不碰引擎。
    ///
    /// 单独拆出来只有一个理由：**它可以被离线验证。**
    /// 去重的判据一旦写错（少一个条件、比较了不该比较的量），
    /// 后果是状态轨里留下被撤销的记录 → 回放时同一块砖被判两次 →
    /// 判定条飘两次字、行星跳两格。这类缺陷在真机上表现为
    /// "偶尔有点怪"，抓不住也归因不了。
    ///
    /// 而它本来就只有两层逻辑，值得为可测性拆出来：
    /// 引擎部分（scrPlayer / chosenPlanet）留在 KeyRecorder 里，
    /// 判定条件全在这里，预览器直接链接本文件跑。
    ///
    /// 照 Spectre 的 Patch_Recording.cs:99-112 的去重规则。
    /// </summary>
    internal static class HitDedupe
    {
        /// <summary>
        /// 记一次命中，必要时撤掉上一条。
        ///
        /// 命中会被撤销：手滑多按一次、游戏自己重判、或长按段里
        /// 同一块砖连着判两次。撤销的实现是"删掉上一条"，所以
        /// 判据必须是"这两条其实是同一次命中"。
        /// </summary>
        internal static void Record(List<ReplayHitContext> list, ReplayHitContext hc)
        {
            if (list == null || hc.CurrentFloorId < 0)
            {
                // 地板取不到（场景卸载的中间态）：这种条目无法定位，
                // 录进去会在回放时乱触发命中。宁可少一条。
                return;
            }
            if (list.Count > 0 && SameHit(list[list.Count - 1], hc))
            {
                list.RemoveAt(list.Count - 1);
            }
            list.Add(hc);
        }

        /// <summary>
        /// 两条记录是不是同一次命中。
        ///
        /// **必须比地板序号。** 自由漫游区段内地板序号会重复，
        /// 只比状态会在漫游里误撤真命中——表现为漫游段少一次
        /// 行星切换，画面卡在两块砖中间。
        ///
        /// **刻意不比较角度。** angle / CachedAngle / TargetExitAngle
        /// 每帧都在变。把它们算进"同一次"里，去重就几乎永不触发，
        /// 撤销的记录会留在状态轨里——那正是这个函数要防的事。
        /// 剩下的几项在"同砖重复判定"时是不变的。
        /// </summary>
        internal static bool SameHit(ReplayHitContext a, ReplayHitContext b)
            => a.CurrentFloorId == b.CurrentFloorId
            && a.FreeRoamSection == b.FreeRoamSection
            && a.NoFailHit == b.NoFailHit
            && a.IsAuto == b.IsAuto
            && a.NextFloorAuto == b.NextFloorAuto
            && a.RdcAuto == b.RdcAuto
            && a.MidspinInfiniteMargin == b.MidspinInfiniteMargin
            && a.OverloadCounter.Equals(b.OverloadCounter);
    }
}
