<#
    变异验证：把修复退回旧样子，看闸门会不会变红。

    **为什么需要它。** 一条断言只要是"看起来合理"的，就很可能根本没接上：
    判据搜的名字在生产代码里根本没被调用、或者那条分支根本走不到，
    于是真代码改坏了检查照样全绿。这一轮已经靠它抓到过 8 个真问题，
    也抓到过 3 条我自己写反的断言和 2 条恒真断言——没有它，
    这些都会以"已验证"的姿态留在闸门里。

    **变异必须是"退回旧样子"，不是随便造个错。** 造出来的错可能压根
    不走那条判据，那样即使判据是假的也会变红，等于白验。

    用法：
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\mutate.ps1 MUT-O
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\mutate.ps1 MUT-O -NoRun
    跑完自动还原原文件。

    注意：**这个文件必须存成 UTF-8 带 BOM**（同 verify.ps1）。
#>
param(
    # 变异名，见下面 switch。
    #
    # **没有"只施加不跑"的开关**：那种用法迟早会留下一个没还原的变异，
    # 而"工作树里静悄悄地少了一个修复"比没有这个脚本更糟。
    # 要手工确认就先跑本脚本（它自己会还原），再改文件。
    [Parameter(Mandatory = $true)][string]$Which
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'SpectreReplay'
$utf8 = New-Object Text.UTF8Encoding($false)
$touched = New-Object System.Collections.ArrayList

function PathOf([string]$rel) { Join-Path $src $rel }

# 备份一次；同一文件被两条变异用到也只备份一次
function Backup([string]$rel) {
    $p = PathOf $rel
    if (-not (Test-Path $p)) { throw "找不到 $rel" }
    $saved = Join-Path $env:TEMP ('mut_' + $Which + '_' + ($rel -replace '[\\/]', '__'))
    Copy-Item $p $saved -Force
    [void]$touched.Add(@{ Rel = $rel; Saved = $saved })
}

function Lines([string]$rel) { [IO.File]::ReadAllLines((PathOf $rel)) }

# 整行数组替换。**不要用 here-string + .Contains()**：换行或缩进差一个字
# 就匹配不上，而变异**静默没生效**、闸门照样全绿——那比不变异更坏，
# 因为它会让人以为"这条变异验过了"。
function SetLines([string]$rel, [string[]]$out) {
    [IO.File]::WriteAllLines((PathOf $rel), $out, $utf8)
}

# 精确替换整行里的子串，且要求只命中一次
function ReplaceExact([string]$rel, [string]$old, [string]$new) {
    $lines = Lines $rel
    $hit = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($old)) {
            $lines[$i] = $lines[$i].Replace($old, $new)
            $hit++
        }
    }
    if ($hit -ne 1) { throw "$rel 里 '$old' 命中 $hit 次（应为 1）" }
    SetLines $rel $lines
}

function Cleanup() {
    foreach ($t in $touched) {
        Copy-Item $t.Saved (PathOf $t.Rel) -Force
        Remove-Item $t.Saved -Force
    }
}

