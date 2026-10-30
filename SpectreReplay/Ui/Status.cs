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

                // 提到游戏 UI 之上，否则切场景的黑幕会盖住它（见 RaiseAboveGameUi）。
                RaiseAboveGameUi(n);

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
        /// 把提示条所在的 Canvas 提到游戏 UI 之上。
        ///
        /// <para><b>为什么需要这一步。</b>切场景时 <c>scrUIController.WipeToBlack</c> 会把
        /// <c>transitionPanel</c>（一张全屏黑 Image）缩放铺满屏幕，0.3 秒后场景才真正
        /// 加载。提示条在这段黑幕期间**仍然存在、动画也还在跑**，只是被黑幕挡在下面，
        /// 玩家看不到——而「渲染失败」「存档丢失」这类提示恰恰最需要在那几秒被看见。</para>
        ///
        /// <para><b>取值的依据。</b>不用 32767：那是 UnityModManager 自己弹面板用的
        /// （<c>UMM:4135</c> 用 ScreenSpaceOverlay + 32767 盖住整个游戏），压过它会让
        /// 提示条浮在 UMM 设置页上面。这里取一个「高于游戏内所有 Canvas、低于 UMM」的
        /// 值就够了——游戏自带的 UI 都在几百以内。</para>
        ///
        /// <para><b>为什么要 overrideSorting。</b>嵌套 Canvas 的 sortingOrder 默认不生效，
        /// 必须同时把 <c>overrideSorting</c> 置真、并给它一个 GraphicRaycaster（Unity 要求
        /// 覆盖排序的 Canvas 必须能接收射线，否则会报警告）。提示条本来就要屏蔽射线
        /// （见 SuppressRaycasts），所以多挂一个 Raycaster 不会带来误点。</para>
        ///
        /// <para>只在第一次提到时打印一次实测层级，供后续核对——这条日志本身就是
        /// 「探针」，不需要为了量一次值再单独做一版构建。</para>
        /// </summary>
        private const int NotificationSortingOrder = 30000;

        private static bool _raised;

        private static void RaiseAboveGameUi(Notification n)
        {
            if (_raised || n == null)
            {
                return;
            }

            // 提示条自带 Canvas 时提它；没有就提最近的父级 Canvas（同一套 API）。
            Canvas canvas = n.GetComponent<Canvas>() ?? n.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                // 没有 Canvas 说明它并进某个已有 canvas 一起排序——那就没得提，
                // 记一条日志说明"这次的提示在黑幕期间可能看不见"，而不是静默失败。
                Debug.Log("[SpectreReplay] 提示条没有独立 Canvas，无法提到黑幕之上"
                    + "（切场景期间可能看不见，但不影响功能）");
                _raised = true;
                return;
            }

            try
            {
                int before = canvas.sortingOrder;
                string modeBefore = canvas.renderMode.ToString();

                // 记录黑幕所在的层级，便于确认「提上去了」这件事真的成立。
                string wipeInfo = "n/a";
                try
                {
                    var ui = scrUIController.instance;
                    if (ui != null && ui.transitionPanel != null)
                    {
                        Canvas wipeCanvas = ui.transitionPanel.GetComponentInParent<Canvas>();
                        if (wipeCanvas != null)
                        {
                            wipeInfo = "order=" + wipeCanvas.sortingOrder
                                + " mode=" + wipeCanvas.renderMode
                                + " sameAsNotification=" + ReferenceEquals(wipeCanvas, canvas);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 这段只进 Player.log，用 ASCII 标记而不是中文短语：
                    // 不然会被闸门的「硬编码玩家可见文案」检查当成界面文字。
                    wipeInfo = "unreadable(" + ex.GetType().Name + ")";
                }

                canvas.overrideSorting = true;
                canvas.sortingOrder = NotificationSortingOrder;
                if (canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                {
                    canvas.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                }

                _raised = true;
                Debug.Log("[SpectreReplay] 提示条已提到游戏 UI 之上：order "
                    + before + " → " + NotificationSortingOrder
                    + "（原 mode=" + modeBefore + "）；黑幕 canvas " + wipeInfo);
            }
            catch (Exception ex)
            {
                // 提不上去不该影响提示本身：它只是可能被黑幕挡住，功能没坏。
                Debug.Log("[SpectreReplay] 提示条提层级失败（不影响功能）："
                    + ex.GetType().Name + ": " + ex.Message);
                _raised = true;
            }
        }

        /// <summary>
        /// 一句话警告版。用于"操作没生效"这类必须让玩家看见的情况。
        /// </summary>
        internal static void Warn(string key, string detail = null)
            => Say(key, detail, Level.Warning, extra: 2f);
    }
}
