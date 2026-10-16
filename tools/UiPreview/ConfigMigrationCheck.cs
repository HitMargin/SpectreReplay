// 旧配置迁移的真跑验证（不依赖 Unity、不加载主程序集）。
//
// 这里把主项目 ConfigManager.MigrateLegacyConfig 的 JSON 变换**按同一判据**在
// 离屏工程里重演一遍，输入就是线上那份真实的旧 Configs.json。
// 复用同一份 Newtonsoft（UiPreview.csproj 已引用 System.Drawing.Common，此处补 Newtonsoft）。
//
// 验四件事：
//   ① 旧结构能被识别（判据不误判新版）
//   ② 每个旧键都落到正确的新键上（值不变）
//   ③ 迁移结果立刻被读取侧的 `?? Settings.X` 接受（即键名与 ConfigManager 读取侧一致）
//   ④ 迁移是幂等的：第二次跑不再改动
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UiPreview
{
    internal static class ConfigMigrationCheck
    {
        // 与 ConfigManager.MigrateLegacyConfig 的映射表保持一致。
        // 如果主项目改了映射而这里没改，③ 会红——那就是两份判据走偏的信号。
        private static readonly (string oldKey, string newKey, JTokenType kind)[] Map =
        {
            ("RecordingOn",   "ReplayOn",             JTokenType.Boolean),
            ("RenderOn",      "RenderOn",             JTokenType.Boolean),
            ("AsyncInput",    "GetDataFromAsyncInput",JTokenType.Boolean),
            ("AutoSave",      "AutoSave",             JTokenType.Boolean),
            ("SaveKey",       "save_button",          JTokenType.Integer),
            ("CancelSaveKey", "cancel_save_button",   JTokenType.Integer),
            ("Surface",       "render_capture_mode",  JTokenType.Integer),
            ("Width",         "render_width",         JTokenType.Integer),
            ("Height",        "render_height",        JTokenType.Integer),
            ("Fps",           "render_fps",           JTokenType.Integer),
            ("Encoder",       "render_encoder",       JTokenType.Integer),
            ("Quality",       "render_quality",       JTokenType.Integer),
            ("Container",     "render_output_format", JTokenType.Integer),
            ("TailSeconds",   "render_tail",          JTokenType.Float),
        };

        // OutputDir 是唯一的字符串项，走 Strings 分区而非 Sets/Ints，单独处理。
        private const string OutputDirOldKey = "OutputDir";
        private const string OutputDirNewKey = "render_output_dir";

        /// <summary>
        /// 旧版（自研引擎）配置文件里**会出现**的顶层键全集。
        ///
        /// **为什么是硬编码的常量而不是\"读线上文件\"。** 线上 Configs.json 在
        /// 2026-10-11 修好「设置存不下来」之后第一次真的被写出新版结构；此前的
        /// 判据拿顶层键当旧键逐个要归属，于是 StringLists/Colors
        /// 被判成「旧键无归属」两条假红。**旧键是一个外部事实**（旧版引擎写出来
        /// 的那份文件长什么样），不该由\"线上文件此刻的状态\"决定——那会让判据
        /// 随着玩家是否保存过一次而变红或变绿。
        ///
        /// 与 <see cref="Map"/> 的分工：Map 是\"旧→新的值映射\"（复演用），
        /// 本表是\"旧文件里有哪些键\"（完整性用）。这里必须包含**有意丢弃**的
        /// Mode / DuplicateWhenLate——它们在旧文件里确实存在，由调用方按
        /// intentionallyDropped 排除，而不是从本表里删掉（删掉就看不出它们被
        /// 有意丢弃了）。
        /// </summary>
        private static readonly string[] LegacyKeys =
        {
            // 布尔
            "RecordingOn", "RenderOn", "AsyncInput", "AutoSave",
            // 整数 / 枚举
            "SaveKey", "CancelSaveKey", "Surface", "Width", "Height",
            "Fps", "Encoder", "Quality", "Container",
            // 浮点
            "TailSeconds",
            // 字符串（单独处理，但也是旧文件里的顶层键）
            OutputDirOldKey,
            // 有意丢弃（旧文件里有，新版无对应语义）
            "Mode", "DuplicateWhenLate",
        };

        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 旧配置迁移 ──");
            int fail = 0;

            // ① 主项目里必须真的有迁移调用与判据
            string cfg = Path.Combine(projectRoot, "State", "ConfigManager.cs");
            if (!File.Exists(cfg))
            {
                Console.WriteLine("    ✗ 找不到 ConfigManager.cs");
                return 1;
            }
            string code = SrcUtil.Clean(File.ReadAllText(cfg));
            if (!code.Contains("MigrateLegacyConfig(json)", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ LoadConfigs 里没有调用 MigrateLegacyConfig —— 旧配置会被静默按默认值读取");
                fail++;
            }
            if (!code.Contains("json[\"Sets\"] != null", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ 迁移判据不含“新分区已存在”的排除项，可能误判新版文件");
                fail++;
            }

            // ② 主项目的映射必须覆盖**每一个旧键**（除 Mode/DuplicateWhenLate 有意丢弃）。
            //
            // 判据落在主项目源码上，且只看 MigrateLegacyConfig 方法体内——不能整文件
            // 搜键名：ConfigManager.cs 的读写侧本来就到处出现 "AutoSave"，整文件搜的
            // 话删掉迁移那一行照样全绿（MUT-C3 实测）。Map 也只是复演变换的副本，
            // 拿它代判同样是自已测自己。
            //
            // **判据的输入必须是"确定的旧键集合"，不能是"线上文件当前长什么样"。**
            // 2026-10-11 修复「设置存不下来」之后，线上 Configs.json 第一次真的被
            // 写出新版结构（Sets/Ints/Strings/StringLists/Colors），
            // 而这条判据仍把顶层键当成旧键逐个要归属 ⇒ StringLists/Colors
            // 被判成"旧键无归属"三条假红。更糟的是第 ④ 步：Apply 对新版文件立刻
            // return，于是"值不变、迁移幂等"整段**静默变成空转**——全绿，但一个问题
            // 也没验。这两件事是同一个根因：判据把"线上文件的当前状态"当成了输入。
            //
            // 现在改成：LegacyKeys 是外部事实（旧文件里真正会出现的键名），判据认它；
            // 线上文件若恰好还是旧版，则**额外**把它里面的键也逐个核一遍（能抓到
            // 我们没登记的新旧键）。两条路都不是空转。
            string[] intentionallyDropped = { "Mode", "DuplicateWhenLate" };
            string migrationBody = ExtractMigrationBody(code);
            string live = FindLiveConfig();
            bool liveIsLegacy = false;
            JObject json = null;
            if (live != null)
            {
                json = JObject.Parse(File.ReadAllText(live));
                // 新版判据与主项目 MigrateLegacyConfig 的排除项**必须一致**：
                // 主项目认 Sets/Ints/Strings 任一存在即新版，这里不能多认也不能少认。
                liveIsLegacy = json["Sets"] == null && json["Ints"] == null
                            && json["Strings"] == null;
                if (!liveIsLegacy)
                {
                    Console.WriteLine("    · 线上 Configs.json 已是新版结构（迁移已生效），"
                        + "改验旧键全集");
                }
            }
            else
            {
                Console.WriteLine("    · 线上 Configs.json 不在预期位置，改验旧键全集");
            }

            var legacyProps = new List<string>();
            foreach (var k in LegacyKeys)
            {
                if (intentionallyDropped.Contains(k)) continue;
                if (k == OutputDirOldKey) continue;
                legacyProps.Add(k);
            }
            if (liveIsLegacy)
            {
                // 线上文件还是旧版：它里面出现的每个键都得有归属。这能抓到
                // LegacyKeys 漏登记的旧键（判据的输入不依赖我们自己的回忆）。
                foreach (var prop in json.Properties())
                {
                    if (intentionallyDropped.Contains(prop.Name)) continue;
                    if (prop.Name == OutputDirOldKey) continue;
                    if (!legacyProps.Contains(prop.Name)) legacyProps.Add(prop.Name);
                }
            }
            foreach (var name in legacyProps)
            {
                if (!migrationBody.Contains("\"" + name + "\"", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ 旧键 " + name + " 在主项目迁移表里没有归属"
                        + "（会被静默丢弃，这项设置重启后变回默认）");
                    fail++;
                }
            }

            // ③ 每一个映射项都必须与读取侧的键名对得上
            foreach (var (oldKey, newKey, kind) in Map)
            {
                if (!code.Contains("\"" + newKey + "\"", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ 迁移写入 " + newKey + "，但读取侧没有这个键"
                        + "（" + oldKey + " 的值搬过去也没人读）");
                    fail++;
                }
            }
            // OutputDir 同理（它是唯一的字符串项）
            if (!code.Contains("\"" + OutputDirNewKey + "\"", StringComparison.Ordinal))
            {
                Console.WriteLine("    ✗ 迁移写入 " + OutputDirNewKey + "，但读取侧没有这个键");
                fail++;
            }

            // ④ 跑一遍变换，确认值不变且可重复。
            //
            // **输入不能固定在线上文件上。** Apply 对新版文件第一句就 return，
            // 于是线上转成新版之后，这整段会**静默空转**——一条 ✗ 都不会印，
            // 看起来全绿，实际一个问题也没验（与 ② 的三条假红是同一个根因的
            // 另一副面孔：一个把新版当旧版报错，一个干脆什么都不做）。
            // 所以：线上还是旧版就用真文件；已是新版（或读不到）就**合成一份
            // 旧文件**，用非默认值——值不默认才能把"迁移漏搬"照出来：漏搬时
            // 落到新分区的会是默认值，与合成值不等，第 ④ 步立刻报"值变了"。
            try
            {
                JObject work = liveIsLegacy
                    ? JObject.Parse(File.ReadAllText(live))
                    : SynthesizeLegacy();
                var before = work.Properties().ToDictionary(p => p.Name, p => p.Value);
                Apply(work);
                var after = JObject.Parse(work.ToString());
                Apply(after);
                if (after.ToString() != work.ToString())
                {
                    Console.WriteLine("    ✗ 迁移不幂等：第二次跑结果不同");
                    fail++;
                }
                foreach (var (oldKey, newKey, kind) in Map)
                {
                    if (!before.ContainsKey(oldKey)) continue;
                    JToken got = null;
                    if (kind == JTokenType.Boolean) got = work["Sets"]?[newKey];
                    else got = work["Ints"]?[newKey];
                    if (got == null)
                    {
                        Console.WriteLine("    ✗ " + oldKey + " 没有落到 " + newKey);
                        fail++;
                        continue;
                    }
                    string a = before[oldKey].ToString();
                    string b = got.ToString();
                    if (a != b && !(kind == JTokenType.Float && Math.Abs(double.Parse(a) - double.Parse(b)) < 1e-9))
                    {
                        Console.WriteLine("    ✗ " + oldKey + " 值在迁移中变了：" + a + " -> " + b);
                        fail++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ 迁移执行抛异常：" + ex.GetType().Name + ": " + ex.Message);
                fail++;
            }

            if (fail == 0)
                Console.WriteLine("    ✓ 旧键全部有归属、值不变、迁移幂等"
                    + (liveIsLegacy ? "（用线上旧文件）" : "（用合成的旧文件）"));
            return fail;
        }

        private static void Apply(JObject json)
        {
            if (json["Sets"] != null || json["Ints"] != null || json["Strings"] != null) return;
            var sets = new JObject();
            var ints = new JObject();
            var strs = new JObject();
            foreach (var (oldKey, newKey, kind) in Map)
            {
                var t = json[oldKey];
                if (t == null) continue;
                if (kind == JTokenType.Boolean) sets[newKey] = t.Type == JTokenType.Boolean ? t : (JToken)false;
                else ints[newKey] = t;
            }
            var od = json[OutputDirOldKey];
            if (od != null && od.Type == JTokenType.String && !string.IsNullOrEmpty(od.ToString()))
                strs[OutputDirNewKey] = od;
            json["Sets"] = sets;
            json["Ints"] = ints;
            json["Strings"] = strs;
        }

        /// <summary>
        /// 取出 MigrateLegacyConfig 方法体（从声明后到第一个只含 } 的行为止）。
        /// 剥过注释的源码上传号匹配即可——这个方法体内只有成对的花括号。
        /// </summary>
        private static string ExtractMigrationBody(string code)
        {
            int at = code.IndexOf("void MigrateLegacyConfig(", StringComparison.Ordinal);
            if (at < 0) return "";
            int open = code.IndexOf('{', at);
            if (open < 0) return "";
            int depth = 0, i = open;
            for (; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0) { i++; break; }
                }
            }
            return code.Substring(open, i - open);
        }

        /// <summary>
        /// 合成一份**旧版**配置，供第 ④ 步在线上文件已是新版时使用。
        ///
        /// 值刻意取非默认：迁移若把某个键搬丢，第 ④ 步会是 `got == null`
        /// （"没有落到新键"）；若搬到别的新键上，值比对会不等。用全默认值的话
        /// 前者仍能抓、后者容易与巧合相撞，非默认值让两边都确定。
        /// </summary>
        private static JObject SynthesizeLegacy()
        {
            var j = new JObject();
            int n = 0;
            foreach (var (oldKey, newKey, kind) in Map)
            {
                if (kind == JTokenType.Boolean) j[oldKey] = (n++ % 2) == 0;
                else if (kind == JTokenType.Float) j[oldKey] = 12.5;
                else j[oldKey] = 100 + n++;
            }
            j[OutputDirOldKey] = "D:\\probe\\out";
            return j;
        }

        private static string FindLiveConfig()
        {
            string[] candidates =
            {
                @"D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\SpectreReplay\Configs.json",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
