using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// ReplayInput 外部驱动契约的通用推桥。
///
/// **这是一份跨模组约定，不是某一家 KV 的私有接口。** 同类键位显示模组
/// （RainingKeys 0.5.0 反编译实证，其余同类同理）普遍公开一个名为
/// <c>ReplayInput</c> 的静态类型，供回放模组按约定调用：
///   · <c>OnStartInputs()</c>   —— 清容器并按玩家配置重建，键全部置
///     <c>controlled = true</c>（物理轮询停止，只认外部调用）；
///   · <c>OnKeyPressed/OnKeyReleased(KeyCode)</c> —— 容器按 Unity KeyCode
///     字典查键，查不到（没配置的键）静默忽略；
///   · <c>OnEndInputs()</c>     —— 清容器并还原玩家的普通（物理轮询）键位。
/// 所以这些 KV 既看不到 Unity Input 也看不到 SkyHook 广播，拉取接口
/// （<see cref="SpectreReplay.API.SpectreAPI"/>）对它们也不适用——它们不会来查。
/// 唯一让它们亮起来的方式就是按这份契约推调用。
///
/// **发现方式：按类型名扫描，不绑程序集名。** 契约只约定「类型叫
/// <c>ReplayInput</c>」，不约定命名空间与程序集（RainingKeys 挂在
/// <c>RainingKeys.Bootstrap</c> 下，别家各有各的挂法）。所以每次解析遍历
/// 已加载程序集，凡名为 <c>ReplayInput</c> 且四个方法形状完整的类型
/// **全部**收编——同时装几个这类 KV 就亮几个。形状不完整的不驱动
/// （那不是在声明契约），但留一条日志。
///
/// **键码方向。** 回放内部状态是原生 VK（<see cref="ManagedInput"/> 的
/// <see cref="ManagedInput.IsHeldVk"/> 域），契约要 Unity KeyCode。
/// 正向换算 <see cref="KeyViewerBridge.ToNativeVk"/> 是 KeyCode→VK；这里走
/// SkyHook 的逆链 NativeKeyCodeToKeyLabel → SkyHookKeyToUnityKey，外加
/// VK_APPS=93 ↔ KeyCode.Menu 的互逆特判（SkyHook KeyLabel 缺 Menu）。
///
/// **失败策略与其它桥一致**：一个目标都没有时静默（没装此类 KV 是正常情况）；
/// 名字对上而形状不完整时告警一次；任何调用异常只记日志，绝不带偏回放本体。
/// </summary>
internal static class ReplayInputBridge
{
    /// <summary>契约类型名。只认名字与形状，不认程序集/命名空间。</summary>
    private const string DriverTypeName = "ReplayInput";

    /// <summary>一个已解析的驱动目标（某个 KV 模组暴露的 ReplayInput 类型）。</summary>
    private sealed class DriverTarget
    {
        internal string Origin;      // 诊断用：程序集名
        internal MethodInfo Start;   // OnStartInputs()
        internal MethodInfo End;     // OnEndInputs()（返回值约定为 false，忽略）
        internal MethodInfo Press;   // OnKeyPressed(KeyCode)
        internal MethodInfo Release; // OnKeyReleased(KeyCode)
        internal bool Active;        // 本目标当前处于受控会话（Start 已发、End 未发）
    }

    private static readonly List<DriverTarget> Targets = new List<DriverTarget>();

    // 已解析且至少命中一个目标：不再每局重扫（程序集集在会话内不变）。
    // 零命中时不置位——同类 KV 可能比我们晚加载，下一局回放再试。
    private static bool _resolved;

    /// <summary>
    /// 模组重新装配时复位。桥不是 Harmony 补丁（纯反射直调），不存在卸载问题，
    /// 但「已解析」的 MethodInfo 与各目标的会话状态在关开模组后应当重来一次，
    /// 与 <see cref="KeyViewerBridge.Reset"/> 同一纪律。
    /// </summary>
    internal static void Reset()
    {
        _resolved = false;
        Targets.Clear();
    }

