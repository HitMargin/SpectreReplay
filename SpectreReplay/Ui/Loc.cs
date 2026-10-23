using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 界面文案。14 种语言，按游戏语言自动选。
    ///
    /// **文案数据在 <see cref="LocData"/>，本文件只放选择逻辑。** 分开是因为
    /// 数据有 14×325 条，混在一起会让这段逻辑（选语言、回退链）淹没在几千行
    /// 字面量中间——而它恰恰是最需要被读懂的部分：漏一种语言、回退链断一级，
    /// 都不报错，只表现为"界面上出现了另一种语言"。
    ///
    /// 语言集合与上游 Spectre 的 <c>LocalizationManager.langlist</c> **完全一致**
    /// （zh / zh-t / en / ko / ja / es / pt / fr / de / pl / cs / ro / ru / vi）。
    /// </summary>
    internal static class Loc
    {
        internal const string Brand = "Spectre Replay";

        internal static string Hint => T("ui.hint");

        private static readonly Dictionary<SystemLanguage, string> CodeByLanguage =
            new Dictionary<SystemLanguage, string>
            {
                { SystemLanguage.English, "en" },
                { SystemLanguage.Korean, "ko" },
                { SystemLanguage.ChineseSimplified, "zh" },
                { SystemLanguage.ChineseTraditional, "zh-t" },
                { SystemLanguage.Spanish, "es" },
                { SystemLanguage.Portuguese, "pt" },
                { SystemLanguage.Japanese, "ja" },
                { SystemLanguage.Polish, "pl" },
                { SystemLanguage.Russian, "ru" },
                { SystemLanguage.Romanian, "ro" },
                { SystemLanguage.Vietnamese, "vi" },
                { SystemLanguage.French, "fr" },
                { SystemLanguage.Czech, "cs" },
                { SystemLanguage.German, "de" },
            };

        internal static string CurrentLanguage = "en";

        /// <summary>
        /// 按游戏的语言设置选界面语言。游戏把语言放在
        /// <c>RDString.language</c>（public static SystemLanguage）。
        /// 面板每次打开都调一次：玩家在设置里换语言后，模组界面跟着变。
        ///
        /// 游戏语言不在 14 种里（荷兰语、芬兰语…）时回落到英文——那种情况下
        /// 界面语言的正确期望就是"用通用语"，而不是"猜一个最接近的"。
        /// </summary>
        internal static void RefreshLanguage()
        {
            try
            {
                if (CodeByLanguage.TryGetValue(RDString.language, out string code))
                {
                    CurrentLanguage = code;
                }
                else
                {
                    CurrentLanguage = "en";
                }
            }
            catch
            {
                // 游戏还没初始化：保持英文默认。**不要在这里猜"用户是中文用户"**，
                // 理由见 CurrentLanguage 的注释。
                CurrentLanguage = "en";
            }
        }

        /// <summary>
        /// 取文案。回退链：当前语言 → 英文 → 中文 → 键名。
        ///
        /// 英文排在中文前面，是因为这张表里**没有一种语言的人看不懂英文**，
        /// 而看得懂中文的只是其中一部分。中文放在最后一级，是因为漏翻
        /// 最可能漏在别的语言上：作者母语是中文，中文表几乎总是最全的那一份，
        /// 用它兜底最有可能真的兜住。
        ///
        /// 键名是最后的兜底，只该出现在"真的漏了"的时候——把
        /// <c>record.autosave</c> 这种东西露给玩家是最糟的形态。
        /// </summary>
        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            if (LocData.Languages.TryGetValue(CurrentLanguage, out var table)
                && table.TryGetValue(key, out string val))
            {
                return val;
            }
            if (LocData.Languages.TryGetValue("en", out var en) && en.TryGetValue(key, out string enVal))
            {
                return enVal;
            }
            if (LocData.Languages.TryGetValue("zh", out var zh) && zh.TryGetValue(key, out string zhVal))
            {
                return zhVal;
            }
            return key;          // 全都漏了才露键名——那是真漏
        }

        /// <summary>
        /// 取文案并填 {0} 之类的占位符。
        ///
        /// 直接 string.Format：文案里有 {0} 是我们自己写的，不含用户输入，
        /// 真出了 FormatException 就退回原文——宁可显示 "{0}"，
        /// 也不能让整个界面在 OnGUI 里抛异常（UMM 会吞掉异常，
        /// 玩家的表现是"面板卡住不动"，比报错更难查）。
        /// </summary>
        internal static string F(string key, params object[] args)
        {
            string t = T(key);
            if (args == null || args.Length == 0 || t.IndexOf('{') < 0)
            {
                return t;
            }
            try
            {
                return string.Format(t, args);
            }
            catch
            {
                return t;
            }
        }
    }
}
