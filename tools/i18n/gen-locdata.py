# 从 tools/i18n/source.json + tools/i18n/locales/*.json 生成 SpectreReplay/Ui/LocData.cs。
#
# 为什么生成 C# 字面量而不是运行时读 JSON 嵌入资源：
#   本仓库反复记录过「静默降级」这一类失效。嵌入资源多出一条**运行时**失败路径
#   （资源名对不上 / 流读不出 / JSON 坏了），失败方式恰好是最糟的那种——表为空，
#   玩家界面上看到的是 record.autosave 这种原始键名，而日志里可能什么都没有。
#   编译进代码的字典字面量不可能在运行时缺失：漏了就是编译不过。
#
# 键顺序固定取 source.json 的顺序（= 原本 Zh 表的顺序），
# 这样 diff 里同一组文案永远连续，不因生成而乱序。
import io, json, os, sys

ROOT = r"D:\Projects\SpectreReplay"
SRC = os.path.join(ROOT, "tools", "i18n", "source.json")
I18N = os.path.join(ROOT, "tools", "i18n", "locales")
OUT = os.path.join(ROOT, "SpectreReplay", "Ui", "LocData.cs")

# 顺序即界面上不会被看到的"语言清单顺序"，但决定字典初始化的迭代顺序。
LANGS = [
    ("zh", "Chinese (Simplified)"),
    ("zh-t", "Chinese (Traditional)"),
    ("en", "English"),
    ("ja", "Japanese"),
    ("ko", "Korean"),
    ("es", "Spanish"),
    ("pt", "Portuguese"),
    ("fr", "French"),
    ("de", "German"),
    ("pl", "Polish"),
    ("cs", "Czech"),
    ("ro", "Romanian"),
    ("ru", "Russian"),
    ("vi", "Vietnamese"),
]


def esc(s):
    """C# 普通字符串字面量转义。"""
    out = []
    for ch in s:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        else:
            out.append(ch)
    return "".join(out)


def main():
    src = json.load(io.open(SRC, encoding="utf-8-sig"))
    keys = [x["key"] for x in src]
    srcmap = {x["key"]: x for x in src}

    data = {}
    for code, _ in LANGS:
        if code == "zh":
            data[code] = {x["key"]: x["zh"] for x in src}
        elif code == "en":
            data[code] = {x["key"]: x["en"] for x in src}
        else:
            p = os.path.join(I18N, code + ".json")
            if not os.path.exists(p):
                sys.exit("缺少翻译文件：" + p)
            d = json.load(io.open(p, encoding="utf-8-sig"))
            ent = d["entries"]
            miss = [k for k in keys if k not in ent]
            extra = [k for k in ent if k not in srcmap]
            if miss or extra:
                sys.exit("%s 键集合不对：缺 %d %s，多 %d %s"
                         % (code, len(miss), miss[:5], len(extra), extra[:5]))
            data[code] = {k: ent[k] for k in keys}

    lines = []
    lines.append("// 界面文案数据：14 种语言 × %d 个键。" % len(keys))
    lines.append("//")
    lines.append("// **本文件由 _gen_locdata.py 生成，不要手改。** 改文案请改 `tools/i18n/source.json`")
    lines.append("// 或 `tools/i18n/locales/<lang>.json` 后重新生成——手改会在下次生成时被覆盖，")
    lines.append("// 而且很容易只改到一种语言，留下「这里怎么还是英文」的下一个坑。")
    lines.append("//")
    lines.append("// 为什么是编译进代码的字面量、而不是运行时读嵌入资源：")
    lines.append("//   读文件多一条**运行时**失败路径（资源名对不上 / 流读不出 / JSON 坏了），")
    lines.append("//   而它的失败方式正好是最糟的——表为空，玩家看到 record.autosave 这种")
    lines.append("//   原始键名，日志里还未必有线索。编译进代码就不可能在运行时缺失。")
    lines.append("//")
    lines.append("// 语言代码与 `LocalizationManager.langlist` 完全一致（同一份游戏枚举映射）。")
    lines.append("namespace SpectreReplay.Ui")
    lines.append("{")
    lines.append("    internal static class LocData")
    lines.append("    {")
    lines.append("        /// <summary>语言代码 → 该语言的键值表。</summary>")
    lines.append("        internal static readonly System.Collections.Generic.Dictionary<string,"
                 " System.Collections.Generic.Dictionary<string, string>> Languages =")
    lines.append("            new System.Collections.Generic.Dictionary<string,"
                 " System.Collections.Generic.Dictionary<string, string>>")
    lines.append("            {")
    for code, desc in LANGS:
        lines.append('                ["%s"] = new System.Collections.Generic.Dictionary<string, string>'
                     % code)
        lines.append("                {")
        for k in keys:
            v = data[code][k]
            if v is None:
                v = ""
            lines.append('                    ["%s"] = "%s",' % (esc(k), esc(v)))
        lines.append("                },")
    lines.append("            };")
    lines.append("    }")
    lines.append("}")

    text = "\n".join(lines) + "\n"
    # 仓库约定：.cs 文件带 UTF-8 BOM（PS 5.1 / 部分工具按 ANSI 读非 BOM 文件会乱码）。
    with io.open(OUT, "w", encoding="utf-8-sig", newline="") as f:
        f.write(text)
    print("wrote", OUT, len(text), "chars,", len(keys), "keys x", len(LANGS), "langs")


if __name__ == "__main__":
    main()