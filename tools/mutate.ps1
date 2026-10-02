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
    [Parameter(Mandatory = $true)][string]$Which,

    # **反向对照**：这条变异必须让闸门**保持全绿**。
    #
    # 为什么要有这个开关：反向对照用来证明判据的另一半也在跑——
    # 比如"封禁词写在注释里不该变红"，证明剥注释那一步没被绕过。
    # 但本脚本原本的语义是"红了才算通过"（exit 1 = 这条断言是假的），
    # 反向对照天然会撞上它，于是要么被误当成坏变异而改掉，
    # 要么被人手工豁免——那等于没有这个对照。
    #
    # 用法：mutate.ps1 MUT-AR -ExpectGreen
    [switch]$ExpectGreen
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
#
# **必须校验实参个数。** PowerShell 的命令调用模式里 `+` 是普通实参、
# 不是运算符：`F 'a' 'b' + "`n" + 'c'` 会把 `$b` 定成 'b'，把
# `+`、`(换行)`、`+`、`c` 当成额外实参丢掉。ReplaceExact 只声明两个
# 形参，多出来的被静默忽略，于是**变异整个没生效**、闸门全绿——
# 恰好是本文件专门要防的那件事。
# 多半行的替换值必须写进括号：F 'a' ('b' + "`n" + 'c')。
function ReplaceExact([string]$rel, [string]$old, [string]$new) {
    if ($args.Count -ne 0) {
        throw ($rel + '：ReplaceExact 多出 ' + $args.Count +
            ' 个实参 —— 多半是替换值里的 + 被拆到了外面。PowerShell' +
            ' 会把 + 当成普通实参丢掉，那样变异根本没生效、闸门却全绿。' +
            '替换值要写进括号。实参：' + ($args -join ' | '))
    }
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
$CLAMPS  = 'Features\Playback\PlaybackClamps.cs'
$SETTINGS = 'Config\Settings.cs'
$RECORDING = 'Features\Recording\Patch_Recording.cs'
$LIVE      = 'Features\VideoRender\LiveSession.cs'

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
    # ── 偏差 24：回放期的死亡屏障 ──────────────────────────────
    # 屏障整个没了 —— 这是**修好之前**的样子：hitbox 死亡绕过 noFail，
    # 直接 ChangeState(States.Fail) + song.Stop()，成片断在一砖。
    # 必须整条删掉（连特性一起），只掏空方法体会被断言 ② 的
    # 「方法体必须紧跟特性」顶住，等于没验到想验的东西。
    'MUT-AF' {
        Backup $CLAMPS
        $lines = Lines $CLAMPS
        $hit = $lines | Select-String -SimpleMatch '[GamePatch(typeof(scrPlayer), "Die"' |
            Select-Object -First 1
        if (-not $hit) { throw '找不到 scrPlayer.Die 屏障' }
        $at = $hit.LineNumber - 1
        $depth = 0
        $end = $at
        $seen = $false
        for ($i = $at; $i -lt $lines.Count; $i++) {
            $depth += ([regex]::Matches($lines[$i], '\{')).Count
            $depth -= ([regex]::Matches($lines[$i], '\}')).Count
            if ($depth -le 0) { $seen = $true; $end = $i; break }
        }
        if (-not $seen) { throw '屏障的方法体括号没配平' }
        SetLines $CLAMPS ($lines[0..($at - 1)] + $lines[($end + 1)..($lines.Count - 1)])
    }
    # 判据反了：只判 !hitbox，漏掉 IsPlaying —— 玩家**正常游玩**时
    # 打错键的失败判定也被跳过（回放不在，屏障照样生效）。
    # 这一条钉的是"判据里的 IsPlaying 那一半"，② 要求两半都在。
    'MUT-AG' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS 'if (!PlaybackEngine.IsPlaying || !hitbox)' `
            'if (!hitbox)'
    }
    # 只判 IsPlaying，漏掉 !hitbox —— 命中/失败判定的普通失败
    # （打错键、判定窗外）整个消失，回放里的判定窗不再对应录制那一档。
    'MUT-AH' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS 'if (!PlaybackEngine.IsPlaying || !hitbox)' `
            'if (!PlaybackEngine.IsPlaying)'
    }
    # 挡下之后返回 true（让 Die 照跑）—— 屏障只写了日志，
    # 状态机照走，成片还是断在同一砖。对照 ② 的尾巴判据。
    # 定位必须**在屏障的方法体内**：MarkFailPrefix 里也有一处
    # `return false`，先撞上的会是它（第一版正是这样——改到了 :45，
    # 闸门反而全绿，等于验了个寂寞）。
    'MUT-AI' {
        Backup $CLAMPS
        $lines = Lines $CLAMPS
        $needle = '[GamePatch(typeof(scrPlayer), "Die"'
        $start = ($lines | Select-String -SimpleMatch $needle | Select-Object -First 1)
        if (-not $start) { throw '找不到屏障' }
        $out = @()
        $done = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ((-not $done) -and $i -gt $start.LineNumber - 1 -and
                $lines[$i].Trim() -eq 'return false;') {
                $out += ($lines[$i] -replace 'return false;',
                    'return true;   // MUT-AI：只记了日志，让 Die 照跑')
                $done = $true
                continue
            }
            $out += $lines[$i]
        }
        if (-not $done) { throw '屏障里没有 return false，MUT-AI 打偏了' }
        SetLines $CLAMPS $out
    }
    # 顺手把行星爆炸也拦了 —— 屏障的对象写错成 scrPlanet.Die，
    # 命中链路里 scrPlayer.Hit 走到的那次爆炸就永远不出现。对照 ⑤。
    'MUT-AJ' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS 'typeof(scrPlayer), "Die", PatchKind.Prefix, "playback"' `
            'typeof(scrPlanet), "Die", PatchKind.Prefix, "playback"'
    }
    # IgnoreMarkFail 的 finally 没了 —— MarkFailScope 里 body() 抛异常时
    # 标志永久留在 true，之后玩家整局看不到 miss 指示器。对照 ④。
    # 只匹配代码行（`try {` 的 `finally` 独占一行且缩进 12）：
    # `finally` 那个词在文档注释里也出现过。
    'MUT-AK' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS '            finally' '            // MUT-AK: 忘了还原'
    }
    # 特性对照 ①：在源码里真的用一次被封禁的 API。
    # （判据扫的是**去注释后**的全文，所以把词写进注释不该触发——
    #  这一点也要真验：见 MUT-AR，它往注释里塞一个封禁词）
    'MUT-AL' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS `
            '            if (!PlaybackEngine.IsPlaying || !hitbox)' `
            '            if (!PlaybackEngine.IsPlaying || !hitbox || scrMisc.GetHitMarginInSec(0, 0, 0, 0, 0) > 0)'
    }
    # 特性对照 ①：把自定判定的入口写进**字符串**（AccessTools 的绕法）
    'MUT-AM' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS `
            '            if (!PlaybackEngine.IsPlaying || !hitbox)' `
            '            if (!PlaybackEngine.IsPlaying || !hitbox || FeatureGate.IsEnabled("ScrMisc"))'
    }
    # 特性对照 ③：把 BGAMode 开关加回设置（哪怕默认 false）
    'MUT-AN' {
        Backup $SETTINGS
        ReplaceExact $SETTINGS `
            '        internal static bool AsyncInput = true;' `
            ('        internal static bool AsyncInput = true;' + "`n`n" +
             '        /// <summary>BGA 模式：屏蔽失败与判定飘字。</summary>' + "`n" +
             '        internal static bool BGAMode = false;')
    }
    # 特性对照 ④：把只读 Postfix 改成调用 AddHit
    # （"顺手补一下记账"是最可能的来由，而它没有任何症状）
    'MUT-AO' {
        Backup $RECORDING
        # 先把 __instance 接进来，否则那行 AddHit 编译不过，
        # 而编译不过就只会报"预览器挂了"，验不到判据。
        ReplaceExact $RECORDING `
            '        private static void HitPostfix(HitMargin hit)' `
            '        private static void HitPostfix(scrMarginTracker __instance, HitMargin hit)'
        ReplaceExact $RECORDING `
            '                KeyRecorder.RecordMargin(hit, KeyRecorder.SongPositionOfNow());' `
            ('                KeyRecorder.RecordMargin(hit, KeyRecorder.SongPositionOfNow());' +
             "`n" + '                __instance.AddHit(hit);   // MUT-AO')
    }
    # 特性对照 ⑥：帧栅格改用 Time.realtimeSinceStartup —— 跟随 timeScale，
    # 游戏一暂停（编辑器内回放正可能被暂停）栅格就停，音画永久错位
    'MUT-AP' {
        Backup $LIVE
        ReplaceExact $LIVE `
            'private readonly Stopwatch _clock = new Stopwatch();' `
            'private readonly Stopwatch _clockUnused = new Stopwatch();'
        ReplaceExact $LIVE `
            '=> Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;' `
            '=> Time.realtimeSinceStartup;'
    }
    # 特性对照 ⑤：照搬 Perf 目录那个自我统计（反射加载自身程序集）
    'MUT-AQ' {
        Backup $LOADER
        ReplaceExact $LOADER `
            '        private static bool _initialized;' `
            ('        private static bool _initialized;' + "`n" +
             '        private static int CountSelfPatches()' + "`n" +
             '        {' + "`n" +
             '            int n = 0;' + "`n" +
             '            foreach (Type t in Assembly.LoadFrom(typeof(PatchLoader).Assembly.Location).GetTypes())' + "`n" +
             '            {' + "`n" +
             '                n += t.GetCustomAttributes(false).Length;' + "`n" +
             '            }' + "`n" +
             '            return n;' + "`n" +
             '        }')
    }
    # 特性对照 ① 的**反向对照**：把封禁词写进注释。判据剥注释后扫，
    # 所以这条必须仍然全绿 —— 它证明 ① 不会被"注释里提一句 scrMisc"
    # 这种必要说明误伤。反向对照的作用是证明那半边判据也真的在跑。
    'MUT-AR' {
        Backup $CLAMPS
        ReplaceExact $CLAMPS `
            '            if (!PlaybackEngine.IsPlaying || !hitbox)' `
            ('            // 这里不能改成 scrMisc.GetHitMarginInSec：' +
             "`n" +
             '            if (!PlaybackEngine.IsPlaying || !hitbox)')
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

if ($ExpectGreen) {
    if ($bad.Count -eq 0) {
        Write-Host ("✓ $Which 反向对照成立：闸门保持全绿") -ForegroundColor Green
        exit 0
    }
    Write-Host "✗ $Which 反向对照被检出（失败 $($bad.Count) 条）" `
        -ForegroundColor Red
    Write-Host "  这一条本来该全绿。它变红说明判据把本该放行的东西也拦下了 ——" `
        -ForegroundColor Red
    Write-Host "  大概率是判据过严（例如忘了剥注释、或名字匹配没有忽略大小写）。" `
        -ForegroundColor Red
    exit 1
}

if ($bad.Count -eq 0) {
    Write-Host "✗ $Which 没有让闸门变红——这条断言是假的" -ForegroundColor Red
    exit 1
}
Write-Host "✓ $Which 被检出（失败 $($bad.Count) 条）" -ForegroundColor Green
exit 0