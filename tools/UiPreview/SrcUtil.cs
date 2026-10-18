// 源码级检查的共用小工具。
//
// 为什么单独抽出来：**StripComments 已经被逐字抄了三份**
// （PatchGuardCheck / FrameSourceCheck / HitTrackCheck）。
// 抄到第四份时就该停了——同一段判据存在两个副本时，改了一份忘了另一份，
// 就会出现"检查 A 变红、检查 B 还是绿的"这种最难查的现象，
// 而闸门看起来还是全绿。
//
// 用法：读进来先 Clean 掉注释，再往**控制流**上落判据。
// 注释必须先剥，否则「文件里出现过 return false」这种判据会被
// 说明文字里的例子骗过去——第一版 Guard 检查就是这么全绿的。
using System;
using System.IO;
using System.Text;

namespace UiPreview
{
    internal static class SrcUtil
    {
        /// <summary>
        /// 游戏程序集所在目录（<c>adofai-libs</c>）。
        ///
        /// 本地布局是**仓库的同级目录**（csproj 里 31 个 HintPath 都写
        /// <c>..\..\adofai-libs\</c>）；CI 里由检出位置决定，靠环境变量
        /// <c>ADOFAI_LIBS</c> 指过去。两种布局都要能跑——否则依赖真 dll 的
        /// 那几项检查只会在本地绿、在 CI 里静默跳过，而「绿着的闸门其实有几项
        /// 没跑」正是这套工具最想避免的形态。
        /// </summary>
        internal static string GameLibsRoot()
            => ResolveRoot("ADOFAI_LIBS", "adofai-libs", @"D:\Projects\adofai-libs");

        /// <summary>游戏程序集的完整路径；目录不存在返回 null（调用方决定跳过还是判红）。</summary>
        internal static string GameAssembly(string fileName)
        {
            string libs = GameLibsRoot();
            return libs == null ? null : Path.Combine(libs, fileName);
        }

        /// <summary>
        /// 按 ① 环境变量 ② 从产物目录逐级向上找同级目录 ③ 历史绝对路径 定位。
        ///
        /// **为什么要有 ②。** 本地产物在
        /// <c>&lt;repo&gt;\tools\UiPreview\bin\Release\net10.0-windows</c>，
        /// CI 在 <c>&lt;ws&gt;\SpectreReplay\tools\UiPreview\bin\Release\net10.0-windows</c>：
        /// 两者往上若干层都会经过自己的仓库根，再上一层就是依赖仓库的所在。
        /// 写死绝对路径则 CI 必挂；只认环境变量则本地每次都要额外配置。
        /// </summary>
        private static string ResolveRoot(string envName, string sibling, string legacy)
        {
            string fromEnv = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
                return fromEnv;

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string probe = Path.Combine(dir, sibling);
                if (Directory.Exists(probe)) return probe;
                DirectoryInfo parent = Directory.GetParent(
                    dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent == null) break;
                dir = parent.FullName;
            }

            return Directory.Exists(legacy) ? legacy : null;
        }

        /// <summary>按相对路径读源码；读不到返回空串（由各检查自己判）。</summary>
        internal static string Read(string projectRoot, string relative)
        {
            string p = Path.Combine(projectRoot, relative.Replace('/', '\\'));
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }

        /// <summary>剥掉 // 与 /* */ 注释（正确跳过字符串与字符字面量）。</summary>
        internal static string Clean(string src)
        {
            if (src.Length == 0)
            {
                return src;
            }
            var sb = new StringBuilder(src.Length);
            bool inBlock = false, inStr = false, inChar = false, esc = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    { inBlock = false; i++; }
                    else if (c == '\n') sb.Append(c);
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '*')
                { inBlock = true; i++; continue; }
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                // **字符串内的单引号不是字符字面量的定界符。**
                // 与 HardTextCheck 曾栽的是同一个坑：撇号（英文日志文案里极常见，
                // 如 "the game's UpdateHoldKeys"）若在 inStr 期间也切换 inChar，
                // 会让之后整个文件的注释都因 `!inChar` 守卫而剥不掉，
                // 于是"不许出现 X"那类判据把注释里的说明文字当成缺陷（常红）。
                if (c == '"') { inStr = !inStr; sb.Append(c); continue; }
                if (inStr) { sb.Append(c); continue; }
                if (c == '\'') inChar = !inChar;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>读 + 剥注释，一步到位。</summary>
        internal static string Source(string projectRoot, string relative)
            => Clean(Read(projectRoot, relative));
    }
}