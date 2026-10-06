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
    [switch]$SkipDeploy,
    [switch]$SkipAudits
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

# 只读审计脚本（离线、不起游戏、不改任何文件）。
# **每个都必须登记在这里**：2026-10-06 抓到一次真孤儿——四个补丁审计脚本
# （harmony-shape / instance-injection / patch-sig / result-injection）写完后
# 只在当轮手工跑过，从没进过闸门。不登记 = 一次都不自动跑 = 等于没写，
# 而闸门只会更绿。LevelProbeCheck 也栽过同样的跟头。
#
# **输出只留摘要。** deadcode-audit 是"报告型"脚本，一次吐 100 行；
# 全量贴进闸门会把真正的 ✗ 埋掉（第一次接进来就这样，绿色的部署摘要被
# 一百行灰字推到屏幕外）。判据：正常时只印一行摘要，出问题时才贴详情——
# 而"出问题"由退出码判定，不靠人眼扫输出。
function RunAudits {
    $audits = @(
        @{ Name = 'harmony-shape-audit.ps1';      Label = 'Harmony 补丁签名形状（32 处）' }
        @{ Name = 'instance-injection-audit.ps1'; Label = '__instance 只对实例方法（16 处）' }
        @{ Name = 'patch-sig-audit.ps1';          Label = '普通形参与目标同名同型（7 处）' }
        @{ Name = 'result-injection-audit.ps1';   Label = '__result 目标均有返回类型（3 处）' }
        @{ Name = 'deadcode-audit.ps1';           Label = '零调用点静态方法（报告型）' }
        @{ Name = 'settings-ui-audit.ps1';        Label = '设置项是否有界面入口（报告型）' }
        # port-diff-audit **是真门禁，不是报告型**：它拿一份「有意偏离上游登记表」
        # 逐条比对，挂不上的差异直接 exit 1。加了它，"抄错上游"从此会被抓到，
        # 而"我们有意改的"必须先在登记表里加一行（含原因）——这正是 round 59/60
        # 立的约定。它也曾一度是孤儿工具（写完只在当轮手工跑过），见 MUT 之外的教训：
        # 任何新脚本不加进这个表就等于没写。
        @{ Name = 'port-diff-audit.ps1';           Label = '移植保真度（每处差异须挂修改记录）' }
    )
    foreach ($a in $audits) {
        $p = Join-Path $root "tools\$($a.Name)"
        if (-not (Test-Path $p)) {
            Write-Host "  ✗ 审计脚本不存在：$($a.Name)（注册表与磁盘不一致）" -ForegroundColor Red
            $script:fail++
            continue
        }
        $tmp = [IO.Path]::GetTempFileName()
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $p > $tmp 2>&1
            $code = $LASTEXITCODE
            $out = @(Get-Content $tmp -ErrorAction SilentlyContinue)
            if ($code -eq 0) {
                # 取脚本自己印的统计行，凑成一行摘要
                $summary = (($out | Where-Object { $_ -match '全部通过|共 \d+ 个|不合规' } | Select-Object -First 2) -join '；')
                if ([string]::IsNullOrWhiteSpace($summary)) { $summary = '退出码 0' }
                Write-Host ("  ✓ {0}  →  {1}" -f $a.Label, $summary.Trim()) -ForegroundColor DarkGray
            } else {
                Write-Host "  ✗ $($a.Label)  →  退出码 $code" -ForegroundColor Red
                # 报告型审计（deadcode / settings-ui）失败时**只贴统计行**，不贴全表：
                # 它们动辄上百行，全贴会把真正的 ✗ 埋掉，也不会有人读。
                # 统计行认 "=== " 与几个分类关键字；匹配不到才退回复印末 12 行。
                $detail = @($out | Where-Object { $_ -match '^=== |wired|UI 壳|无界面但被消费|零读取|不合规|全部通过' })
                if ($detail.Count -eq 0) { $detail = @($out | Select-Object -Last 12) }
                $detail | Select-Object -Last 12 | ForEach-Object { Write-Host ('      ' + $_) }
                $script:fail++
            }
        } catch {
            Write-Host "  ✗ $($a.Label)  →  抛异常：$($_.Exception.Message)" -ForegroundColor Red
            $script:fail++
        } finally {
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }
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
        # 必须 --no-incremental：预览器链接的是**外部**文件（Gfx.cs、
        # PanelHost.cs、UiFonts.cs …），改完还原之后普通的增量构建察觉不到，
        # 跑出来还是上一次的二进制。已经因此误判过两次"修复没生效"。
        #
        # **构建失败必须判红，不能把输出丢掉。** 原先这里写的是
        # `| Out-Null`，于是预览器编译不过时后面 --no-build 去跑一个
        # 不存在的 exe，报的是"系统找不到指定的文件"——把"代码写错了"
        # 伪装成"环境有问题"，排查方向整个带偏（这次就撞上了）。
        $buildOut = & dotnet build -c Release -v q --nologo --no-incremental 2>&1
        $buildErrs = @($buildOut | Select-String -Pattern 'error CS')
        if ($buildErrs.Count -gt 0) {
            $buildErrs | Select-Object -Unique -First 8 | ForEach-Object {
                Write-Host ('  ✗ 预览器编译不过：' + $_.Line.Trim()) -ForegroundColor Red
            }
            $script:fail++
            Pop-Location
            return
        }
        $out = & dotnet run -c Release -v q --no-build -- $outDir --click 2>&1
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

if (-not $SkipAudits) {
    Step '只读审计（Harmony 形参 / 死代码）' { RunAudits }
}

Step '部署一致性' {
    $built = Join-Path $root 'SpectreReplay\bin\Release\SpectreReplay.dll'
    $mod = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\SpectreReplay'
    $live = Join-Path $mod 'SpectreReplay.dll'
    # 取最后一条**非空**输出：deploy.ps1 结尾是个空行，直接 -Last 1 会拿到
    # 它，看起来像"部署脚本啥也没干"。这里踩过一次，误以为部署失败。
    if (-not $SkipDeploy) {
        # 部署失败（游戏在跑 = dll 被锁）**必须记成一项失败，不能让它抛出来**。
        # 原先直接 & 调用，deploy.ps1 的 throw 会把整个闸门掀掉：
        # 后面「Spectre 未被污染」整段不跑，末尾也没有汇总——
        # 看上去像"闸门崩了"，而不是"部署这一项没过"。
        # 游戏开着是常态，不是异常。
        $d = ''
        try {
            $d = & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $root 'tools\deploy.ps1') -Config Release 2>&1 |
                Where-Object { $_.ToString().Trim() } | Select-Object -Last 1
            Write-Host ('  ' + $d)
        } catch {
            Write-Host ('  ✗ 部署失败：' + $_.Exception.Message) -ForegroundColor Red
            Write-Host '    （游戏在跑时 dll 被锁，先关掉再部署）' -ForegroundColor DarkGray
            $script:fail++
        }
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
    # 只看**已跟踪**文件的改动（git status 的第一列非 ??）。
    #
    # 原先用 `status --porcelain` 全量判定，结果被未跟踪目录误伤：
    # .zcode/ 下出现了别人留下的反编译产物（第三方库，不是我们的代码），
    # 于是闸门报"绝不该动它"——而 Spectre 的源码一个字节都没变。
    #
    # 判据仍然是硬的：只要有任何一个已跟踪文件被改过或被删，就红。
    # 未跟踪目录（?? 前缀）不归我们管，也无权替别人删。
    $dirty = @(& git status --porcelain |
        Where-Object { $_ -notmatch '^\?\?' })
    $untracked = @(& git status --porcelain |
        Where-Object { $_ -match '^\?\?' })
    $head = (& git log --oneline -1)
    Pop-Location
    if ($dirty.Count -gt 0) {
        Write-Host '  ✗ Spectre 有已跟踪文件被改动——绝不该动它' -ForegroundColor Red
        foreach ($d in $dirty) { Write-Host "      $d" -ForegroundColor Red }
        $script:fail++
    } else {
        $note = if ($untracked.Count -gt 0) {
            "（有 $($untracked.Count) 个未跟踪目录，非本工程产物，不计）"
        } else { '' }
        Write-Host "  ✓ 已跟踪文件干净$note（$head）"
    }
}

Write-Host ''
if ($fail -eq 0) {
    Write-Host '全部通过' -ForegroundColor Green
} else {
    Write-Host "$fail 项不通过" -ForegroundColor Red
}
exit $fail
