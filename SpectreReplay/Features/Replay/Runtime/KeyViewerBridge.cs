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
    /// 尝试挂载桥接补丁。找不到 KeyViewer 时静默返回——那是正常情况。
    ///
    /// **只有「程序集确实存在」时才会记下「本次会话已尝试」。**
    ///
    /// 这一点很关键：本方法在每次回放开始时调用（ReplayPlayer.Start），而不是在
    /// 模组初始化时。玩家可能先开一局回放、再让 KeyViewer 加载（模组管理器的手动
    /// 启用、或加载顺序靠后）。若在「程序集还没出现」时就把会话标记置真，那之后
    /// 的每一次回放都会直接 return，键位显示器永久失联且没有任何日志——
    /// 表现就是「以前能用，现在怎么都不亮」。
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

        // **不能把 KeyCode 直接当虚拟键码用。**
        // Unity 的字母键 Code 是小写 ASCII（KeyCode.A = 97），而 Win32 的 VK_A = 65；
        // 两者只对空格、方向键等少数键恰好相同，字母键全部错位——
        // 显示层会变成「按 A 亮 R」这种随机效果。
        //
        // 只需要做 **KeyCode → 原生 VK** 这一步：
        //   KeyCode →(UnityKeyToSkyHookKey)→ KeyLabel
        //           →(KeyLabelToNativeKeyCode)→ 原生 VK
        //
        // **绝不能在这里再过一次 Settings.key_code_convert。**
        // `ManagedInput.Held[]` 存的是**已经过映射**的键码（ReplayPlayer 里
        // `num = key_code_convert[ev.KeyCode]` 之后才 Note），显示层问的是
        // 「这颗物理键此刻亮不亮」，如果再查一次同一张表，就是把正向表当反向表用：
        // 映射 R→Space 时，问 R 得到 map[82]=32、`Held[32]` 为真 ⇒ **R 亮**；
        // 问 Space 得到 map[32]=32（未限制时）或 0（限制时）⇒ Space 反而按表外被吞。
        // 现象正是「我明明映射成了空格，回放却亮 R」；打开「只模拟列表中的键」后
        // 表外全填 0，错的这一颗成了唯一还亮的，看起来就像映射根本没生效。
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