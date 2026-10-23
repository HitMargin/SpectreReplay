// 特殊按键过滤（移植自上游 Spectre 的 Patch_AllowSpecialKeys.cs）。
//
// ADOFAI 认为 F1~F12、Alt、Win、Tab、End、Print 这类键多半是误触，不计入按键数。
// 但玩家有意用它们打谱时，这些键就录不进回放。这里让玩家勾选哪些确实在用，
// 勾了就按普通按键处理。
//
// 两个接入点缺一不可，分别对应轮询与异步输入：
//   · RDInputType_Keyboard.CountSpecialInput
//   · RDInputType_AsyncKeyboard.GetSpecialInput
//
// 目标方法返回的是「这次哪些特殊键被按下了」，调用方会把这些键从本帧按键集里
// 移除。因此从这个结果里删掉一条 = 该键不再被剔除。
//
// 异步侧有两个版本形状（r150 改了签名），故拆成 MaxVersion=149 / MinVersion=150
// 两条：
//   · Keyboard.CountSpecialInput     返回 List<KeyCode>（两版一致）
//   · AsyncKeyboard.GetSpecialInput  r149- 返回 List<AsyncKeyCode>
//                                    r150+ void + 调用方传入 keys 缓冲
//
// 不能反过来往列表里加：调用方在 `keys` 上做 Remove，加进去只会被删掉；而同一个
// 列表还用于 Cancel()（Escape），允许加入会让 Esc 也被放开，回放里卡一个 Esc
// 会打断进关流程。候选集因此不含 Escape。
//
// 这条路径在每帧输入处理上，所以把设置解析成可比较的值再按值比较，稳态零分配；
// 用签名（哈希聚合 + 元素个数）判断设置是否变化，变了才重建。
//
// 补丁是否挂载由 FeatureKeys.SpecialKeys 决定（Main 里注册为
// ReplayOn && HasSpecialKeys()）。没勾任何键时不挂载，避免为用不到的功能
// 付每帧一次委托调用。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class Patch_KeyboardSpecialKeys
{
    private static HashSet<KeyCode> _allowedCodes = new();
    private static int _codesSig = int.MinValue;
    private static int _codesCount = -1;

    /// <summary>
    /// 把设置里的枚举名解析成 <see cref="KeyCode"/> 集合。
    ///
    /// **签名口径**：HashSet 遍历顺序不保证稳定，所以用"各元素哈希的无序聚合"
    /// （异或 + 求和）再叠上元素个数——等长但换了键也会改变签名。
    ///
    /// **三道护栏，每道都对应一种"玩家手改配置"能造出来的坏值：**
    ///   ① `Enum.IsDefined`——`Enum.TryParse("99", out code)` 会**成功**并给出
    ///      `(KeyCode)99` 这种越界值（EnumParseGuardCheck 记的就是这条）；
    ///   ② `Escape`——它是同一个列表里的取消键，放开会打断进关流程；
    ///   ③ `None`——`KeyCode.None` 在 `Input.GetKeyDown` 上恒为假，放进来无害
    ///      但会让玩家以为"我勾了 None"，属于噪音。
    ///
    /// 解析失败一律**跳过该项、不抛**：这条路径每帧都跑，抛异常会打断整条
    /// 输入处理（上游注释同样强调了这一点）。
    /// </summary>
    private static void EnsureCodes()
    {
        var names = Settings.AllowedKeyboardSpecialKeys;
        int xor = 0, sum = 0;
        foreach (string name in names)
        {
            int h = name != null ? name.GetHashCode() : 0;
            xor ^= h;
            sum = unchecked(sum + h);
        }
        int sig = unchecked(xor * 397 ^ sum);
        if (sig == _codesSig && names.Count == _codesCount)
            return;

        var set = new HashSet<KeyCode>();
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            try
            {
                if (!Enum.TryParse(name, ignoreCase: true, out KeyCode code))
                    continue;
                if (!Enum.IsDefined(typeof(KeyCode), code))
                    continue;
                if (code == KeyCode.Escape || code == KeyCode.None)
                    continue;
                set.Add(code);
            }
            catch { }
        }
        _allowedCodes = set;
        _codesSig = sig;
        _codesCount = names.Count;
    }

    [SpectrePatch(typeof(RDInputType_Keyboard), "CountSpecialInput", PatchType.Postfix,
        Feature = FeatureKeys.SpecialKeys)]
    internal static void Postfix(List<KeyCode> __result)
    {
        EnsureCodes();
        if (_allowedCodes.Count == 0 || __result == null || __result.Count == 0)
            return;
        // 按枚举值比较，零字符串分配
        __result.RemoveAll(k => _allowedCodes.Contains(k));
    }
}

