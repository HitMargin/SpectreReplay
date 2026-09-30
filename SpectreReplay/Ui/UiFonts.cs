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
        /// 为什么不只解析一次：游戏在主菜单和关卡里是**不同的场景**，模组加载
        /// 那一刻资源未必就绪；只解析一次就会把"暂时拿不到"固化成"永远拿不到"。
        ///
        /// **必须连 _cjk/_latin 一起清**。之前只重置 _resolved，于是上一次解析
        /// 的结果被下一次复用——离线跑回退链时三条分支全拿到同一个字体，
        /// 看起来像解析逻辑没生效。离屏验证：dotnet run -- <dir>
        /// </summary>
        internal static void Invalidate()
        {
            _resolved = false;
            _cjk = null;
            _latin = null;
        }

        /// <summary>
        /// 含 CJK 字形的字体；拿不到时退回拉丁字体，最后退回 TMP 默认。
        ///
        /// 这里**自己触发解析**而不是只读缓存。之前它是个纯读属性的表达式，
        /// 任何在 Apply 之前读它的地方拿到的都是 null，于是悄悄退到
        /// TMP 默认字体（多半没有中日韩字形）——界面上一片空白方块，
        /// 而且没有任何报错。离屏验证：dotnet run -- &lt;dir&gt;
        /// </summary>
        internal static TMP_FontAsset Main
        {
            get
            {
                Resolve();
                return (_cjk ?? _latin) ?? TMP_Settings.defaultFontAsset;
            }
        }

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
            string from = null;

            // 1. RDConstants.data —— 游戏的常量资源，是 ScriptableObject。
            //    之前用的是 FindAnyObjectByType<RDConstants>()，那要在场景里找
            //    组件；RDConstants 是资源不是场景对象，那样多半找不到，
            //    字体一路退化到兜底分支还不报错。
            try
            {
                var c = RDConstants.data;
                if (c != null)
                {
                    _cjk = Pick(c.chineseFontTMPro, c.japaneseFontTMPro,
                        c.koreanFontTMPro, c.latinFontTMPro);
                    _latin = c.latinFontTMPro ?? _cjk;
                    if (_cjk != null)
                    {
                        from = "RDConstants.data";
                    }
                }
            }
            catch
            {
                // 资源尚未加载
            }

            // 2. 兜底走 Resources。路径来自游戏自己的资源清单
            //    （StreamingAssets\aa\catalog.json 里 SourceHanSans/* 的实际条目），
            //    别凭空猜——原先写的 "SourceHanSans/Regular" **根本不存在**，
            //    实际是 Normal / Light / Medium / Bold 这些字重，CJK 在
            //    SourceHanSans/ChineseSimplified/ 下面。
            if (_latin == null)
            {
                _latin = Load("SourceHanSans/Latin/Latin-Normal")
                    ?? Load("SourceHanSans/Normal");
                if (_latin != null && from == null)
                {
                    from = "Resources/SourceHanSans/Latin";
                }
            }
            if (_cjk == null)
            {
                _cjk = Load("SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal")
                    ?? Load("SourceHanSans/ChineseSimplified/ChineseSimplified-Normal");
                if (_cjk != null)
                {
                    from = "Resources/SourceHanSans/ChineseSimplified";
                }
            }
            _cjk = _cjk ?? _latin;

            ApplyFallbackChain();
            if (Main == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[SpectreReplay] 没找到任何 TMP 字体（RDConstants.data 与 SourceHanSans 都没拿到）"
                    + "，界面文字可能显示不出来");
            }
            else
            {
                // 字体来源只能从 Player.log 看，出问题时这是第一现场
                UnityEngine.Debug.Log("[SpectreReplay] 界面字体来源：" + from
                    + "（中文=" + Name(_cjk) + " 拉丁=" + Name(_latin) + "）");
            }
        }

        /// <summary>
        /// 把另一套字体挂成回退链。
        ///
        /// 与游戏自己的做法一致（见 ADOFAI\DynamicallyOrderedFont.ApplyForLanguage）：
        /// 设 <c>fallbackFontAssetTable</c>，而不是二选一。界面上中英混排是常态
        /// ——"60 fps"、"Mods\SpectreReplay\Videos"、"4.5 s" 这些 ASCII 串，
        /// 挨着中文标签。只挂一个字体的话，缺的那部分字形就是空白方块。
        /// </summary>
        private static void ApplyFallbackChain()
        {
            if (_cjk == null || _latin == null || _cjk == _latin)
            {
                return;
            }
            try
            {
                _cjk.fallbackFontAssetTable =
                    new System.Collections.Generic.List<TMP_FontAsset> { _latin };
            }
            catch
            {
                // 字体资源还没就绪：不影响主字体显示
            }
        }

        private static TMP_FontAsset Load(string path)
        {
            try
            {
                return Resources.Load<TMP_FontAsset>(path);
            }
            catch
            {
                return null;
            }
        }

        private static string Name(TMP_FontAsset f) => f == null ? "(无)" : f.name;

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
