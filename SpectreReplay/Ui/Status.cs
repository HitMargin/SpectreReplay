using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 提示走自绘 <see cref="Toast"/>（深色圆角提示卡）。
    ///
    /// 之前走游戏 Notification：那条 UGUI 横幅样式固定、没法做滑入/剩余时间条，
    /// 切场景黑幕还可能盖住它（要靠 RaiseAboveGameUi 特判）。2026-11-05 起
    /// TriggerMessage / Say 全部改走同一条 Toast 链路，样式、时机、合并策略
    /// 全部可控，也不再需要提 Canvas 层级或屏蔽射线（Toast 纯展示不响应鼠标）。
    /// </summary>
    internal static class Status
    {
        internal enum Level { Info, Warning }

        /// <summary>
        /// 弹一条提示。
        ///
        /// <paramref name="extra"/> 是**在玩家设定值之上多加的秒数**（默认 0），
        /// 不是绝对时长——解析统一走 <see cref="Settings.NotificationDuration"/>，
        /// 与 SpectreState.TriggerMessage 同一套口径。
        /// </summary>
        internal static void Say(string key, string detail = null,
            Level level = Level.Info, float extra = 0f)
        {
            try
            {
                float seconds = Settings.NotificationDuration(extra);
                string text = Loc.T(key);
                if (!string.IsNullOrEmpty(detail) && text.Contains("{0}"))
                {
                    text = string.Format(text, detail);
                }
                // 2026-11-05 起改走自绘 Toast（深色圆角提示卡），
                // 与 SpectreState.TriggerMessage 同一条视觉链路。
                // 时长口径不变：Settings.NotificationDuration(extra)。
                Toast.Show(text, seconds, level == Level.Warning);
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 提示弹不出来（不影响功能）："
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 一句话警告版。用于"操作没生效"这类必须让玩家看见的情况。
        /// </summary>
        internal static void Warn(string key, string detail = null)
            => Say(key, detail, Level.Warning, extra: 2f);
    }
}
