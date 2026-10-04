using UnityEngine;
internal static class JudgeTextDefaults
{
    internal static readonly Color[] DefaultColors =
    {
        new Color(1f, 0f, 0f, 1f),                    // TooEarly
        new Color(1f, 0.436146f, 0.305882f, 1f),      // VeryEarly
        new Color(0.627451f, 1f, 0.305882f, 1f),      // LittleEarly
        new Color(0.376471f, 1f, 0.306604f, 1f),      // Perfect
        new Color(0.627451f, 1f, 0.305882f, 1f),      // LittleLate
        new Color(1f, 0.435294f, 0.305882f, 1f),      // VeryLate
        new Color(1f, 0f, 0f, 1f),                    // TooLate
        new Color(0f, 1f, 0.930130f, 1f),             // Multipress
        new Color(0.851175f, 0.345912f, 1f, 1f),      // Fail (Miss/Overload/OverPress)
        Color.gray                                    // Auto
    };
}

// 拦截本地化查询，替换判定文本（游戏内飘字、结算界面等所有 HitMargin.* 词条）
