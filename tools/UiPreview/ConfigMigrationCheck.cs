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

            // ② 主项目的映射必须覆盖旧文件里出现的每一个旧键（除 Mode/DuplicateWhenLate 有意丢弃）
            string live = FindLiveConfig();
            if (live == null)
            {
                Console.WriteLine("    · 线上 Configs.json 不在预期位置，跳过实跑部分");
                return fail;
            }
            var json = JObject.Parse(File.ReadAllText(live));
            // ② **主项目**的迁移表必须覆盖旧文件里出现的每一个旧键。
            //    判据落在主项目源码上，且只看 MigrateLegacyConfig 方法体内——
            //    不能整文件搜键名：ConfigManager.cs 的读写侧本来就到处出现
            //    "AutoSave"，整文件搜的话删掉迁移那一行照样全绿（MUT-C3 实测）。
            //    Map 也只是复演变换的副本，拿它代判同样是自已测自己。
            string[] intentionallyDropped = { "Mode", "DuplicateWhenLate" };
            string migrationBody = ExtractMigrationBody(code);
            foreach (var prop in json.Properties())
            {
                if (intentionallyDropped.Contains(prop.Name)) continue;
                if (prop.Name == OutputDirOldKey) continue;
                if (!migrationBody.Contains("\"" + prop.Name + "\"", StringComparison.Ordinal))
                {
                    Console.WriteLine("    ✗ 旧键 " + prop.Name + " 在主项目迁移表里没有归属"
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

            // ④ 用真文件跑一遍变换，确认值不变且可重复
            try
            {
                var work = JObject.Parse(File.ReadAllText(live));
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
                Console.WriteLine("    ✓ 旧键全部有归属、值不变、迁移幂等");
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
