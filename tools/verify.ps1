<#
    注意：**这个文件必须存成 UTF-8 带 BOM**。PowerShell 5.1 按 ANSI 读脚本，
    没有 BOM 就会把中文读成乱码，连字符串引号都配不上，直接语法错误。
    而 write/edit 工具写文件时默认不带 BOM——每次改完都得补回来。

    一次性跑完所有离线验证，任何一项红就返回非零。

    为什么要单独一个闸门：预览器和主项目编译的是**不同的源码集**。
    预览器是 net10.0-windows，不链接 Features\（那些文件反过来依赖引擎），
    主项目是 netstandard2.1。于是完全可能出现"预览器全绿、主项目编译不过"
    ——上一轮就真踩了：改完没看构建结果就把编译不过的代码提交了。
    把两边绑在一起，红了就一定会被看见。
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipPreview,
    [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fail = 0

function Step($name, [scriptblock]$body) {
    Write-Host ''
    Write-Host "── $name " -ForegroundColor Cyan -NoNewline
    Write-Host ('─' * [Math]::Max(0, 60 - $name.Length))
    & $body
}

Step '主项目构建' {
    Push-Location $root
    $out = & dotnet build 'SpectreReplay.slnx' -c Release --no-incremental -v m --nologo 2>&1
    Pop-Location
    $errs = @($out | Select-String -Pattern 'error CS')
    $warns = @($out | Select-String -Pattern 'warning CS')
    if ($errs.Count -gt 0) {
        $errs | Select-Object -Unique -First 8 | ForEach-Object {
            Write-Host ('  ✗ ' + $_.Line.Trim()) -ForegroundColor Red
        }
        Write-Host "  ✗ 主项目编译不过" -ForegroundColor Red
        $script:fail++
    } else {
        Write-Host ('  ✓ 0 错误' + $(if ($warns.Count -gt 0) { "，$($warns.Count) 警告" } else { '，0 警告' }))
    }
}

if (-not $SkipPreview) {
    Step '离线验证套件' {
        Push-Location (Join-Path $root 'tools\UiPreview')
        $outDir = Join-Path $env:TEMP 'uipreview'
        $out = & dotnet run -c Release -v q -- $outDir --click 2>&1
        Pop-Location
        $lines = $out
        $bad = @($lines | Where-Object { $_ -match '^\s+✗' })
        $good = @($lines | Where-Object { $_ -match '^\s+✓' })
        foreach ($g in $good) { Write-Host ('  ' + $g.Trim()) -ForegroundColor DarkGray }
        foreach ($b in $bad) { Write-Host ('  ' + $b.Trim()) -ForegroundColor Red }
        Write-Host ('  断言 ' + ($good.Count + $bad.Count) + ' 条，失败 ' + $bad.Count)
        if ($bad.Count -gt 0) { $script:fail++ }
    }
}

Step '部署一致性' {
    $built = Join-Path $root 'SpectreReplay\bin\Release\SpectreReplay.dll'
    $mod = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\SpectreReplay'
    $live = Join-Path $mod 'SpectreReplay.dll'
    # 取最后一条**非空**输出：deploy.ps1 结尾是个空行，直接 -Last 1 会拿到
    # 它，看起来像"部署脚本啥也没干"。这里踩过一次，误以为部署失败。
    if (-not $SkipDeploy) {
        $d = & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $root 'tools\deploy.ps1') -Config Release 2>&1 |
            Where-Object { $_.ToString().Trim() } | Select-Object -Last 1
        Write-Host ('  ' + $d)
    }
    if (-not (Test-Path $built)) {
        Write-Host '  ✗ 构建产物不存在' -ForegroundColor Red
        $script:fail++
    } elseif (-not (Test-Path $live)) {
        Write-Host '  ✗ 线上 dll 不存在' -ForegroundColor Red
        $script:fail++
    } elseif ((Get-FileHash $built -Algorithm SHA256).Hash -eq (Get-FileHash $live -Algorithm SHA256).Hash) {
        Write-Host '  ✓ 线上 dll 与构建产物一致'
    } else {
        Write-Host '  ✗ 线上 dll 与构建产物不一致（没部署？）' -ForegroundColor Red
        $script:fail++
    }
}

Step 'Spectre 未被污染' {
    Push-Location 'D:\Projects\Spectre'
    $dirty = @(& git status --porcelain)
    $head = (& git log --oneline -1)
    Pop-Location
    if ($dirty.Count -gt 0) {
        Write-Host '  ✗ Spectre 工作区有改动——绝不该动它' -ForegroundColor Red
        $script:fail++
    } else {
        Write-Host "  ✓ 干净（$head）"
    }
}

Write-Host ''
if ($fail -eq 0) {
    Write-Host '全部通过' -ForegroundColor Green
} else {
    Write-Host "$fail 项不通过" -ForegroundColor Red
}
exit $fail
