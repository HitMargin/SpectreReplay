using System;
using UnityEngine;
using SpectreReplay.Patching;
using SpectreReplay.Features.Playback;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 回放期间必须夹逼的**全局**状态，照 Spectre 的 SpectreState
    /// （对照文档偏差 19）。
    ///
    /// 这几项与状态轨是一套的：状态轨负责让**判定**复现，这里负责防止
    /// 回放去**污染玩家自己的设置**，以及防止关卡自己把夹逼值重置掉。
    /// </summary>
    internal static class PlaybackClamps
    {
        /// <summary>
        /// 回放期间屏蔽 <c>scrPlanet.MarkFail</c>。
        ///
        /// 照 Spectre 的 <c>Patch_MarkFail</c>（Patch_Misc.cs:73-81）：
        /// Prefix 里 <c>__result = null; return false;</c>——让方法体不执行、
        /// 返回值给 null。
        ///
        /// **为什么需要**：<c>MarkFail()</c>（scrPlanet.cs:1626-1634）无条件
        /// <c>Instantiate</c> 一个 failIndicator 并返回。它有两个调用方：
        /// 游戏自身的失败路径（scrPlayer.cs:301/354/361）与 Spectre 的
        /// <c>ReplayHit</c>。回放期间若有真人误触（或键轨的按键被游戏判失败），
        /// 屏幕上会凭空多出一个红色 miss 指示器，而且它挂在 `next` 行星上，
        /// 会跟着行星一路飞到画面外——**成片里就是一团跟着跑的红色鬼影**。
        ///
        /// 注意与状态轨的分工：<c>HitStateReplayer.ReplayHit</c> 在
        /// <c>NoFailHit</c> 为真时会**主动**调 <c>MarkFail()</c> 并
        /// <c>BlinkForSeconds(3f)</c>——那是录制时就有的 miss 指示器，
        /// 必须复现。所以屏蔽标志由 ReplayHit 自己开合，别的时刻为 false。
        /// </summary>
        [GamePatch(typeof(scrPlanet), "MarkFail", PatchKind.Prefix, "playback")]
        private static bool MarkFailPrefix(ref scrMissIndicator __result)
        {
            if (!IgnoreMarkFail)
            {
                return true;
            }
            __result = null;
            return false;
        }

        /// <summary>
        /// <c>scrFloor.LightUp</c> 前后夹逼判定窗口。
        ///
        /// 照 Spectre 的 <c>Patch_LightUp</c>（Patch_Misc.cs:83-97）。
        ///
        /// **为什么必须夹两次**：<c>LightUp()</c>（scrFloor.cs:1189-1200）里
        /// 有
        ///     if (GCS.hitMarginLimit != HitMarginLimit.PerfectsOnly)
        ///         SetToRandomColor();
        /// 而游戏会在别处把 <c>GCS.hitMarginLimit</c> 写回
        /// <c>Persistence.hitMarginLimit</c>（玩家真实设置）。
        /// 只在开播时设一次的话，砖块亮起时读到的可能已经是玩家自己的档位，
        /// 于是"回放里飘字的判定档位"与录制时不同。
        ///
        /// 夹逼成 <c>PerfectsOnly</c> 是**有意的**：它让 <c>SetToRandomColor</c>
        /// 一定被跳过，砖块颜色不随档位变化——颜色是**录进视频里的**，
        /// 若中途变了，成片与录制就不是一回事。
        /// 之后 Postfix 立刻恢复成录制时的窗口，让后续判定照常按录制档位走。
        ///
        /// 只在回放期间挂载（playback 组按"是否载入回放"门控），
        /// 不影响真人游玩。
        /// </summary>
        [GamePatch(typeof(scrFloor), "LightUp", PatchKind.Prefix, "playback")]
        private static void LightUpPrefix()
        {
            if (PlaybackEngine.IsPlaying)
            {
                GCS.hitMarginLimit = HitMarginLimit.PerfectsOnly;
            }
        }

        [GamePatch(typeof(scrFloor), "LightUp", PatchKind.Postfix, "playback")]
        private static void LightUpPostfix()
        {
            if (PlaybackEngine.IsPlaying)
            {
                GCS.hitMarginLimit = PlaybackEngine.StoredHitMarginLimit;
            }
        }

        /// <summary>由 <c>HitStateReplayer.ReplayHit</c> 在需要复现 miss 时开合。</summary>
        internal static bool IgnoreMarkFail;

        /// <summary>
        /// 开合 miss 屏蔽。
        ///
        /// 用 try/finally 而不是"设了再改回来"：<c>MarkFail</c> 之后还有
        /// <c>BlinkForSeconds</c>，那一行若抛异常（协程启动失败等），
        /// 标志就会**永久留在 true**——之后玩家整局游玩都不会再看到 miss 指示器，
        /// 而且完全无从察觉。
        /// </summary>
        internal static void MarkFailScope(bool ignore, Action body)
        {
            bool backup = IgnoreMarkFail;
            IgnoreMarkFail = ignore;
            try
            {
                body();
            }
            finally
            {
                IgnoreMarkFail = backup;
            }
        }
    }
}
