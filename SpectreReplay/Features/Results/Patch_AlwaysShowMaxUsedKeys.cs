using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SpectreReplay.Features.Results;

/// <summary>
/// 结算详情里「最大同时按键数」默认只在超过 10 键时显示。把那个阈值比较
/// 改成恒真，让任何一局都显示这一行。
///
/// <para><b>为什么按字段名匹配，而不是按上游的 <c>ldc.i4.s 10</c>。</b>
/// r150 的 <c>DetailedResults.GenerateResults</c> 里还有别的 10（HitMargin
/// 枚举数组初始化），只认常量会把不相干的比较改坏。这里要求三条连续指令：
/// <c>ldfld scrController::maximumUsedKeys</c> → <c>ldc.i4.s 10</c> →
/// <c>ble</c>，只改中间的常量为 -1（最大键数不会为负，于是条件恒假、
/// 分支永不跳过显示）。</para>
///
/// <para><b>没命中必须能看见。</b>游戏改 IL 后这里会记一条警告；不能静默
/// 变成「开关打开但结算没变化」。</para>
/// </summary>
internal static class Patch_AlwaysShowMaxUsedKeys
{
    [SpectrePatch(typeof(DetailedResults), "GenerateResults", PatchType.Transpiler,
        Feature = FeatureKeys.AlwaysShowMaxUsedKeys)]
    internal static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        int patched = 0;
        for (int i = 0; i + 2 < list.Count; i++)
        {
            if (!(list[i].operand is FieldInfo field)
                || field.Name != "maximumUsedKeys"
                || field.DeclaringType != typeof(scrController))
                continue;
            if (!(list[i + 1].operand is sbyte threshold)
                || threshold != 10)
                continue;

            // 原地只改操作数：把阈值 10 变成 -1。保留原 opcode（Ldc_I4_S）
            // 与该指令上的 label/exception block，最大键数不会为负，条件恒假。
            list[i + 1].operand = (sbyte)-1;
            patched++;
            break;
        }

        if (patched == 1)
            Debug.Log("[SpectreReplay] 结算最大按键数：已移除 >10 才显示的条件");
        else
            Debug.LogWarning("[SpectreReplay] 结算最大按键数未生效："
                + "DetailedResults.GenerateResults 的 maximumUsedKeys 比较形状变了");
        return list;
    }
}
