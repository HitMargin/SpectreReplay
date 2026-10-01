// Loc.cs 的最小桩件。
//
// 为什么单独放一个文件：Loc.cs 依赖游戏的 RDString.language 判断语言，
// 而整套 Unity 桩件（Stubs.cs）是为已删的自建 Canvas UI 建的，随那套
// UI 一起删掉了。
//
// 但文案表本身**必须继续验**——漏一个键，界面上就是空白或原始键名，
// 而这类问题只在玩家切到英文时才暴露。所以这里只补回 Loc.cs 真正
// 用到的那两个类型，而不是把 24KB 的 RectTransform 桩件拖回来。
//
// 签名照抄游戏的真实源码（adofai-libs\source\Assembly-CSharp\RDString.cs）：
// RDString 是**全局命名空间**下的静态类，language 是**字段**不是属性。
// 这两处一开始都写错了（放进 ADOFAI 命名空间、写成了属性），
// 结果 Loc.cs 的 RDString 和 SystemLanguage 都找不到——
// 桩件跟真实 API 对不上时，编译错误反而是最好的提示。
namespace UnityEngine
{
    /// <summary>Unity 的语言枚举。Loc.cs 只拿它跟 RDString.language 比。</summary>
    public enum SystemLanguage
    {
        English = 10,
        Chinese = 36,
        ChineseSimplified = 41,
        ChineseTraditional = 42,
        Japanese = 22,
        Korean = 23,
    }
}

/// <summary>
/// 游戏的语言设置。真实类是全局命名空间下的静态类
/// （见 adofai-libs\source\Assembly-CSharp\RDString.cs，无 namespace 声明）。
/// </summary>
public static class RDString
{
    /// <summary>
    /// 由 LocCheck 设置，用来验两种语言下的文案完整性。
    /// 真实类型里这是一个**字段**（public static SystemLanguage language）。
    /// </summary>
    public static UnityEngine.SystemLanguage language
        = UnityEngine.SystemLanguage.ChineseSimplified;
}