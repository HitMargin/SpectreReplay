// Loc.cs 的最小桩件。
//
// 为什么单独放一个文件：Loc.cs 依赖游戏的 RDString.language 判断语言，
// 而整套 Unity 桩件（Stubs.cs）是为已删的自建 Canvas UI 建的，随那套
// UI 一起删掉了。
//
// 但文案表本身**必须继续验**——漏一个键，界面上就是空白或原始键名，
// 而这类问题只在玩家切到另一种语言时才暴露。所以这里只补回 Loc.cs 真正
// 用到的那两个类型，而不是把 24KB 的 RectTransform 桩件拖回来。
//
// 签名照抄游戏的真实源码（adofai-libs\source\Assembly-CSharp\RDString.cs）：
// RDString 是**全局命名空间**下的静态类，language 是**字段**不是属性。
// 这两处一开始都写错了（放进 ADOFAI 命名空间、写成了属性），
// 结果 Loc.cs 的 RDString 和 SystemLanguage 都找不到——
// 桩件跟真实 API 对不上时，编译错误反而是最好的提示。
//
// **枚举值本身也必须与游戏一致，否则闸门会有一种测不出来的假绿。**
// 2026-10-19 实测到过：这里原先写 ChineseSimplified = 41、Traditional = 42，
// 而游戏里是 40 和 41（Chinese 也不是 36 而是 6）。桩件里的错值只要
// 与 Loc.cs 的判断常量**同名**，离线套件就照样全绿——它俩一起错，
// 自洽了。真实游戏里则表现为「简中玩家看到繁中、繁中玩家落回英文」，
// 而离线套件永远发现不了。下面的值逐个对照过
// UnityEngine.CoreModule.dll 的 UnityEngine.SystemLanguage 枚举。
//
// 判据 ⑲a 会重新解析游戏 dll 里那份枚举，与本文件逐项比对。
namespace UnityEngine
{
    /// <summary>Unity 的语言枚举。Loc.cs 拿它跟 RDString.language 比。</summary>
    public enum SystemLanguage
    {
        // **只列 Loc.CodeByLanguage 真正引用的那 14 种。**
        //
        // 多列没用到的项是有害的：上面那段注释说"桩件值必须与游戏真值一致"，
        // 而每多一个手写常量，就多一处会抄错的地方，却没有任何判据会因此变红。
        // 2026-10-19 实测过——我顺手补了 Italian = 16（真值 21）、
        // Dutch = 9（真值也 9，侥幸对）和 Finnish = 13（真值也 13），
        // 判据 ⑲a 当场抓住了 Italian。删掉比改对更省事：它根本不在映射里。
        //
        // 数值以游戏 UnityEngine.CoreModule.dll 为准，逐项核对过。
        Czech = 7,
        German = 15,
        English = 10,
        Spanish = 34,
        French = 14,
        Japanese = 22,
        Korean = 23,
        Polish = 27,
        Portuguese = 28,
        Romanian = 29,
        Russian = 30,
        Vietnamese = 39,
        Chinese = 6,
        ChineseSimplified = 40,
        ChineseTraditional = 41,
    }
}

/// <summary>
/// 游戏的语言设置。真实类是全局命名空间下的静态类
/// （见 adofai-libs\source\Assembly-CSharp\RDString.cs，无 namespace 声明）。
/// </summary>
public static class RDString
{
    /// <summary>
    /// 由各检查项设置，用来验不同语言下的文案完整性。
    /// 真实类型里这是一个**字段**（public static SystemLanguage language）。
    /// </summary>
    public static UnityEngine.SystemLanguage language
        = UnityEngine.SystemLanguage.ChineseSimplified;
}