internal static class Patch_AsyncSpecialKeys
{
    private static HashSet<string> _allowedNames = new(StringComparer.Ordinal);
    private static int _labelsSig = int.MinValue;
    private static int _labelsCount = -1;

    private static readonly Dictionary<int, string> LabelNames = new();

    private static void EnsureLabels()
    {
        var names = Settings.AllowedAsyncSpecialKeys;
        int xor = 0, sum = 0;
        foreach (string name in names)
        {
            int h = name != null ? name.GetHashCode() : 0;
            xor ^= h;
            sum = unchecked(sum + h);
        }
        int sig = unchecked(xor * 397 ^ sum);
        if (sig == _labelsSig && names.Count == _labelsCount)
            return;

        // 与键盘侧同一套护栏：Escape 是取消键、Unknown 匹配不上任何真实按下
        // （见上面的长注释）。这里按名字挡，因为 KeyLabel 的成员名由游戏侧决定，
        // 我们不引它的枚举值来做强类型判断（那会把"游戏加枚举值"变成编译期风险）。
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (string.Equals(name, "Escape", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                continue;
            set.Add(name);
        }
        _allowedNames = set;
        _labelsSig = sig;
        _labelsCount = names.Count;
    }

    /// <summary>label 枚举值 → 名字，按值缓存（枚举 GetHashCode 返回底层值，同值必同键）。</summary>
    private static string LabelName(in AsyncKeyCode code)
    {
        int key = code.label.GetHashCode();
        if (LabelNames.TryGetValue(key, out string cached))
            return cached;
        string name;
        try { name = code.label.ToString(); }
        catch { name = string.Empty; }
        LabelNames[key] = name;
        return name;
    }

    // 版本分叉（r150 改了签名）：
    //   r141–r149  private List<AsyncKeyCode> GetSpecialInput()
    //   r150+      private void GetSpecialInput(List<AsyncKeyCode> keys)
    //
    // 必须拆两条：`__result` 在 void 目标上注不进值（Harmony 静默给默认值），
    // 而 r150+ 的 `keys` 形参在旧版不存在（DisambiguateByPatchParams 按名匹配不上）。
    // 与 Patch_Hit 同一模式：MinVersion/MaxVersion 各钉一半，门控跳过的那条不算失败。

    // r141–r149：返回列表 —— 与键盘侧同款 `__result` 写法
    [SpectrePatch(typeof(RDInputType_AsyncKeyboard), "GetSpecialInput", PatchType.Postfix,
        Feature = FeatureKeys.SpecialKeys, MaxVersion = 149)]
    internal static void PostfixOld(List<AsyncKeyCode> __result)
    {
        EnsureLabels();
        if (_allowedNames.Count == 0 || __result == null || __result.Count == 0)
            return;
        // 名字只在首次遇到该 label 时算一次；之后是纯字典/哈希查找，零分配
        __result.RemoveAll(k => _allowedNames.Contains(LabelName(in k)));
    }

    // r150+：void + `keys` 形参，直接过滤调用方传进来的缓冲。方法内先 Clear 再填充，
    // Postfix 跑完时里面就是这次按下的特殊键全集。
    [SpectrePatch(typeof(RDInputType_AsyncKeyboard), "GetSpecialInput", PatchType.Postfix,
        Feature = FeatureKeys.SpecialKeys, MinVersion = 150)]
    internal static void PostfixNew(List<AsyncKeyCode> keys)
    {
        EnsureLabels();
        if (_allowedNames.Count == 0 || keys == null || keys.Count == 0)
            return;
        keys.RemoveAll(k => _allowedNames.Contains(LabelName(in k)));
    }
}