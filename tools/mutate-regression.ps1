<#
  全量变异回归：逐个跑 mutate.ps1 的每条变异，确认"该红的都红、该绿的全绿"。
  为什么值得周期性做：变异会**静默失效**——项目自己记过两次
  （ReplaceExact 多出的实参被吃掉、锚点命中多次）。表现是脚本报
  "这条断言是假的"，而真相是变异根本没施加。
  本脚本只判定，不修；退出码非 0 就列出所有问题项。
#>
param(
    # 只跑名字里有这些字的（调试用）
    [string]$Filter = ''
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$mut = Join-Path $root 'tools\mutate.ps1'
if (-not (Test-Path $mut)) { throw "找不到 $mut" }

$text = [IO.File]::ReadAllText($mut, [Text.Encoding]::UTF8)
$all = [regex]::Matches($text, "'(MUT-[A-Z0-9]+)'\s*\{") |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
if ($Filter) { $all = $all | Where-Object { $_ -like "*$Filter*" } }

# 反向对照（P0）单独处理：它要求全绿
$expectGreen = @('MUT-P0', 'MUT-AR', 'MUT-BZ')

$pass = New-Object System.Collections.Generic.List[string]
$fail = New-Object System.Collections.Generic.List[string]
$err  = New-Object System.Collections.Generic.List[string]

foreach ($m in $all) {
    $isGreen = $expectGreen -contains $m
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $mut, $m)
    if ($isGreen) { $args += '-ExpectGreen' }
    $out = & powershell @args 2>&1
    $code = $LASTEXITCODE
    $txt = ($out | Out-String)
    $detected = $txt -match '被检出|反向对照成立'
    $line = (($out | Where-Object { $_ -match '断言 \d+ 条' } | Select-Object -First 1))
    if ($line -eq $null) { $line = ($out | Select-Object -Last 1) }

    if ($isGreen) {
        if ($code -eq 0) { $pass.Add("$m (反向对照)") }
        else { $fail.Add("$m (反向对照) exit=$code  $line") }
        continue
    }
    # exit 2 = 变异自身跑不动（锚点未命中/预览器编译不过）；exit 1 = 断言是假的
    if ($code -eq 0 -and $detected) { $pass.Add("$m") }
    elseif ($code -eq 2) { $err.Add("$m exit=2（变异自身失效：锚点未命中或预览器编译不过）`n      $line") }
    else { $fail.Add("$m exit=$code（断言是假的）`n      $line") }
}

Write-Output ""
Write-Output "================ 全量变异回归结果 ================"
Write-Output ("通过 {0} / {1}" -f $pass.Count, $all.Count)
if ($err.Count) {
    Write-Output ""
    Write-Output "── 变异自身失效 {0} 条（比"断言假"更严重：根本没施加）──" -ForegroundColor Yellow
    $err | ForEach-Object { Write-Output "  $_" }
}
if ($fail.Count) {
    Write-Output ""
    Write-Output "── 断言是假的 {0} 条 ──" -ForegroundColor Red
    $fail | ForEach-Object { Write-Output "  $_" }
}
if ($err.Count -eq 0 -and $fail.Count -eq 0) {
    Write-Output "全部变异按预期工作" -ForegroundColor Green
}
exit ($err.Count + $fail.Count)
