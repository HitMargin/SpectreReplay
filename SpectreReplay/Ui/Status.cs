using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 提示走**游戏自带的 Notification**。
    ///
    /// 这不是我发明的用法——Spectre 的 SpectreState.TriggerMessage 就是
    /// 这么做的（它的注释写着"Notification 内部 StartCoroutine + UI 操作，
    /// 后台线程直调会炸——转回主线程"）。而 Notification 是游戏自己的类
    /// （Assembly-CSharp/Notification.cs，继承 ADOBase），不是我造的资源。
    ///
    /// Notification.instance 在取用时会自己
    /// Instantiate(RDConstants.data.prefab_notification)，**不需要我们预先
    /// 准备任何东西**。
    ///
    /// 之前自建了一套"设置页顶栏常驻一行"，那有个硬缺陷：**玩家不进设置页
    /// 就看不见**。而提示的意义恰恰在于"你正在打游戏的时候想知道发生了什么"，
    /// 必须在游戏画面上出现。
    /// </summary>
    internal static class Status
    {
        internal enum Level { Info, Warning }

        /// <summary>
        /// 被屏蔽了 raycastTarget 的 Graphic，等协程恢复。
        ///
        /// 类型刻意用 Graphic[]（基类）而不是 Image/Text：预制体内部结构
        /// 随游戏版本变，只认基类才不会因为多了个子类型就漏掉。
        /// </summary>
        private struct Suppressed
        {
            internal Graphic[] Graphics;
            internal bool[] RaycastTargets;
        }

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
                var n = Notification.instance;
                if (n == null)
                {
                    return;
                }
                string text = Loc.T(key);
                if (!string.IsNullOrEmpty(detail) && text.Contains("{0}"))
                {
                    text = string.Format(text, detail);
                }
                n.text.text = text;
                n.icon.enabled = true;
                n.button.onClick.RemoveAllListeners();
                n.icon.sprite = level == Level.Warning ? n.warningIcon : n.completeIcon;
                n.icon.color = level == Level.Warning
                    ? new Color(1f, 0.2f, 0.2f)
                    : new Color(0.2f, 0.7215686f, 0.3921569f);

                // ── 屏蔽提示条上的射线 ──────────────────────────────
                // 不做这一步，弹出来的通知条会**吃掉它下面的点击**：
                // 玩家正准备按下一个砖块，通知正好盖住，判定就没了。
                // 而提示条只是"看一眼就走"的东西，不该有任何交互。
                // 照 Spectre 的 SpectreState.TriggerMessage（:338-341），
                // 它也是关掉后由协程 RestoreGraphics 按原值恢复。
                Suppressed s = SuppressRaycasts(n, seconds);

                // SetupNotification 是 private，编译不过也调不着——
                // 反射拿，跟 Spectre 用 PatchManager.GetMethodInfo 同一个路子。
                var m = typeof(Notification).GetMethod("SetupNotification",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(float), typeof(float), typeof(bool) },
                    null);
                if (m != null)
                {
                    m.Invoke(n, new object[] { seconds, 1f, true });
                }
                RestoreLater(n, s, seconds);
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 提示弹不出来（不影响功能）："
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 把通知条上所有 Graphic 的 raycastTarget 关掉，记下原值。
        ///
        /// `true` 是 includeInactive：预制体里可能有未激活的子对象，
        /// 它们之后被激活时同样需要被屏蔽。
        /// </summary>
        private static Suppressed SuppressRaycasts(Notification n, float seconds)
        {
            var r = new Suppressed { Graphics = null, RaycastTargets = null };
            if (n.bar == null)
            {
                return r;
            }
            r.Graphics = n.bar.GetComponentsInChildren<Graphic>(true);
            r.RaycastTargets = new bool[r.Graphics.Length];
            for (int i = 0; i < r.Graphics.Length; i++)
            {
                if (r.Graphics[i] == null) continue;
                r.RaycastTargets[i] = r.Graphics[i].raycastTarget;
                r.Graphics[i].raycastTarget = false;
            }
            return r;
        }

        /// <summary>
        /// 延时把 raycastTarget 恢复原值。
        ///
        /// 用协程而不是定时器：协程随通知条一起被 Unity 管理，
        /// 场景切换时自动销毁，不会有"恢复回调在场景卸载后触发"的悬垂。
        /// </summary>
        private static void RestoreLater(Notification n, Suppressed s, float seconds)
        {
            if (s.Graphics == null || s.Graphics.Length == 0)
            {
                return;
            }
            try
            {
                n.StartCoroutine(RestoreRoutine(s, seconds));
            }
            catch (Exception)
            {
                // 协程起不来（对象已被销毁）就就地恢复：
                // 宁可在已销毁的对象上写一次，也不能把射线永久留在关闭状态。
                RestoreNow(s);
            }
        }

        private static System.Collections.IEnumerator RestoreRoutine(
            Suppressed s, float seconds)
        {
            // 0.5+0.5 是通知条自己的滑入/淡出余量（照 Spectre RestoreGraphics 的取法）。
            // 多等一点是为了确保玩家真的读完了才把点击还给底下的按钮。
            yield return new WaitForSecondsRealtime(0.5f + 0.5f + seconds + 0.3f);
            RestoreNow(s);
        }

        private static void RestoreNow(Suppressed s)
        {
            if (s.Graphics == null) return;
            for (int i = 0; i < s.Graphics.Length; i++)
            {
                // Unity 对象可能已被销毁：== null 能识别"假 null"，
                // 用 != null 而不是 ReferenceEquals 才安全。
                if (s.Graphics[i] == null) continue;
                s.Graphics[i].raycastTarget = s.RaycastTargets[i];
            }
        }

        /// <summary>
        /// 一句话警告版。用于"操作没生效"这类必须让玩家看见的情况。
        /// </summary>
        internal static void Warn(string key, string detail = null)
            => Say(key, detail, Level.Warning, extra: 2f);
    }
}
