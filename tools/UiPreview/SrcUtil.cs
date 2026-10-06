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
using System.IO;
using System.Text;

namespace UiPreview
{
    internal static class SrcUtil
    {
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