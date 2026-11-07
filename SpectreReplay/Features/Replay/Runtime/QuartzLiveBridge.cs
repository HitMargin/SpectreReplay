using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 实时回放桥：把回放按键边沿直喂 Quartz KeyViewer（石英）。
///
/// **为什么要有它。** Quartz 雨滴的唯一活水是它自己打在
/// <c>SkyHookManager.HookCallback</c> 上的前缀（HookInput → HookKeys.KeyEvent
/// → KvInputQueue.Push），而回放期我们的 <see cref="Patch_MirrorEchoSuppressor"/>
/// 在同一个方法上吞掉除 Esc 外的全部事件。两个前缀都没写显式优先级，谁先跑
/// 取决于注册顺序——模组加载顺序、中途开关模组重注册都会翻：它先跑雨滴活，
/// 我们先跑它的前缀被短路、雨滴死。这就是「Windows API 对石英的 KV 有概率
/// 不生效」的根因（2026-11-07 实测：三个 KV 里只有石英不显示）。
///
/// **绕开顺序赌博**：Quartz 留了公共外部驱动 <c>KvExternalDriver</c>
/// （渲染模式已在用）。Begin 之后它的正常 Push 通道停用（<c>!externalDriver</c>
/// 门），队列唯一数据源就是我们——不受前缀顺序影响，Mirror 开关也不影响，
/// 还顺带消掉了 Mirror 开启时钩子回声造成的双份边沿。按住态走
/// <c>HookKeys.Register</c> 兜底源（Held() = Input.GetKey 优先、查不到再问
/// 注册源），Mirror 关着盒子也能亮。
///
/// **与渲染模式互斥**：渲染期 RenderOverlayCapture 用虚拟时钟接管同一个
/// 驱动，这里必须让路（VideoRender.Active 门），否则同一条边喂两遍。
/// </summary>
internal static class QuartzLiveBridge
{
    private const string SourceId = "spectrereplay";

    private static bool _attemptedThisSession;
    private static bool _begun;

    private static MethodInfo _kvBegin;
    private static MethodInfo _kvKey;
    private static MethodInfo _kvEnd;
    private static MethodInfo _kvRegister;
    private static MethodInfo _kvUnregister;
    private static readonly HashSet<ushort> _held = new HashSet<ushort>();

    /// <summary>
    /// 回放开始时调用：Quartz 在场就接管显示；不在场静默返回，下次回放再试
    /// （Quartz 可能后加载）。与 <see cref="KeyViewerBridge.TryInstall"/> 同一套
    /// 生命周期约定：只有「程序集在场但接口对不上」才记会话标记。
    /// </summary>
    internal static void TryInstall()
    {
        if (_begun) return;
        if (_attemptedThisSession) return;
        if (Features.VideoRender.VideoRender.Active) return;   // 渲染期归渲染桥管

        try
        {
            Type driver = AccessTools.TypeByName("Quartz.Features.KeyViewer.KvExternalDriver");
            Type hookKeys = AccessTools.TypeByName("Quartz.Game.HookKeys");
            if (driver == null || hookKeys == null) return;    // Quartz 未加载：下次回放再试

            MethodInfo begin = AccessTools.Method(driver, "Begin", new[] { typeof(Func<double>) });
            MethodInfo end = AccessTools.Method(driver, "End");
            _kvKey = AccessTools.Method(driver, "Key", new[] { typeof(KeyCode), typeof(bool) });
            _kvRegister = AccessTools.Method(hookKeys, "Register", new[]
            {
                typeof(string), typeof(Func<KeyCode, bool>), typeof(Func<KeyCode, bool>),
            });
            _kvUnregister = AccessTools.Method(hookKeys, "Unregister", new[] { typeof(string) });
            if (begin == null || end == null || _kvKey == null
                || _kvRegister == null || _kvUnregister == null)
            {
                // 接口形状变了：重试也不会变，只记一次，免得每局重复反射
                _attemptedThisSession = true;
                Debug.LogWarning("[SpectreReplay] Quartz 实时桥未生效：KvExternalDriver/"
                    + "HookKeys 接口形状变了（回放时石英 KV 将不显示回放键）");
                return;
            }

            _kvBegin = begin;
            _kvEnd = end;
            // 墙钟必须用 Stopwatch：Begin 的 Func 会在 Quartz 的钩子线程上被读，
            // Unity 的 Time API 只能在主线程碰。
            _kvBegin.Invoke(null, new object[]
            {
                (Func<double>)(() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency),
            });
            _kvRegister.Invoke(null, new object[]
            {
                SourceId,
                (Func<KeyCode, bool>)(code =>
                {
                    ushort vk = KeyViewerBridge.ToNativeVk(code);
                    return vk != 0 && ManagedInput.Active && ManagedInput.IsHeldVk(vk);
                }),
                (Func<KeyCode, bool>)(code => ManagedInput.Active),
            });
            _begun = true;
            Debug.Log("[SpectreReplay] Quartz 实时桥已接管：回放边沿与按住态直喂"
                + "（不依赖 Windows API 镜像，也不受钩子前缀顺序影响）");
        }
        catch (Exception ex)
        {
            // 显示层的任何异常都不能带偏回放本体
            Debug.LogWarning("[SpectreReplay] Quartz 实时桥挂载失败: " + ex.Message);
        }
    }

