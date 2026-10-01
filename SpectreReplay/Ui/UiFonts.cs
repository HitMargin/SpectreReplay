using System;
using System.Linq;          // SortedSet.ToArray()——netstandard2.1 里不在 System
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
        private static string from;
        private static int bestMissing;

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

        /// <summary>
        /// 查一段文字里有没有字体不认识的字。
        ///
        /// **这是"文字不显示"最常见、也最难自己发现的一种原因。** 游戏
        /// 只收录了它自己用到的字形（省体积），模组写的字只要有一个不在
        /// 里面，TMP 就画一个空白——**不报错、不留痕**，界面上就是那句话
        /// 缺了几个字，或者整行看着比预期短。
        ///
        /// 离线预览查不出来：那边用 GDI+ 的微软雅黑，任何汉字都画得出来。
        /// 真机上也只有 TMP 自己知道答案——好在它有 HasCharacters。
        ///
        /// 查出来就记进 Player.log，缺的是哪几个字一目了然，不用猜。
        /// </summary>
        internal static void ReportMissing(string what, string text)
        {
            var font = Main;
            if (font == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                // 两个要点，都踩过：
                //   第二个参数是 **out**（反射显示 List<uint>&，编译器要 out）
                //   4 参重载返回的是 **uint[]**，不是 List——没 List.Count
                if (!font.HasCharacters(text, out uint[] missing, true, false)
                    && missing != null && missing.Length > 0)
                {
                    // 去重：TMP 按出现次数给，一个字出现两次就报两遍，
                    // 日志里会变成"渲渲渲渲"，看不出到底缺了几个字。
                    var seen = new System.Collections.Generic.SortedSet<char>();
                    foreach (uint c in missing)
                    {
                        if (c < 0x110000u)
                        {
                            seen.Add((char)c);
                        }
                    }
                    // 写成一条拼好的字符串：拆成几段的话，硬编码文案检查会
                    // 把后半截当成"玩家会看到的界面文案"报出来（它确实抓到了）。
                    UnityEngine.Debug.LogWarning(
                        $"[SpectreReplay] 字体缺字形：{what} 里没有「"
                        + $"{new string(seen.ToArray())}」"
                        + $"这些位置会是空白。字体={font.name}");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    "[SpectreReplay] 查缺字形失败：" + ex.Message);
            }
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;

            // 候选逐个收，谁**真的**有界面要用的字就用谁。
            //
            // 之前是"谁先取到谁上"，那等于相信字段名——真机日志里
            // RDConstants.data.chineseFontTMPro 给的是
            // `cjkFonts-regular-normalized SDF`，它**缺界面里 200 多个字**。
            // 界面上少数能显示的字是回退链救回来的，救不回来的全是空白，
            // 于是整页看着像空的，只有一行说明的下半截隐约可见。
            //
            // 所以顺序无所谓，重要的是**验**：HasCharacters 是 TMP 自己的
            // 答案，比任何字段名都可靠。
            var cands = new System.Collections.Generic.List<Candidate>();
            Try(() =>
            {
                var c = RDConstants.data;
                if (c == null) return;
                Add(cands, c.chineseFontTMPro, "RDConstants/中文", true);
                // chineseFontTMProDuplicate：同一份资源重复挂了一个字段，
                // 名字里的 Duplicate 很说明问题——真正的中文字形多半在这儿，
                // 而 chineseFontTMPro 挂的是那个不含 CJK 的 sourceFont。
                Add(cands, c.chineseFontTMProDuplicate, "RDConstants/中文副本", true);
                Add(cands, c.japaneseFontTMPro, "RDConstants/日文", true);
                Add(cands, c.koreanFontTMPro, "RDConstants/韩文", true);
                Add(cands, c.latinFontTMPro, "RDConstants/拉丁", true);
            });
            Add(cands, Load("SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal"),
                "Resources/SourceHanSansSC-Normal");
            Add(cands, Load("SourceHanSans/ChineseSimplified/ChineseSimplified-Normal"),
                "Resources/ChineseSimplified-Normal");
            Add(cands, Load("SourceHanSans/Normal"), "Resources/SourceHanSans/Normal");
            Add(cands, Load("SourceHanSans/Latin/Latin-Normal"),
                "Resources/Latin-Normal");
            Add(cands, TMP_Settings.defaultFontAsset, "TMP默认");

            string probe = Loc.AllText();
            var tried = new System.Text.StringBuilder();
            Candidate best = null;
            Candidate firstGame = null;
            foreach (var cd in cands)
            {
                cd.Missing = CountMissing(cd.Font, probe);
                if (tried.Length > 0) tried.Append("  ");
                tried.Append(cd.Label).Append("=").Append("缺" + cd.Missing);
                // 记下第一个游戏自己的字体。全都不完整时用它兜底：
                //
                // 游戏的 sourceFont（cjkFonts…）字形不全，是因为汉字挂在
                // 它**回退链**里的语言字体上，而那条链由游戏在运行时维护
                // （DynamicallyOrderedFont.ApplyForLanguage）。模组加载那一刻
                // 链可能还没填上，于是实测"缺字"——但它随后就会变得完整。
                //
                // 反过来，一个 Resources 字体一旦实测缺字就是**真的**缺，
                // 不会有人再去补它。所以全都不完整时，宁可用游戏的。
                if (cd.FromGame && firstGame == null)
                {
                    firstGame = cd;
                }
                if (cd.Missing == 0)
                {
                    best = cd;
                    break;
                }
                if (best == null || cd.Missing < best.Missing)
                {
                    best = cd;
                }
            }
            best ??= firstGame;
            if (best?.Font == null)
            {
                _cjk = null;
                _latin = null;
            }
            else
            {
                _cjk = best.Font;
                _latin = PickLatin(cands) ?? _cjk;
                from = best.Label;
                bestMissing = best.Missing;
            }

            ApplyFallbackChain();
            if (Main == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[SpectreReplay] 没找到任何 TMP 字体，界面文字可能显示不出来");
            }
            else
            {
                // 缺几个字、哪个字体、为什么选它——只能从 Player.log 看。
                // 出问题时这是第一现场：以前这行只写字体名，恰恰是那个名字
                // （"中文=cjkFonts..."）把人带偏了，以为字段叫中文就是中文字体。
                UnityEngine.Debug.Log("[SpectreReplay] 界面字体来源：" + from
                    + "（缺字 " + bestMissing + "）候选：" + tried
                    + "（中文=" + Name(_cjk) + " 拉丁=" + Name(_latin) + "）");
                if (bestMissing > 0)
                {
                    ReportMissing("UI text", probe);
                }
            }
        }

        private sealed class Candidate
        {
            internal TMP_FontAsset Font;
            internal string Label;
            internal int Missing = int.MaxValue;

            /// <summary>游戏自己的字体——它的回退链由游戏在运行时填。</summary>
            internal bool FromGame;
        }

        private static void Add(System.Collections.Generic.List<Candidate> into,
            TMP_FontAsset f, string label, bool fromGame = false)
        {
            if (f != null)
            {
                into.Add(new Candidate
                {
                    Font = f,
                    Label = label,
                    FromGame = fromGame,
                });
            }
        }

        /// <summary>取第一个不是 CJK 主字体的候选，作为拉丁回退。</summary>
        private static TMP_FontAsset PickLatin(System.Collections.Generic.List<Candidate> cands)
        {
            foreach (var cd in cands)
            {
                if (cd.Font != _cjk && cd.Font != null)
                {
                    return cd.Font;
                }
            }
            return null;
        }

        /// <summary>这段文字里有多少个字形是字体没有的。拿不到答案时按 0 算。</summary>
        private static int CountMissing(TMP_FontAsset font, string text)
        {
            if (font == null || string.IsNullOrEmpty(text))
            {
                return 0;
            }
            try
            {
                if (font.HasCharacters(text, out uint[] missing, true, false))
                {
                    return 0;
                }
                var set = new System.Collections.Generic.HashSet<char>();
                if (missing != null)
                {
                    foreach (uint c in missing)
                    {
                        if (c < 0x110000u) set.Add((char)c);
                    }
                }
                return set.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // 资源尚未加载
            }
        }

        /// <summary>
        /// **什么都不做**——而且这是有意为之。
        ///
        /// 原先这里把 `_cjk.fallbackFontAssetTable` 设成 `[拉丁, TMP默认]`，
        /// 注释还写着"与游戏自己的做法一致"。**那句话是错的，而且做法是反的。**
        ///
        /// 看游戏自己的代码（ADOFAI\DynamicallyOrderedFont.ApplyForLanguage）：
        ///
        ///     sourceFont.fallbackFontAssetTable = [选中语言字体, ...其它, ...extra];
        ///
        /// `cjkFonts-regular-normalized SDF` 就是那个 **sourceFont（base font）**——
        /// 它自己**本来就不含 CJK 字形**，汉字全在它回退链里的
        /// `simplifiedChineseFont` 上。所以：
        ///
        ///   · 拿它当"中文字体"是看错了字段语义
        ///   · 改它的回退链 = 把游戏的字体解析链整个拆掉
        ///
        /// 而 `fallbackFontAssetTable` 是**资源上的共享全局状态**，不是某个
        /// 文字的私有属性。模组改它 = 改游戏自己界面用的那份。
        ///
        /// 现在不碰它：选字体时已经逐个验过字形（见 Resolve），挑的是一个
        /// **自己就带全部所需字形**的字体，不需要链来补。
        /// </summary>
        private static void ApplyFallbackChain()
        {
            // 刻意留空。见上面的说明。
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
