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

        /// <summary>头部副标题。**走文案表**：原先写死成中文常量，英文界面下会直接显示
        /// 中文，而 Loc.cs 在硬编码检查里整文件豁免——这条永远不会被闸门抓到。
        /// 另外那句话本身也是错的：写的是 F9，而 F9 是「手动保存回放」的键，
        /// UMM 的开关窗口热键默认是 Ctrl+F10。</summary>
        internal static string Hint => T("ui.hint");

        /// <summary>
        /// 游戏语言 → 我们的语言代码。
        ///
        /// **为什么不复用 <c>LocalizationManager.langlist</c>：** 那张表在
        /// <c>SpectreReplay</c> 命名空间下，而本文件被链接进离线预览工程
        /// （<c>tools\UiPreview\UiPreview.csproj</c>）单独编译，那边没有
        /// LocalizationManager。直接引用会让预览器编译不过，而预览器编译不过时
        /// 闸门只会报"一条断言都没解析到"——看起来像套件崩了，不像"少链了一个文件"。
        ///
        /// 两张表的值必须一致（都对着游戏的 SystemLanguage 枚举）。判据 ⑲b 守这一点：
        /// 它会读出两张表逐项比对，改了一边忘了另一边会直接红。
        ///
        /// （枚举值以游戏 UnityEngine.CoreModule.dll 为准逐个核对过：
        ///   Czech=7、German=15、French=14、Japanese=22、Korean=23、Polish=27、
        ///   Portuguese=28、Romanian=29、Russian=30、Spanish=34、Vietnamese=39、
        ///   ChineseSimplified=40、ChineseTraditional=41、English=10。
        ///   注意 Chinese=6 是"中文（合体）"，与这两个不是同一个值。
        ///   预览桩件 LocStubs.cs 曾经把 ChineseSimplified 写成 41、Traditional 写成 42，
        ///   比真值各偏 1——那种错误在真实游戏里表现为"简中玩家看到繁中"，
        ///   而离线套件因为用的是同一份错误的桩，永远测不出来。）
        /// </summary>
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

        /// <summary>
        /// 当前界面语言代码（"zh" / "en" / "ja" …）。
        ///
        /// **默认英文而不是中文。** 这里曾经是 `UseChinese = true`，理由是
        /// "用户是中文用户"；但那句话只在作者自己的机器上成立。模组发出去之后，
        /// 一个德语玩家在游戏初始化完成前打开面板，会先看到一整屏中文——
        /// 而他没有任何办法让它变回去，除非去改游戏语言再改回来。
        /// 英文是这张表里最安全的默认：14 种语言里没有人完全看不懂它。
        /// </summary>
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
