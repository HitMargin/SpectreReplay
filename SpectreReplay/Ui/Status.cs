using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

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
        /// 弹一条提示。
        ///
        /// 全部包在 try 里：Notification.instance 会去 Instantiate 预制体，
        /// 而预制体所在的资源组在某些时机可能还没就绪（主菜单刚起来、
        /// 场景切换中）。提示失败绝不能把调用方（录制/渲染的收尾逻辑）
        /// 一起带崩——那会把"出片成功"变成"出片失败"。
        /// </summary>
        internal static void Say(string key, string detail = null,
            Level level = Level.Info, float seconds = 4f)
        {
            try
            {
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
                // 点它没有任何后续动作，所以清掉上一个提示留下的监听器。
                // 不清的话玩家点了会触发上一个提示的回调（可能是"跳到关卡选择"）。
                n.button.onClick.RemoveAllListeners();
                n.icon.sprite = level == Level.Warning ? n.warningIcon : n.completeIcon;
                n.icon.color = level == Level.Warning
                    ? new Color(1f, 0.2f, 0.2f)
                    : new Color(0.2f, 0.7215686f, 0.3921569f);
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
            => Say(key, detail, Level.Warning, 5f);
    }
}
