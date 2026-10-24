using System;
using System.Reflection;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 把回放按键状态桥接给按键显示类模组（KeyViewer 系）。
///
/// **为什么需要这一层。** 回放的按键有两条腿：
///   · 游戏判定 ← <see cref="ManagedInput"/> 覆盖 RDInput 读数（Patch_RDInput）
///   · 事件管线 ← <see cref="AsyncInputInjector"/> 合成 SkyHook 事件
/// 两条都**不经过 Unity 的 Input**。而 KeyViewer 系的读数是
/// <c>KeySource.GetKey(code) => ShimGetKey(code) || Input.GetKey(code)</c>
/// （JipperKeyViewer/KeySource.cs:95）——只有 OS 输入或**它自己的 KeyInput.GetKey**
/// 能看到按键。回放期间两者都是空的，于是键位显示器一片漆黑，
/// 而游戏里其实一切正常。
///
/// **参考实现就是这么做的**（ADOFAIMacro 的说明，以及 Spectre 原版）：
/// 回放引导器检测名为 KeyViewer 的模组，对
/// <c>KeyViewer.Core.Input.KeyInput.GetKey</c> 打 Harmony 补丁注入回放状态。
/// 本类补上这一环。
///
/// **只做 OR，不做替换**：玩家自己按下的物理键仍然要为真，否则回放期间
/// 手动操作会被显示层吞掉。
///
/// 目标类型在编译期不存在（是别的模组），只能运行时反射解析 +
/// <see cref="PatchShared.SpectrePatchHub.PatchManual"/> 手动挂载。
/// </summary>
internal static class KeyViewerBridge
{
    private const string TargetAssembly = "KeyViewer";
    private const string TargetType = "KeyViewer.Core.Input.KeyInput";
    private const string TargetMethod = "GetKey";
    private const string PatchId = "keyviewer-getkey";

    private static bool _installed;
    private static bool _attemptedThisSession;

    /// <summary>
    /// 补丁被整体卸载后复位「已挂接」状态。
    ///
    /// **为什么必须有这一步。** 挂上去的是 PatchManual（owner 是我们），
    /// SpectrePatchHub.UnpatchAll 会把它卸掉，但这里的 _installed 是普通静态字段，
    /// 不随卸载翻假。于是「关一次模组再打开」之后：TryInstall 见到 _installed 为真
    /// 直接返回，桥接补丁**永远不会重新挂上**，键位显示器整局全黑，而且一条日志
    /// 都不会有（它以为早已装好）。模组每次启用时清一次即可。
    /// </summary>
    internal static void Reset()
    {
        _installed = false;
        _attemptedThisSession = false;
    }

    /// <summary>
    /// 尝试挂载桥接补丁。找不到 KeyViewer 时静默返回——那是正常情况。
    ///
    /// **只有「程序集确实存在」时才会记下「本次会话已尝试」。**
    ///
    /// 本方法在每次回放开始时调用（ReplayPlayer.Start），不是在模组初始化时。
    /// KeyViewer 可能后加载，若在「程序集还没出现」时就把会话标记置真，
    /// 之后的每次回放都会直接 return，键位显示器永久失联且没有日志。
    ///
    /// 反过来，程序集已在但类型/方法对不上（版本不兼容）就记下：那种情况重试
    /// 也不会变，只是每帧重复反射。
    /// </summary>
    internal static void TryInstall(bool force = false)
    {
        if (_installed) return;
        if (_attemptedThisSession && !force) return;

        try
        {
            Assembly asm = FindAssembly(TargetAssembly);
            if (asm == null) return;   // 模组还没加载：不记标记，下次回放再试

            Type type = asm.GetType(TargetType, throwOnError: false);
            if (type == null) return;  // 同上

            // 从这里开始，程序集确实在：无论成败都记下，避免每次回放重复反射
            _attemptedThisSession = true;

            MethodInfo target = type.GetMethod(
                TargetMethod,
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(KeyCode) },
                null);
            if (target == null) return;

            MethodInfo patch = typeof(KeyViewerBridge).GetMethod(
                nameof(GetKeyPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (patch == null) return;

            PatchShared.SpectrePatchHub.PatchManual(
                target, patch, PatchShared.PatchType.Postfix, PatchId,
                FeatureKeys.Replay);

            _installed = true;
            Debug.Log("[SpectreReplay] 已为 KeyViewer 挂接回放按键桥接"
                + "（KeyViewer.Core.Input.KeyInput.GetKey）");
        }
        catch (Exception ex)
        {
            // 别的模组的类型解析失败绝不能让回放本身出问题：这只是显示层
            Debug.LogWarning("[SpectreReplay] KeyViewer 桥接挂载失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 在已加载的程序集里按名字找。用 <c>AppDomain</c> 而不是
    /// <c>Assembly.Load</c>：目标模组已经加载过，再 Load 一次会得到第二个副本，
    /// 打上去的补丁对真正在用的那份无效。
    /// </summary>
    private static Assembly FindAssembly(string name)
    {
        Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].GetName().Name == name) return all[i];
        }
        return null;
    }

    /// <summary>
    /// 补丁体。KeyViewer 的 <c>GetKey(KeyCode)</c> 返回该键此刻是否按住。
    ///
    /// **用 OR 合并**而不是赋值：物理键盘照常工作，回放按键只是额外的真值来源。
    /// </summary>
    private static void GetKeyPostfix(ref bool __result, KeyCode code)
    {
        if (__result) return;                       // 物理键已为真，无需覆盖
        if (!ManagedInput.Active) return;           // 只在回放期间生效

        // Unity KeyCode 与 Win32 VK 只在少数键上同值（Space/Esc/Enter），
        // 字母键与方向键全部错位，所以必须先换算。
        // Held[] 存的是映射后的键码，这里不能再查 Settings.key_code_convert，
        // 否则同一张表被当成反向表用，映射过的那颗键会顶着原键码亮。
        ushort index = ToNativeVk(code);
        if (index != 0 && ManagedInput.IsHeldVk(index)) __result = true;
    }

    /// <summary>KeyCode → 原生虚拟键码（**不查重映射表**）；无法换算时返回 0。</summary>
    private static ushort ToNativeVk(KeyCode code)
    {
        try
        {
            SkyHook.KeyLabel label = SkyHook.SkyHookKeyMapper.UnityKeyToSkyHookKey(code);
            if (label == SkyHook.KeyLabel.Unknown) return 0;
            return SkyHook.SkyHookKeyMapper.KeyLabelToNativeKeyCode(label);
        }
        catch
        {
            return 0;
        }
    }
}