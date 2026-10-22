# 从 tools/i18n/source.json + tools/i18n/locales/*.json 生成 SpectreReplay/Ui/LocData.cs。
#
# 这是 gen-locdata.py 的 PowerShell 移植版（本机没有 Python，只有 Store 的转发存根）。
# 逻辑与原脚本逐行对应，包括：
#   · 键顺序 = source.json 的顺序
#   · zh/en 取自 source.json，其余 12 种取自 locales/<code>.json 的 entries
#   · 各语言键集合必须与 source 完全一致，否则直接拒绝生成（不写半张表）
#   · 输出 UTF-8 BOM + LF 换行（仓库约定，PS 5.1 / 部分工具按 ANSI 读非 BOM 会乱码）
#
# 为什么生成 C# 字面量而不是运行时读 JSON 嵌入资源：
#   嵌入资源多一条**运行时**失败路径（资源名对不上 / 流读不出 / JSON 坏了），
#   失败方式恰好是最糟的那种——表为空，玩家界面上看到 record.autosave 这种原始
#   键名，而日志里可能什么都没有。编译进代码的字典字面量不可能在运行时缺失。

$ErrorActionPreference = 'Stop'

$Root  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Src   = Join-Path $Root 'tools\i18n\source.json'
$I18n  = Join-Path $Root 'tools\i18n\locales'
$Out   = Join-Path $Root 'SpectreReplay\Ui\LocData.cs'

# 顺序即字典初始化顺序；与 LocalizationManager.langlist 的映射保持一致
$Langs = @(
    @('zh',   'Chinese (Simplified)'),
    @('zh-t', 'Chinese (Traditional)'),
    @('en',   'English'),
    @('ja',   'Japanese'),
    @('ko',   'Korean'),
    @('es',   'Spanish'),
    @('pt',   'Portuguese'),
    @('fr',   'French'),
    @('de',   'German'),
    @('pl',   'Polish'),
    @('cs',   'Czech'),
    @('ro',   'Romanian'),
    @('ru',   'Russian'),
    @('vi',   'Vietnamese')
)

function Esc([string]$s) {
    if ($null -eq $s) { return '' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        switch ($ch) {
            '\'  { [void]$sb.Append('\\');  continue }
            '"'  { [void]$sb.Append('\"');  continue }
            "`n" { [void]$sb.Append('\n');  continue }
            "`r" { [void]$sb.Append('\r');  continue }
            "`t" { [void]$sb.Append('\t');  continue }
            default { [void]$sb.Append($ch) }
        }
    }
    return $sb.ToString()
}

$src = Get-Content -LiteralPath $Src -Raw -Encoding UTF8 | ConvertFrom-Json
$keys = @($src | ForEach-Object { $_.key })
$srcMap = @{}
foreach ($e in $src) { $srcMap[$e.key] = $e }

$data = @{}
foreach ($pair in $Langs) {
    $code = $pair[0]
    if ($code -eq 'zh') {
        $m = @{}
        foreach ($e in $src) { $m[$e.key] = $e.zh }
        $data[$code] = $m
    }
    elseif ($code -eq 'en') {
        $m = @{}
        foreach ($e in $src) { $m[$e.key] = $e.en }
        $data[$code] = $m
    }
    else {
        $p = Join-Path $I18n ($code + '.json')
        if (-not (Test-Path -LiteralPath $p)) {
            Write-Error "缺少翻译文件：$p"
            exit 1
        }
        $d = Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
        $ent = @{}
        foreach ($prop in $d.entries.PSObject.Properties) { $ent[$prop.Name] = $prop.Value }

        $miss  = @($keys | Where-Object { -not $ent.ContainsKey($_) })
        $extra = @($ent.Keys | Where-Object { -not $srcMap.ContainsKey($_) })
        if ($miss.Count -gt 0 -or $extra.Count -gt 0) {
            $m5 = ($miss | Select-Object -First 5) -join ', '
            $e5 = ($extra | Select-Object -First 5) -join ', '
            Write-Error ("{0} 键集合不对：缺 {1} [{2}]，多 {3} [{4}]" -f $code, $miss.Count, $m5, $extra.Count, $e5)
            exit 1
        }
        $m = @{}
        foreach ($k in $keys) { $m[$k] = $ent[$k] }
        $data[$code] = $m
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("// 界面文案数据：14 种语言 × $($keys.Count) 个键。")
$lines.Add("//")
$lines.Add("// **本文件由 tools/i18n/gen-locdata.ps1 生成，不要手改。** 改文案请改 ``tools/i18n/source.json``")
$lines.Add("// 或 ``tools/i18n/locales/<lang>.json`` 后重新生成——手改会在下次生成时被覆盖，")
$lines.Add("// 而且很容易只改到一种语言，留下「这里怎么还是英文」的下一个坑。")
$lines.Add("//")
$lines.Add("// 为什么是编译进代码的字面量、而不是运行时读嵌入资源：")
$lines.Add("//   读文件多一条**运行时**失败路径（资源名对不上 / 流读不出 / JSON 坏了），")
$lines.Add("//   而它的失败方式正好是最糟的——表为空，玩家看到 record.autosave 这种")
$lines.Add("//   原始键名，日志里还未必有线索。编译进代码就不可能在运行时缺失。")
$lines.Add("//")
$lines.Add("// 语言代码与 ``LocalizationManager.langlist`` 完全一致（同一份游戏枚举映射）。")
$lines.Add("namespace SpectreReplay.Ui")
$lines.Add("{")
$lines.Add("    internal static class LocData")
$lines.Add("    {")
$lines.Add("        /// <summary>语言代码 → 该语言的键值表。</summary>")
$lines.Add("        internal static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> Languages =")
$lines.Add("            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>")
$lines.Add("            {")
foreach ($pair in $Langs) {
    $code = $pair[0]
    $lines.Add('                ["' + $code + '"] = new System.Collections.Generic.Dictionary<string, string>')
    $lines.Add("                {")
    foreach ($k in $keys) {
        $v = $data[$code][$k]
        if ($null -eq $v) { $v = '' }
        $lines.Add('                    ["' + (Esc $k) + '"] = "' + (Esc ([string]$v)) + '",')
    }
    $lines.Add("                },")
}
$lines.Add("            };")
$lines.Add("    }")
$lines.Add("}")

$text = ($lines -join "`n") + "`n"
# 仓库约定：.cs 带 UTF-8 BOM；newline 用 LF（与原脚本 newline="" 一致）
$enc = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($Out, $text, $enc)
Write-Output ("wrote {0} {1} chars, {2} keys x {3} langs" -f $Out, $text.Length, $keys.Count, $Langs.Count)