    /// <summary>喂一个回放按键边沿。渲染期让路（那边有自己的虚拟时钟驱动）。</summary>
    internal static void Edge(ushort vk, bool down)
    {
        if (!_begun || Features.VideoRender.VideoRender.Active) return;
        try
        {
            KeyCode key = Features.VideoRender.RenderOverlayCapture.QuartzKeyCode(vk);
            if (key == KeyCode.None) return;
            _kvKey.Invoke(null, new object[] { key, down });
            if (down) _held.Add(vk);
            else _held.Remove(vk);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] Quartz 实时桥按键注入失败: " + ex.Message);
        }
    }

    /// <summary>seek 重建前清一次按住态（随后 RebuildHeldInputs 会按新游标重喂）。</summary>
    internal static void ResetLive()
    {
        if (!_begun) return;
        foreach (ushort vk in new List<ushort>(_held))
            Edge(vk, false);
        _held.Clear();
    }

    /// <summary>
    /// 按托管输入的净按住态播种显示（开局对齐 / seek 重建的收尾步）。
    ///
    /// **为什么不把重建走的每条历史事件喂给队列。** 那样每条 down 都会 spawn
    /// 一滴雨，历史按键越多、开局那一帧的假雨越大（2026-11-07「刚开始播放
    /// 就一排键被按下」实测）。真相只是「此刻什么还按着」，所以等
    /// RebuildHeldInputs 把 Restore 全部走完、净态落定，再按 IsHeldVk 播种。
    /// </summary>
    internal static void SeedHeld()
    {
        if (!_begun) return;
        for (ushort vk = 1; vk < 256; vk++)
        {
            if (ManagedInput.IsHeldVk(vk) && !_held.Contains(vk))
                Edge(vk, true);
        }
    }

    /// <summary>回放结束：补发抬起、卸掉兜底源、把输入通道还给 Quartz。</summary>
    internal static void Shutdown()
    {
        if (!_begun) return;
        try
        {
            foreach (ushort vk in new List<ushort>(_held))
            {
                KeyCode key = Features.VideoRender.RenderOverlayCapture.QuartzKeyCode(vk);
                if (key != KeyCode.None)
                    _kvKey.Invoke(null, new object[] { key, false });
            }
            _kvEnd.Invoke(null, null);
            _kvUnregister.Invoke(null, new object[] { SourceId });
        }
        catch (Exception ex)
        {
            // 兜底清理失败只影响显示层，不该带偏 Stop 的其余步骤
            Debug.LogWarning("[SpectreReplay] Quartz 实时桥收尾失败: " + ex.Message);
        }
        _held.Clear();
        _kvBegin = null;
        _kvKey = null;
        _kvEnd = null;
        _kvRegister = null;
        _kvUnregister = null;
        _begun = false;
    }
}
