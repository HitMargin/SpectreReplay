using UnityEngine;
using TMPro;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 字体解析。
    ///
    /// TextMeshPro 的组件必须显式赋 font 才会渲染字形——没赋的 TMP_Text
    /// 在场景里表现为"组件在、字一个不出"。本工程所有文字都是
    /// AddComponent&lt;TextMeshProUGUI&gt;() 建出来的，所以必须在这里统一补上。
    ///
    /// 字体来源按优先级回退：
    ///   1. RDConstants.chineseFontTMPro —— 游戏自带的中日韩字体，含 CJK 字形
    ///   2. RDConstants.latinFontTMPro  —— 拉丁字体
    ///   3. Resources 里的 SourceHanSans —— 与 RDString 加载编辑器字体同一条路
    ///   4. TMP_Settings.defaultFontAsset —— TMP 包的默认字体（可能没有 CJK）
    ///
    /// 中文界面必须落到 1 或 3，否则所有汉字都是空白方块。
    /// </summary>
    internal static class UiFonts
    {
        private static TMP_FontAsset _cjk;
        private static TMP_FontAsset _latin;
        private static bool _resolved;

        /// <summary>
        /// 重新解析字体。面板每次打开都调一次。
        ///
        /// 为什么不只解析一次：RDConstants 是场景里的对象，而游戏在主菜单
        /// 和关卡里是**不同的场景**——模组加载那一刻场景里可能根本没有
        /// RDConstants，只解析一次就会永远拿不到字体，界面全是空白。
        /// </summary>
        internal static void Invalidate() => _resolved = false;

        /// <summary>含 CJK 字形的字体；拿不到时退回拉丁字体。</summary>
        internal static TMP_FontAsset Main =>
            (_cjk ?? _latin) ?? TMP_Settings.defaultFontAsset;

        /// <summary>纯 ASCII 的小标签可用拉丁字体，字形更清晰。</summary>
        internal static TMP_FontAsset Latin => _latin ?? Main;

        internal static void Apply(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }
            Resolve();
            if (Main != null)
            {
                text.font = Main;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;
            try
            {
                var c = UnityEngine.Object.FindAnyObjectByType<RDConstants>();
                if (c != null)
                {
                    _cjk = Pick(c.chineseFontTMPro, c.japaneseFontTMPro,
                        c.koreanFontTMPro, c.latinFontTMPro);
                    _latin = c.latinFontTMPro ?? _cjk;
                }
            }
            catch
            {
                // 场景里没有 RDConstants：下面走 Resources
            }
            if (_cjk == null)
            {
                try
                {
                    _cjk = Resources.Load<TMP_FontAsset>("SourceHanSans");
                }
                catch
                {
                    // 资源不存在就保持 null
                }
            }
            if (_latin == null)
            {
                _latin = _cjk;
            }
            if (_cjk == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[SpectreReplay] 没找到任何 TMP 字体，界面文字可能显示不出来");
            }
        }

        private static TMP_FontAsset Pick(params TMP_FontAsset[] candidates)
        {
            foreach (var f in candidates)
            {
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }
    }
}
