// 缺字形自检的验证。
//
// "文字不显示"最常见、也最难自己发现的一种原因：游戏只收录了**它自己
// 用到的**字形（省体积），模组写的字只要有一个不在里面，TMP 就画一个
// 空白——不报错、不留痕，界面上就是那句话缺了几个字。
//
// 离线预览查不出来：那边用 GDI+ 的微软雅黑，任何汉字都画得出来。
// 这里验证的是"自检代码本身能不能把缺的字报出来"，缺字的事实只有
// 真机上的 TMP 才知道——但自检一旦报出来，缺的是哪几个字一目了然，
// 不用再猜。
using System;
using System.Collections.Generic;
using SpectreReplay.Ui;
using TMPro;

namespace UiPreview
{
    internal static class MissingGlyphCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 缺字形自检 ──");
            int fail = 0;

            // 探针字体：缺字表是**实例**的（真实字体各有各的字符表），
            // 得让 UiFonts.Main 真的解析到这个实例上才能测。光建个对象
            // 改它的缺字表没用——ReportMissing 查的是 Main。
            // 探针字体：缺字表是**实例**的（真实字体各有各的字符表），
            // 得让 UiFonts.Main 真的解析到这个实例上才能测。光建个对象
            // 改它的缺字表没用——ReportMissing 查的是 Main。
            //
            // **所有**候选都要缺那几个字：现在解析是"逐个验字形、挑一个
            // 一个字都不缺的"，只让一个候选缺的话，它压根不会被选中，
            // ReportMissing 自然没话说——那是选字形的断言，不是这条的。
            // 要走到"最好的也缺"这一支，就得全都缺。
            var probe = new TMP_FontAsset { name = "探针" };
            UnityEngine.Resources.Registry.Clear();
            foreach (var p in new[]
            {
                "SourceHanSans/ChineseSimplified/SourceHanSansSC-Normal",
                "SourceHanSans/ChineseSimplified/ChineseSimplified-Normal",
                "SourceHanSans/Normal",
                "SourceHanSans/Latin/Latin-Normal",
            })
            {
                UnityEngine.Resources.Registry.Add(new TMP_FontAsset { name = p });
            }
            // FontCheck 的最后一个用例把 RDConstants.data 整个置空了，
            // 而这里跑在它后面。检查之间不该互相假设对方留下的状态。
            if (UnityEngine.RDConstants.data == null)
            {
                UnityEngine.RDConstants.data = new UnityEngine.RDConstants();
            }
            UnityEngine.RDConstants.data.chineseFontTMPro = probe;
            UnityEngine.RDConstants.data.latinFontTMPro = probe;

            // ① 什么都不缺时不该报警
            foreach (var f in UnityEngine.Resources.Registry) f.Missing.Clear();
            probe.Missing.Clear();
            UiFonts.Invalidate();
            Loc.UseChinese = true;
            var lines = CaptureWarnings(() =>
                UiFonts.ReportMissing("UI text", Loc.AllText()));
            if (lines.Count == 0)
            {
                Console.WriteLine("    ✓ 字都在时不自检报警");
            }
            else
            {
                Console.WriteLine("    ✗ 字都在却报了缺：");
                foreach (var l in lines) Console.WriteLine("        " + l);
                fail++;
            }

            // ② 缺字时必须**指名道姓**地报出来。
            //    挑的字必须真的出现在界面文案里，否则它压根不会被查——
            //    一开始我随手写了「缘」，它不在文案中，自检自然不报，
            //    一开始我随手写了「缘」，它不在文案中，自检自然不报，
            //    结果被误判成"自检不起作用"。
            foreach (var f in UnityEngine.Resources.Registry)
            {
                f.Missing.Clear();
                f.Missing.Add('渲');
                f.Missing.Add('录');
            }
            probe.Missing.Clear();
            probe.Missing.Add('渲');
            probe.Missing.Add('录');
            UiFonts.Invalidate();
            lines = CaptureWarnings(() =>
                UiFonts.ReportMissing("UI text", Loc.AllText()));
            bool named = false;
            foreach (var l in lines)
            {
                if (l.Contains("渲") && l.Contains("录")) named = true;
            }
            string first = lines.Count > 0
                ? lines[0].Substring(0, Math.Min(70, lines[0].Length))
                : "（没报）";
            Console.WriteLine($"    {(named ? "✓" : "✗")} 缺字时指名报出来  → {first}");
            if (!named) fail++;

            // ③ 同一个字只报一次（"渲"被设了两次）
            bool once = true;
            foreach (var l in lines)
            {
                int n = 0, idx = l.IndexOf('渲');
                while (idx >= 0) { n++; idx = l.IndexOf('渲', idx + 1); }
                if (n > 1) once = false;
            }
            Console.WriteLine($"    {(once ? "✓" : "✗")} 同一个字不重复报");
            if (!once) fail++;

            probe.Missing.Clear();
            UiFonts.Invalidate();
            // 收尾：把替身字体撤掉，别留给后面跑的检查
            UnityEngine.Resources.Registry.Clear();
            UnityEngine.Resources.HasSourceHanSans = true;
            return fail;
        }

        /// <summary>跑一段代码，把它打的警告收集起来。</summary>
        private static List<string> CaptureWarnings(Action body)
        {
            var got = new List<string>();
            var real = UnityEngine.Debug.LogWarning;
            UnityEngine.Debug.LogWarning = o => got.Add(o == null ? "" : o.ToString());
            try
            {
                body();
            }
            finally
            {
                UnityEngine.Debug.LogWarning = real;
            }
            return got;
        }
    }
}