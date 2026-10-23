# 清理“AI 汇报式”注释：整块删除，不留半句。
#
# 判据（任一命中即视为叙述性块，且块长 >= 2 才删）：
#   · 日期 20xx-xx-xx
#   · “用户裁定/要求/报/说/反馈”
#   · “上一轮/上一版/原先/曾经/此前/后来/早先/当时/本轮/历史”
#   · “实测 / round N / 第 N 轮”
#   · “判据 / 变异 / MUT- / 回归 / 闸门 / 审计”
#   · “教训 / 坑 / 防退回 / 失效形态”
#
# 单行注释不动（通常是在解释紧邻的那一行代码）。
# 代码行尾的注释也不动（本脚本只处理整行注释块）。
param(
    [switch]$DryRun,
    [string[]]$OnlyFiles = @()
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'SpectreReplay'

$markers = @(
    '\b20\d\d-\d\d-\d\d\b',
    '用户(裁定|要求|报|说|反馈)',
    '上一轮|上一版|上一个|原先|曾经|此前|后来|早先|当时|本轮',
    '实测',
    'round\s*\d+|第\s*\d+\s*轮',
    '判据',
    '变异',
    'MUT-',
    '回归',
    '闸门',
    '审计',
    '教训',
    '防退回',
    '失效形态'
)
$markerRe = ($markers -join '|')

function IsCommentLine([string]$line) {
    $t = $line.TrimStart()
    return $t.StartsWith('//')
}

$files = @(Get-ChildItem $src -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })

if ($OnlyFiles.Count -gt 0) {
    $files = @($files | Where-Object { $OnlyFiles -contains $_.Name })
}

$totalRemoved = 0
$report = @()

foreach ($f in $files) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $nl = if ($text -match "`r`n") { "`r`n" } else { "`n" }
    $lines = $text -split "`r?`n"

    $out = New-Object 'System.Collections.Generic.List[string]'
    $i = 0
    $removedHere = 0
    $blocksHere = 0

    while ($i -lt $lines.Count) {
        if (IsCommentLine $lines[$i]) {
            $start = $i
            while ($i -lt $lines.Count -and (IsCommentLine $lines[$i])) { $i++ }
            $block = $lines[$start..($i - 1)]
            $joined = ($block -join "`n")

            $hit = $joined -match $markerRe
            if ($hit -and $block.Count -ge 2) {
                $removedHere += $block.Count
                $blocksHere++
                # 吞掉块后面的一个空行，避免连续空行
                if ($i -lt $lines.Count -and $lines[$i].Trim() -eq '') { $i++ }
                # 也吞掉块前面的一个空行（如果上一行已经是空行）
                if ($out.Count -gt 0 -and $out[$out.Count - 1].Trim() -eq '') {
                    $out.RemoveAt($out.Count - 1)
                    $out.Add('')
                }
                continue
            } else {
                foreach ($b in $block) { $out.Add($b) }
                continue
            }
        }
        $out.Add($lines[$i])
        $i++
    }

    if ($removedHere -gt 0) {
        $totalRemoved += $removedHere
        $report += [PSCustomObject]@{ File = $f.Name; Blocks = $blocksHere; Lines = $removedHere }
        if (-not $DryRun) {
            $newText = ($out -join $nl)
            $enc = New-Object System.Text.UTF8Encoding($hasBom)
            [IO.File]::WriteAllText($f.FullName, $newText, $enc)
        }
    }
}

$report | Sort-Object Lines -Descending | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ("删除注释行合计 {0} 行，涉及 {1} 个文件{2}" -f $totalRemoved, $report.Count, $(if ($DryRun) { '（试跑，未写盘）' } else { '' }))