trap {
    Cleanup
    Write-Host "变异失败并已还原：$($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$OFFLINE = 'Features\VideoRender\OfflineSession.cs'
$STATE   = 'Features\Playback\HitStateReplayer.cs'
$LOADER  = 'Patch\PatchLoader.cs'
$LIFE    = 'Features\Playback\Patch_Lifecycle.cs'
$ENGINE  = 'Features\Playback\PlaybackEngine.cs'
$HITREC  = 'Features\Recording\Patch_HitRecord.cs'

switch ($Which) {
    # 写端（Set 侧）整个删掉，只留读端 —— 上一版的"只做一半"
    'MUT-O' {
        Backup $OFFLINE
        $lines = Lines $OFFLINE
        $hit = $lines | Select-String -SimpleMatch 'private static void PinSetPrefix' |
            Select-Object -First 1
        if (-not $hit) { throw '找不到 PinSetPrefix' }
        $sig = $hit.LineNumber - 1          # 0 基
        $from = $sig - 2                    # 连同上面两行 [GamePatch(...)]
        $depth = 0
        $end = $sig
        for ($i = $sig; $i -lt $lines.Count; $i++) {
            $depth += ([regex]::Matches($lines[$i], '\{')).Count
            $depth -= ([regex]::Matches($lines[$i], '\}')).Count
            if ($depth -le 0 -and $i -gt $sig) { $end = $i; break }
        }
        SetLines $OFFLINE ($lines[0..($from - 1)] + $lines[($end + 1)..($lines.Count - 1)])
    }
    # 把 Spectre 那条 `+ clock.Time * pitch` 照搬过来（多乘一次 pitch）
    'MUT-P' {
        Backup $OFFLINE
        ReplaceExact $OFFLINE 'PinnedSongPosition = _frameIndex / (double)_fps;' `
            'PinnedSongPosition = _frameIndex / (double)_fps * pitch;   // MUT-P'
    }
    # freeroam 门控恒放行（等于没挂这个 Prefix）
    'MUT-Q' {
        Backup $STATE
        ReplaceExact $STATE `
            'return _hits[Cursor].FreeRoamSection == controller.curFreeRoamSection;' `
            'return false;   // MUT-Q'
    }
    # 结束判据改回读被钉住接管后的 songposition_minusi
    'MUT-R' {
        Backup $OFFLINE
        ReplaceExact $OFFLINE 'double pos = OfflineSession.LastRealSongPosition;' `
            'double pos = scrConductor.instance.songposition_minusi;   // MUT-R'
    }
    # Accessor 退回 internal（当天那条 CS0617）
    'MUT-S' {
        Backup $LOADER
        ReplaceExact $LOADER 'public PropertyAccessor Accessor;' `
            'internal PropertyAccessor Accessor;   // MUT-S'
    }
    # 先替换 value 之后才抄真值 → 抄到的是钉扎值自己
    'MUT-T' {
        Backup $OFFLINE
        $out = @()
        foreach ($l in Lines $OFFLINE) {
            $t = $l.Trim()
            if ($t -eq 'OfflineSession.LastRealSongPosition = value;') {
                $out += '                    value = OfflineSession.PinnedSongPosition;   // MUT-T'
                $out += '                    OfflineSession.LastRealSongPosition = value;'
                continue
            }
            if ($t -eq 'value = OfflineSession.PinnedSongPosition;') { continue }
            $out += $l
        }
        SetLines $OFFLINE $out
    }
    # 门控条件改成"下一条区段 +1 == 当前区段" → 正常回放时永不成立，等于没禁
    'MUT-U' {
        Backup $STATE
        ReplaceExact $STATE `
            'return _hits[Cursor].FreeRoamSection == controller.curFreeRoamSection;' `
            'return _hits[Cursor].FreeRoamSection + 1 == controller.curFreeRoamSection;   // MUT-U'
    }
    # ResolveTarget 不翻译属性名 → 属性补丁挂不上
    'MUT-V' {
        Backup $LOADER
        ReplaceExact $LOADER 'p.GetGetMethod(true)' 'p.GetGetMethod(false)'
    }
    # 整条生命周期拆除链退回"不存在"——这就是这一版的旧样子：
    # Stop() 只剩界面按钮一个调用点。
    # **必须整条拿掉，而不是把某个方法体掏空**：掏空之后 C# 根本编译不过，
    # 预览器读源码不受影响，但它会一路找到下一个挂点的方法体、读到人家的
    # Teardown()，闸门全绿——那是"变异形状不对"，不是"断言是假的"。
    'MUT-W' {
        Backup $LIFE
        $lines = Lines $LIFE
        $start = $null
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('internal static void Teardown()')) { $start = $i; break }
        }
        if ($null -eq $start) { throw '找不到 Teardown 定义' }
        $depth = 0
        $end = $start
        for ($i = $start; $i -lt $lines.Count; $i++) {
            $depth += ([regex]::Matches($lines[$i], '\{')).Count
            $depth -= ([regex]::Matches($lines[$i], '\}')).Count
            if ($depth -le 0 -and $i -gt $start) { $end = $i; break }
        }
        $out = @()
        foreach ($i in 0..$end) { $out += $lines[$i] }
        $out += '    }'
        $out += '}'
        SetLines $LIFE $out
    }
    # 退出挂点改成 Postfix → 场景已经切走，拆得太晚
    'MUT-X' {
        Backup $LIFE
        ReplaceExact $LIFE 'typeof(scrController), "QuitToMainMenu", PatchKind.Prefix' `
            'typeof(scrController), "QuitToMainMenu", PatchKind.Postfix   // MUT-X'
    }
    # 给退出挂点设上 "playback" 门控 —— 真实世界会踩的坑：
    # 回放没载入时该开关是 false，补丁压根不挂且日志里一句失败都没有。
    'MUT-Y' {
        Backup $LIFE
        ReplaceExact $LIFE 'typeof(scrController), "Restart", PatchKind.Prefix)' `
            'typeof(scrController), "Restart", PatchKind.Prefix, "playback")   // MUT-Y'
    }
    # 删掉空 Feature 的早退分支，退回"查不到的功能名 = 关闭"。
    # 这一条最恶劣：不挂载、不报失败、计数还看着正常。
    'MUT-Z' {
        Backup $LOADER
        $lines = Lines $LOADER
        $start = $null
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('if (string.IsNullOrEmpty(name))')) { $start = $i; break }
        }
        if ($null -eq $start) { throw '找不到空 Feature 的早退分支' }
        # 分支体是 `{` / `return true;` / `}` 三行
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -ge $start -and $i -le $start + 3) { continue }
            $out += $lines[$i]
        }
        SetLines $LOADER $out
    }
    # Teardown 不调 Stop → 四条挂点都变成空转（形状全对、行为没了）
    'MUT-AA' {
        Backup $LIFE
        ReplaceExact $LIFE 'PlaybackEngine.Stop();' '// MUT-AA: 忘了调 Stop'
    }
    # 四条挂点各写一份拆除代码（把 Restart 那条改成自己清数据），
    # 对照"必须共用一个入口"——改了三处忘了第四处是真实事故形态。
    #
    # **只能替换 Teardown() 那一行**：如果去替换整个方法体（连 `{` `}` 一起），
    # 判据 ④ 的"方法体必须在签名后面"会先一步变红——那是另一条断言的功劳，
    # 这条变异就算"被检出"也没验到它想验的东西。变异形状必须对准判据。
    'MUT-AB' {
        Backup $LIFE
        $lines = Lines $LIFE
        $hit = $lines | Select-String -SimpleMatch 'private static void RestartPrefix' |
            Select-Object -First 1
        if (-not $hit) { throw '找不到 RestartPrefix' }
        $at = $hit.LineNumber - 1
        $out = @()
        foreach ($i in 0..($lines.Count - 1)) {
            if ($i -eq ($at + 2)) {
                $out += '            PlaybackEngine.Stop();   // MUT-AB'
                continue
            }
            $out += $lines[$i]
        }
        SetLines $LIFE $out
    }
    # Stop() 里三件拆除之一挪到 `_data == null` 早退之后。
    # 退出钩子必然走这条早退路径（回放被中断时数据可能已清空），
    # 所以这不是"无害的重排"。
    'MUT-AC' {
        Backup $ENGINE
        $lines = Lines $ENGINE
        $start = $null
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('internal static void Stop()')) { $start = $i; break }
        }
        if ($null -eq $start) { throw '找不到 PlaybackEngine.Stop()' }
        $hit = $null
        for ($i = $start; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Contains('RestoreGlobalState();')) { $hit = $i; break }
        }
        if ($null -eq $hit) { throw '找不到 RestoreGlobalState() 调用' }
        $out = @()
        foreach ($i in 0..($lines.Count - 1)) {
            if ($i -eq $hit) { continue }
            # 插到 `if (_data == null)` 那行之后
            if ($lines[$i].Contains('if (_data == null)')) {
                $out += $lines[$i]
                $out += '                RestoreGlobalState();   // MUT-AC'
                continue
            }
            $out += $lines[$i]
        }
        SetLines $ENGINE $out
    }
    # 双轨互锁退化成"什么都不做" → 键轨的判定列表混进整局回放的判定
    'MUT-AD' {
        Backup $HITREC
        $lines = Lines $HITREC
        $hit = $lines | Select-String -SimpleMatch 'if (PlaybackEngine.HitLocked)' |
            Select-Object -First 1
        if (-not $hit) { throw '找不到 HitLocked 互锁' }
        $at = $hit.LineNumber - 1
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -eq ($at + 1)) { $out += '                    return true;   // MUT-AD'; continue }
            if ($i -eq ($at + 2)) { continue }   # 原来的 return false;
            $out += $lines[$i]
        }
        SetLines $HITREC $out
    }
    # 游戏把退出入口改名/改签名（对照 ⑥：那条断言必须真的去反射真程序集）
    'MUT-AE' {
        Backup $LIFE
        ReplaceExact $LIFE 'typeof(scrUIController), "WipeToBlack", PatchKind.Prefix' `
            'typeof(scrUIController), "WipeToBlackXXX", PatchKind.Prefix   // MUT-AE'
    }
    default { throw "未知变异 $Which" }
}

Write-Host "已施加 $Which" -ForegroundColor Yellow

# 只跑离屏套件：主项目编译会因为某些变异先挂掉（MUT-S 的 CS0617），
# 那样报出来的是编译错误而不是那条断言，白跑一趟。
# 预览器只把源码当**文本**读，所以这类变异照样能验。
Push-Location (Join-Path $root 'tools\UiPreview')
$b = & dotnet build -c Release -v q --nologo --no-incremental 2>&1
$berrs = @($b | Select-String -Pattern 'error CS')
if ($berrs.Count -gt 0) {
    Pop-Location
    Cleanup
    Write-Host '✗ 预览器自身编译不过（检查代码写错了，不是被测对象）' -ForegroundColor Red
    $berrs | Select-Object -First 5 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) -ForegroundColor Red }
    exit 2
}
$out = & dotnet run -c Release -v q --no-build -- (Join-Path $env:TEMP 'uipreview') --click 2>&1
Pop-Location

$bad = @($out | Where-Object { $_ -match '^\s+✗' })
$good = @($out | Where-Object { $_ -match '^\s+✓' })
foreach ($b2 in $bad) { Write-Host ('  ' + $b2.Trim()) -ForegroundColor Red }
Write-Host ('  断言 ' + ($good.Count + $bad.Count) + ' 条，失败 ' + $bad.Count)

Cleanup
Write-Host "已还原 $Which" -ForegroundColor Yellow

if ($bad.Count -eq 0) {
    Write-Host "✗ $Which 没有让闸门变红——这条断言是假的" -ForegroundColor Red
    exit 1
}
Write-Host "✓ $Which 被检出（失败 $($bad.Count) 条）" -ForegroundColor Green
exit 0