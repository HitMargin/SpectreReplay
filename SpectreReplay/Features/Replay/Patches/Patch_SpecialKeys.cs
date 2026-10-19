// 特殊按键过滤（移植自上游 Spectre 的 Patch_AllowSpecialKeys.cs）。
//
// ── 这个功能解决什么 ─────────────────────────────────────────
// ADOFAI 把一部分按键当作"不该计入按键数"的特殊键：`RDInputType_Keyboard.CountSpecialInput`
// 返回的就是这些（F1~F12、Alt、Win、Tab、End、Print 等）。游戏把它们从按键计数里
// 剔掉，理由是"这些多半是误触或截图键"。
//
// 但对回放/录制来说这个假设是错的：玩家**有意**用 F 键或 Tab 玩谱面时，这些按键
// 一个都不会进回放——录下来的东西按不出原效果，而界面上完全看不出为什么。
// 上游的做法是让玩家勾选"这些特殊键里，哪几个我确实在用"，勾了就当作普通按键。
//
// ── 两个接入点，缺一不可 ─────────────────────────────────────
//   · RDInputType_Keyboard.CountSpecialInput      —— 轮询输入路径
//   · RDInputType_AsyncKeyboard.GetSpecialInput   —— 异步输入路径
// 只挂一个的话，玩家切了输入模式另一半就失效，而现象（"某些键还是录不到"）
// 与"根本没实现"完全一样。
//
// ── 改动的是"谁算特殊键"的判定，不是按键状态本身 ──────────────
// 两个目标方法返回的是**这次哪些特殊键被按下了**的列表；调用方
// （`RDInputType_Keyboard.MainIgnoreActive` / `RDInputType_AsyncKeyboard` 的同类
// 逻辑）拿到后逐条把该键从本帧按键集里移除。所以从返回列表里删掉一条 = 该键
// 不再被剔除，会正常计入按键数并随后被我们的录制路径采到。
//
// **绝不能反过来往列表里加**：调用方是在 `keys` 上做 Remove，加进去只是被删掉，
// 看起来一样；但同一个列表还有 `Cancel()` 那条路径（Escape），一旦允许加入，
// 玩家就能把 Esc 也"放开"，而 Esc 是取消键——回放里卡一个 Esc 会把进关流程
// 打断。候选集因此刻意不含 Escape（见 Pages 的候选常量与本文件的两处守卫）。
//
// ── 性能：这条路径在**每帧输入处理**上 ──────────────────────
// 照搬的写法每个按键各调一次 `KeyCode.ToString()` / `label.ToString()`——枚举
// ToString 每次分配一个字符串，而 RemoveAll 又对列表里每个元素各调一次谓词。
// 这里改成"先把设置解析成可比较的值，再按值比较"：稳态零分配。
// 用签名（哈希无序聚合 + 元素个数）判断设置有没有变，变了才重建。
//
// ── Feature 门控：与页面上的勾选是两套东西 ───────────────────
// 设置里勾了键 ≠ 补丁挂着。补丁挂不挂由 `FeatureKeys.SpecialKeys` 决定
// （Main 里注册为 `ReplayOn && HasSpecialKeys()`）。没勾任何键时补丁不挂，
// 是**故意的**：这条路径每帧都跑，没勾键的玩家不该为一个用不到的功能付出
// 每帧一次委托调用。⇒ 想改门控语义时先看 Main 的注册行。

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
        // **早退判据必须是解析后的集合，不能是设置里的原始条数。**
        // 设置里可能只有 "Escape" 或一个已改名的非法名：那时 `_allowedCodes.Count == 0`，
        // 提前返回与继续跑结果一样；但如果先按**原始条数**早退，就会让
        // "设置非空、可解析集合为空" 这种情形整段跳过——玩家看到的是"我勾了却没用"。
        EnsureCodes();
        if (_allowedCodes.Count == 0 || __result == null || __result.Count == 0)
            return;
        // 按枚举值比较，零字符串分配
        __result.RemoveAll(k => _allowedCodes.Contains(k));
    }
}

internal static class Patch_AsyncSpecialKeys
{
    // AsyncKeyCode.label 是 SkyHook.KeyLabel。我方已引用 SkyHook.Unity，所以
    // **可以直接用 KeyLabel 类型**（上游注释说它不可见，是因为它只从 label 字段
    // 间接暴露；实测 KeyLabel 是 SkyHook 里的公开枚举，见 Assembly-CSharp 的
    // AsyncKeyCode.label : SkyHook.KeyLabel）。
    //
    // 即便如此仍保留"名字集合 + 值→名缓存"两层：设置里存的是**字符串**
    // （跨版本稳定，游戏加枚举值不会让旧配置失效），而 RemoveAll 的谓词每帧
    // 对每个元素调用一次——按名字比字符串比按枚举值比整数贵，所以把
    // 枚举值 → 名字 的转换缓存起来，每个不同 label 只 ToString 一次。
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

    [SpectrePatch(typeof(RDInputType_AsyncKeyboard), "GetSpecialInput", PatchType.Postfix,
        Feature = FeatureKeys.SpecialKeys)]
    internal static void Postfix(List<AsyncKeyCode> __result)
    {
        EnsureLabels();
        if (_allowedNames.Count == 0 || __result == null || __result.Count == 0)
            return;
        // 名字只在首次遇到该 label 时算一次；之后是纯字典/哈希查找，零分配
        __result.RemoveAll(k => _allowedNames.Contains(LabelName(in k)));
    }
}