    /// <summary>
    /// 回放开始（<see cref="ReplayPlayer.Start"/>）：让全部 ReplayInput 目标进入
    /// 受控模式。每次回放开始都重发 <c>OnStartInputs</c>——那份契约就是
    /// 「回放开始」的信号，重复调用是幂等的（清容器重建），也顺带修掉
    /// 「回放中途改过 KV 设置导致容器被重建回普通模式」的漂移。
    /// </summary>
    internal static void TryInstall()
    {
        if (!_resolved)
        {
            ResolveTargets();
            // 一个目标都没有：KV 还没加载或压根没装。不记解析完成，
            // 下一局回放再扫一遍（与 KeyViewerBridge 的重试策略同一取舍）。
            if (Targets.Count == 0) return;
            _resolved = true;
        }

        var started = new List<string>();
        foreach (DriverTarget t in Targets)
        {
            try
            {
                t.Start.Invoke(null, null);
                t.Active = true;
                started.Add(t.Origin);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] ReplayInput[" + t.Origin
                    + "] 进入受控模式失败: " + ex.Message);
            }
        }
        // **必须一次性列出全部目标名。**（2026-11-12 用户实测误诊：旧版用
        // _loggedFirst 只打第一个目标名，「扫描到 2 个驱动目标」后面跟一句
        // 单名日志，读起来就像 RainingKeys 没被接管。）每次回放开始都打
        // 全量名单，与 FastForward/首键注入的每局日志同一节奏。
        if (started.Count > 0)
        {
            Debug.Log("[SpectreReplay] 已接管 ReplayInput 键位显示（"
                + string.Join("、", started.ToArray()) + "）");
        }
    }

    /// <summary>
    /// 回放按键边沿（<see cref="ReplayPlayer.PumpInput"/>，与 Quartz 实时桥同点）。
    /// 只转发给受控会话中的目标；VK 换算不出 Unity KeyCode 的键直接丢弃
    /// （KV 侧对未配置键本来也是静默忽略）。换算一次、逐目标转发。
    /// </summary>
    internal static void Edge(ushort vk, bool pressed)
    {
        bool any = false;
        for (int i = 0; i < Targets.Count; i++)
        {
            if (Targets[i].Active) { any = true; break; }
        }
        if (!any) return;

        KeyCode code = ToUnityKey(vk);
        if (code == KeyCode.None) return;
        foreach (DriverTarget t in Targets)
        {
            if (!t.Active) continue;
            try
            {
                (pressed ? t.Press : t.Release).Invoke(null, new object[] { code });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] ReplayInput[" + t.Origin
                    + "] 按键转发失败 vk=" + vk + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 跳转/检查点开局（<see cref="ReplayPlayer.RebuildHeldInputs"/>）后的重播种。
    ///
    /// 这份契约没有「状态播种」接口，只有边沿。跳过段的历史边沿不能喂
    /// （与 Quartz「只喂净按住态」同一取舍——喂了就是开局一排雨），所以：
    /// 重发 <c>OnStartInputs</c> 把容器整体重建回未按下的干净受控态，
    /// 再按 <see cref="ManagedInput.SnapshotHeld"/> 的净按住态逐键补 Down。
    /// </summary>
    internal static void Reseed()
    {
        foreach (DriverTarget t in Targets)
        {
            if (!t.Active) continue;
            try
            {
                t.Start.Invoke(null, null);
                foreach (ushort vk in ManagedInput.SnapshotHeld())
                {
                    KeyCode code = ToUnityKey(vk);
                    if (code == KeyCode.None) continue;
                    t.Press.Invoke(null, new object[] { code });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] ReplayInput[" + t.Origin
                    + "] 重播种失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 回放结束（<see cref="ReplayPlayer.Stop"/>）：全部目标发 <c>OnEndInputs</c>
    /// 清容器并还原玩家的普通键位。收尾漏掉的话 KV 会卡在受控模式——
    /// 物理轮询已停，画面永远停在最后一次按键。
    /// </summary>
    internal static void Shutdown()
    {
        foreach (DriverTarget t in Targets)
        {
            if (!t.Active) continue;
            t.Active = false;
            try
            {
                t.End.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] ReplayInput[" + t.Origin
                    + "] 收尾失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 扫描已加载程序集，收编全部形状完整的 ReplayInput 类型。
    /// 名字对上但形状不完整的（声明了契约却缺方法）告警一次。
    /// </summary>
    private static void ResolveTargets()
    {
        Assembly[] all;
        try
        {
            all = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] ReplayInput 目标扫描失败: " + ex.Message);
            return;
        }

        for (int i = 0; i < all.Length; i++)
        {
            Assembly asm = all[i];
            string origin;
            try { origin = asm.GetName().Name; }
            catch { continue; }
            // 动态程序集/可抛 ReflectionTypeLoadException 的程序集：跳过即可
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            for (int k = 0; k < types.Length; k++)
            {
                Type type = types[k];
                if (type.Name != DriverTypeName) continue;

                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance;
                MethodInfo start = type.GetMethod("OnStartInputs", Any, null, Type.EmptyTypes, null);
                MethodInfo end = type.GetMethod("OnEndInputs", Any, null, Type.EmptyTypes, null);
                MethodInfo press = type.GetMethod("OnKeyPressed", Any, null, new[] { typeof(KeyCode) }, null);
                MethodInfo release = type.GetMethod("OnKeyReleased", Any, null, new[] { typeof(KeyCode) }, null);

                if (start != null && end != null && press != null && release != null)
                {
                    var t = new DriverTarget { Origin = origin, Start = start, End = end, Press = press, Release = release };
                    Targets.Add(t);
                    continue;
                }
                // 名字在、方法残缺：多半是同类 KV 的新/旧版本改了契约形状。
                // 至少有一个方法存在才告警——避免误伤「恰好同名」的无关类型。
                if (start != null || end != null || press != null || release != null)
                {
                    string missing = (start == null ? "OnStartInputs " : "")
                        + (end == null ? "OnEndInputs " : "")
                        + (press == null ? "OnKeyPressed " : "")
                        + (release == null ? "OnKeyReleased" : "");
                    Debug.LogWarning("[SpectreReplay] ReplayInput[" + origin + "] 形状不完整，缺 "
                        + missing + "（回放时该键位显示不跟随）");
                }
            }
        }
        if (Targets.Count > 0)
        {
            Debug.Log("[SpectreReplay] 扫描到 " + Targets.Count + " 个 ReplayInput 驱动目标");
        }
    }

    /// <summary>
    /// 原生 VK → Unity KeyCode：<see cref="KeyViewerBridge.ToNativeVk"/> 的逆运算。
    /// </summary>
    /// <remarks>
    /// 已知怪癖：SkyHook 的映射表把 LeftWindows/RightWindows/LeftMeta/RightMeta
    /// 四个键都映射到 <see cref="SkyHook.KeyLabel.Super"/>，反向只能取到表里
    /// 最后写入的那个（LeftMeta）——玩家配置 Super 系键时高亮可能落在另一颗
    /// Win 键上。Menu 同理是反向特判：KeyLabel 缺 Menu，VK_APPS=93 直取
    /// <see cref="KeyCode.Menu"/>，与正向的 93 特判互逆。
    /// </remarks>
    internal static KeyCode ToUnityKey(ushort vk)
    {
        try
        {
            SkyHook.KeyLabel label = SkyHook.SkyHookKeyMapper.NativeKeyCodeToKeyLabel(vk);
            if (label != SkyHook.KeyLabel.Unknown)
            {
                KeyCode code = SkyHook.SkyHookKeyMapper.SkyHookKeyToUnityKey(label);
                if (code != KeyCode.None) return code;
            }
        }
        catch
        {
            // 原生查询失败不致命：落到下面的特判与 None
        }
        // SkyHook KeyLabel 缺 Menu/VK_APPS；正向（KeyViewerBridge.ToNativeVk）
        // 对 KeyCode.Menu 特判返回 93，这里对 93 特判返回 KeyCode.Menu。
        if (vk == 93) return KeyCode.Menu;
        return KeyCode.None;
    }
}
