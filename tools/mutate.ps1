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
$PAGES     = 'Pages\Pages.cs'
$ONGUI     = 'Ui\OnGui.cs'
$SERVICE   = 'Features\VideoRender\RenderService.cs'
$DATA      = 'Features\Recording\ReplayData.cs'
$GATE      = 'Features\VideoRender\ReadbackGate.cs'

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
    # ── UI 逻辑（用户报「你这个UI逻辑不合理」那一轮）──────────────
    #
    # 这一组有 16 条，形态上刻意做成**"看着还挺合理"的退回**，
    # 而不是随便造个错。理由是本文件开头写的那句：造出来的错可能
    # 压根不走那条判据，那样即使判据是假的也会变红，等于白验。
    # 所以下面每一条都必须是"有人会真的这么改回去"的样子。
    'MUT-AS' {
        # ①a：把死开关原样加回来。勾它什么都不会发生——这就是
        # 「你这个UI逻辑不合理」里最容易再犯的一条。
        Backup $SETTINGS
        ReplaceExact $SETTINGS `
            '        internal static bool AutoSave = true;' `
            ('        internal static bool AutoSave = true;' + "`n" +
             '        /// <summary>MUT-AS：加回来的假开关。</summary>' + "`n" +
             '        internal static bool AsyncInput = true;')
    }
    'MUT-AT' {
        # ①b：**半删**状态。字段没了但配置读还在——读它只会静默拿
        # 默认值，下一个人看着代码会以为"还在兼容旧配置"。
        Backup $SETTINGS
        ReplaceExact $SETTINGS `
            '            Settings.AutoSave = GetBool(d, "AutoSave", Settings.AutoSave);' `
            ('            Settings.AutoSave = GetBool(d, "AutoSave", Settings.AutoSave);' + "`n" +
             '            Settings.AsyncInput = GetBool(d, "AsyncInput", Settings.AsyncInput);')
    }
    'MUT-AU' {
        # ①c：另一半。只把写盘那一条加回来。
        Backup $SETTINGS
        ReplaceExact $SETTINGS `
            '                    { "AutoSave", Settings.AutoSave },' `
            ('                    { "AutoSave", Settings.AutoSave },' + "`n" +
             '                    { "AsyncInput", Settings.AsyncInput },')
    }
    'MUT-AV' {
        # ②a：顶栏退回「只在渲染时才画」。这就是"开着录制打一整局，
        # 设置页顶部一片空白"。返��成早退之后 ②a 与 ②b 同时红。
        Backup $ONGUI
        ReplaceExact $ONGUI `
            '            if (rendering)' `
            ('            if (!rendering)' + "`n" +
             '            {' + "`n" +
             '                return;' + "`n" +
             '            }' + "`n" +
             '            if (rendering)')
    }
    'MUT-AW' {
        # ②b：早退没有，但**只漏掉"录制中"那一支**。
        # 只验 MUT-AV 的话，"忘了加录制那一支"这种最常见的改法照样
        # 全绿——而那正是用户报的第二个毛病。所以四个来源必须逐个点名。
        Backup $ONGUI
        ReplaceExact $ONGUI `
            '            else if (Features.Recording.KeyRecorder.IsRecording)' `
            ('            else if (Features.Playback.PlaybackEngine.IsPending)' + "`n" +
             '            {' + "`n" +
             '                text = Loc.T("status.playing");' + "`n" +
             '            }' + "`n" +
             '            else if (Features.Playback.PlaybackEngine.IsPlaying)')
    }
    'MUT-AX' {
        # ③a：把「开始渲染」按钮挪回实时分支——恒定无效的按钮复活。
        # 形态是**复制**一份过去、原来那份不删：这样"只查按钮还在不在"
        # 的断言仍然是绿的，只有"live 支里不许有它"才抓得住。
        Backup $PAGES
        ReplaceExact $PAGES `
            '                OnGui.Note("render.note.live");' `
            ('                OnGui.Note("render.note.live");' + "`n" +
             '                OnGui.Button("render.start",' + "`n" +
             '                    () => Features.VideoRender.RenderService.StartManual(), true);')
    }
    'MUT-AY' {
        # ③b：离线分支不再写出目标文件——回到"静默取最新那份"。
        # 这个状态**不是完全失效**：按钮还是能点、也真的开始渲染，
        # 只是玩家无从知道渲染的是哪一局，渲染完才发现不对已经浪费几分钟。
        # 所以判据只能落在"有没有把目标写出来"，不能落在"能不能开始"。
        Backup $PAGES
        ReplaceExact $PAGES `
            '                    OnGui.NoteText(Loc.F("render.offline_target",' `
            '                    OnGui.NoteText(Loc.F("render.start",'
    }
    'MUT-AZ' {
        # ③d：取消按钮退回常年摆着（不再问会话状态）。
        Backup $PAGES
        ReplaceExact $PAGES '            if (running)' '            if (true)   // MUT-AZ'
    }
    'MUT-BA' {
        # ③e：StartManual 退回 void（界面层那几条仍然是绿的，因为按钮
        # 确实删掉了；只有服务层这条抓得住"接口失去了表达力"）。
        Backup $SERVICE
        #
        # **这里必须按行改，不能用 ReplaceExact 拼多行锚点。**
        # ReplaceExact 是逐行 `.Contains()` 再 `.Replace()`，它根本不处理
        # 换行——把三行拼成一个带 "`n" 的字符串去匹配单行，命中恒为 0。
        # 而且这两处锚点单独用都会撞上"命中 N 次"：
        #   · `return false;` 在本文件出现 3 次（Live 空跑 / 没有回放 /
        #     已经在跑）；
        #   · `if (Settings.Mode == Settings.RenderMode.Live)` 出现 2 次
        #     （另一处在 OnLevelStart）。
        # 报"命中 N 次（应为 1）"看着像失败，其实是锚点写糊了、变异
        # 压根没生效——那恰恰是本脚本要防的假通过。
        # 所以这里跟 MUT-T 一样，用一个 inMethod 标志把作用域圈在方法体内。
        $out = @()
        $inMethod = $false
        foreach ($l in Lines $SERVICE) {
            $t = $l.Trim()
            if ($t -eq 'internal static bool StartManual()') {
                $inMethod = $true
                $out += '        internal static void StartManual()'
                continue
            }
            if ($inMethod) {
                if ($t -eq '}' -and -not $t.Contains('if')) {
                    # 方法体的收尾大括号（方法内没有别的裸 }）
                    $inMethod = $false
                }
                if ($t -eq 'return false;') {
                    $out += '                return;   // MUT-BA'
                    continue
                }
                if ($t -eq 'return OfflineSession.Start(recent);') {
                    $out += '            OfflineSession.Start(recent);   // MUT-BA'
                    continue
                }
            }
            $out += $l
        }
        SetLines $SERVICE $out
    }
    'MUT-BB' {
        # ④a：把「当前」分组挪回早退之后——回放页在没载入时整块消失。
        # 逻辑一点没变，只有"位置"变了，而位置就是玩家看到的现象。
        #
        # **必须按行改，不能拿 ReplaceExact 拼多行替换值。** ReplaceExact
        # 是逐行 Contains/Replace 后原样写回单行，替换值里的 "`n" 会留在
        # 文件里变成裸换行符，源码当场坏掉；源文本判据读不动坏源码时可能
        # 一条都不红，于是 exit 0 看着像"检出"，其实是变异把代码毁了——
        # 又一次假通过。凡是跨行结构的变异一律按行列表改。
        Backup $PAGES
        $lines = @(Lines $PAGES)
        $sig = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq 'private static void DrawPlaybackState()') {
                $sig = $i
                break
            }
        }
        if ($sig -lt 0) { throw '找不到 DrawPlaybackState 的定义' }
        $grp = -1
        $ifAt = -1
        for ($i = $sig; $i -lt $lines.Count; $i++) {
            if ($grp -lt 0 -and $lines[$i].Trim() -eq 'OnGui.Group("playback.current");') { $grp = $i }
            if ($ifAt -lt 0 -and $lines[$i].Trim() -eq 'if (!playing)') { $ifAt = $i }
        }
        if ($grp -lt 0 -or $ifAt -lt 0) { throw 'MUT-BB：分组或 if (!playing) 没找到' }
        # 从 if (!playing) 的大括号往下数，数到配平那一行就是块的收尾
        $depth = 0
        $blkEnd = -1
        for ($i = $ifAt; $i -lt $lines.Count; $i++) {
            $depth += ([regex]::Matches($lines[$i], '\{')).Count
            $depth -= ([regex]::Matches($lines[$i], '\}')).Count
            if ($depth -le 0 -and $i -gt $ifAt) { $blkEnd = $i; break }
        }
        if ($blkEnd -lt 0) { throw 'MUT-BB：if (!playing) 块没收尾' }
        # 现在的正确结构是「分组在前、早退在后」：$grp < $ifAt。
        # 这个前提不成立就说明代码又变了，得重新想这条变异想证明什么，
        # 不能让它继续跑下去——否则下面那段搬迁会搬到一个别的语义上。
        if (-not ($grp -lt $ifAt)) {
            throw ('MUT-BB：分组不在早退之前（分组在第 ' + $grp +
                ' 行、if 在第 ' + $ifAt + ' 行），代码结构与预期不符')
        }
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -eq $grp) { continue }
            $out += $lines[$i]
            # 搬到 if (!playing) 整块**之后**——这就是旧代码的原样：
            # 空状态一 return，这一组整块都不画了。
            if ($i -eq $blkEnd) {
                $out += $lines[$grp]
            }
        }
        SetLines $PAGES $out
    }
    'MUT-BC' {
        # ④b：空闲状态不再说话（只在失败时提示）。
        # 这是最像"已经修好了"的半成品：失败有了，空状态还是空白。
        Backup $PAGES
        ReplaceExact $PAGES `
            '                    ? Loc.T("playback.idle")' `
            '                    ? Loc.T("playback.idle_x")'
    }
    'MUT-BD' {
        # ⑤a：文本框退回每敲一键提交一次。
        # 最容易改回去的一条——看起来只是"多个循环"，没人会觉得那是 bug。
        Backup $ONGUI
        ReplaceExact $ONGUI `
            '            _textDraft = GUILayout.TextField(_textDraft ?? "",' `
            ('            string now = GUILayout.TextField(_textDraft ?? "",')
        ReplaceExact $ONGUI `
            '            GUI.SetNextControlName(RowInputControlName);' `
            ('            GUI.SetNextControlName(RowInputControlName);' + "`n" +
             '            if (_textDraft != now) { CommitText(); }')
    }
    'MUT-BE' {
        # ⑤c：把控件名改回带点号的形态。
        # **已知后果**：LocCheck.CollectUsedKeys() 会把它当成漏翻的文案键，
        # 闸门报「两种语言都没有」。所以这条预期就是红的——
        # 它证明的正是"这个坑真的存在"，⑤c 那条断言才有意义。
        Backup $ONGUI
        ReplaceExact $ONGUI `
            '        private const string RowInputControlName = "OutDirBox";' `
            '        private const string RowInputControlName = "render.outdir";'
    }
    'MUT-BF' {
        # ⑥c：进度条不再先判总帧数。
        # 原版就是 `fill <= 0f` 才 return，而 0/0 得 NaN、Clamp01 的 <
        # 与 > 对 NaN 全为假，于是 NaN 原样返回，一路走进
        # GUI.DrawTexture(r.width * NaN) 抛 ArgumentException。
        # 症状是点开渲染页整个 OnGUI 被打断，而它恰好发生在
        # "还没开始渲染就翻到这一页"——最容易撞上的时候。
        Backup $PAGES
        ReplaceExact $PAGES `
            '            int total = Features.VideoRender.RenderStatus.FramesTotal;' `
            '            int total = 1;   // MUT-BF: 拿掉判空'
    }
    'MUT-BG' {
        # ⑥b：实时录制**只去掉收尾那一处**清零（开录那一处留着）。
        # 判据要求两处都在，所以只动一处就会红——这正是"⑥b 钉两处"的理由，
        # 也让"顺手只写一处"的半成品过不去。
        #
        # 两处 `RenderStatus.FramesTotal = 0;` 文本完全一样，ReplaceExact
        # 会报"命中 2 次"。所以按行号改，并先把两处的行号核实一遍：
        # 开录那处在 SetupCapture 里、前面挨着 EtaSeconds；收尾那处
        # 后面挨着 `Debug.Log("[SpectreReplay] 录制完成`。
        Backup $LIVE
        $lines = @(Lines $LIVE)
        $hit = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq 'RenderStatus.FramesTotal = 0;') { $hit += $i }
        }
        if ($hit.Count -ne 2) {
            throw ('MUT-BG：预期两处清零，实得 ' + $hit.Count +
                ' 处（' + ($hit -join ',') + '），代码结构与预期不符')
        }
        # 后面那处 = 收尾；用后面那行是不是"录制完成"日志来确认方向，
        # 免得把开录那一处误删（那样 ⑥b 照样红，但证明的是另一件事）。
        $tail = $hit[1]
        $after = ''
        for ($i = $tail + 1; $i -lt [Math]::Min($tail + 5, $lines.Count); $i++) {
            $after += $lines[$i]
        }
        if ($after -notmatch '录制完成') {
            throw 'MUT-BG：第二处清零后面不是"录制完成"，方向判反了'
        }
        $lines[$tail] = '                // MUT-BG: 收尾不再清零'
        SetLines $LIVE $lines
    }
    'MUT-BH' {
        # ⑥d：离线不再真写 ETA，把字段钉死成 -1。
        # 这一版曾经**让闸门保持全绿**（exit 1）——因为原判据只查
        # `EtaSeconds\s*=`，"钉死成 -1"照样满足。于是"ETA 分支仍然
        # 不会成立"这件事在闸门里根本没人管。
        # 判据已改成要求一次带条件的真赋值，见 UiLogicCheck ⑥d。
        Backup $OFFLINE
        ReplaceExact $OFFLINE `
            '            RenderStatus.EtaSeconds = elapsed > 0.05' `
            '            RenderStatus.EtaSeconds = -1;   // MUT-BH'
    }
    # ── 反向对照：注释里塞一个绝对不该命中的封禁词 ──────────
    #
    # 六组判据里有几条是"某段源码里不许出现某个词"（③a live 支不许有
    # render.start、⑥c 不许再用 BarFill、①e 不许有 record.asyncinput）。
    # 那些查的是**剥过注释之后**的源码文本。而本项目的注释里频繁要提到
    # 被封禁的词本身——"这里原先有一个 BarFill 属性"就含 `BarFill`。
    # 哪天判据忘了剥注释，它就会把自己的说明当违规，闸门天天红，
    # 而没有人说得清是哪一条坏了。
    #
    # 这条变异在**注释里**塞三个封禁词，闸门必须保持全绿：
    # 它证明"剥注释这一步还在跑"，而那是上面那几条能成立的前提。
    'MUT-BZ' {
        Backup $ONGUI
        $lines = @(Lines $ONGUI)
        $at = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq 'internal static void RowInput(string labelKey)') {
                $at = $i
                break
            }
        }
        if ($at -lt 0) { throw 'MUT-BZ：找不到 RowInput 的定义' }
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $out += $lines[$i]
            if ($i -eq $at) {
                # BarFill / record.asyncinput / render.start 都是被封禁的词，
                # 这里只作为说明出现在注释里。剥注释要是没生效，③a/⑥c/①e
                # 会立刻红。
                $out += '            // 说明用词（注释里不该被当成代码）：'
                $out += '            //   RenderStatus.BarFill / record.asyncinput / render.start'
            }
        }
        SetLines $ONGUI $out
    }
    # ── 回放文件 v4：两段实体的写入顺序退回旧样子 ──────────────
    #
    # 旧样子是「状态轨 → Keys」，而读端一直是「Keys → 状态轨」。
    # 这不是"少测一种组合"，是**每一局真实录制出来的回放都读不对**：
    # 读端从「读 Keys」那一步起整体错位，而它不一定抛异常——
    # 两段实体的字节数够读时，文件"读得回来、回放能开、渲染能跑"，
    # 只是整局数据全错。原来 136 条断言一条都没抓到，因为往返用例
    # 被拆成了互斥的两组（一组只有按键、一组只有状态轨），
    # 写端与读端各自跳过空段、恰好对上。真实的一局两条轨都有。
    #
    # 这里必须**按行搬整块**：只在文件里交换两段实体的先后。
    # 搬完仍是合法 C#（预览器链接真实源码、当场编译），不会把
    # "变异把代码改坏了"伪装成"断言被检出"。
    'MUT-CA' {
        Backup $DATA
        $lines = @(Lines $DATA)
        $hitK = $lines | Select-String -SimpleMatch 'foreach (var k in data.Keys)' |
            Select-Object -First 1
        $hitH = $lines | Select-String -SimpleMatch 'foreach (var h in data.Hits)' |
            Select-Object -First 1
        if (-not $hitK -or -not $hitH) { throw 'MUT-CA：找不到两段实体的写入循环' }
        $kAt = $hitK.LineNumber - 1
        $hAt = $hitH.LineNumber - 1
        if (-not ($kAt -lt $hAt)) {
            throw ('MUT-CA：期望写端是 Keys 在前（Keys 在第 ' + $kAt +
                ' 行、状态轨在第 ' + $hAt + ' 行），代码结构与预期不符')
        }
        function BlockEnd([string[]]$ls, [int]$from) {
            $depth = 0
            for ($i = $from; $i -lt $ls.Count; $i++) {
                $depth += ([regex]::Matches($ls[$i], '\{')).Count
                $depth -= ([regex]::Matches($ls[$i], '\}')).Count
                if ($depth -le 0 -and $i -gt $from) { return $i }
            }
            throw 'MUT-CA：括号没配平'
        }
        $kEnd = BlockEnd $lines $kAt
        $hEnd = BlockEnd $lines $hAt
        $out = @()
        $out += $lines[0..($kAt - 1)]              # 头部（含 hitCount 与说明注释）
        $out += $lines[$hAt..$hEnd]                # 状态轨整块搬到前面
        $out += $lines[($kEnd + 1)..($hAt - 1)]    # 中间什么都没变
        $out += $lines[$kAt..$kEnd]                # Keys 整块搬到后面
        $out += $lines[($hEnd + 1)..($lines.Count - 1)]
        SetLines $DATA $out
    }
    # ── 回读闸门：把"先置位"退回"先发请求"（整屏模式永久卡在第 1 帧） ──
    #
    # 整屏取帧那条路（ReadPixels）的回调是**同步**的：request() 还没返回，
    # 回调就已经在里面跑完并把在飞标记清掉了。置位若写在 request() 之后，
    # 就落在清位之后 —— 再没有人清它，第 2 帧起一帧都排不上。
    # 表现：Live 模式成片是第一帧的静止画面（时长与音轨都对，看着能用、
    # 内容全错），离线模式只产出 1 帧，且全程不报错、不停机。
    #
    # 这条只改**置位的位置**，改成原来的形状，其余一字不动：
    # 它必须被行为断言 ①（同步回调之后闸门是否放开）抓到，
    # 而结构断言 ⑤/⑥ 在变异下应当保持绿——源码形状本来就"像对的"。
    'MUT-CB' {
        Backup $GATE
        $lines = @(Lines $GATE)
        $setAt = -1
        $retAt = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '_pending = true;') { $setAt = $i }
            if ($lines[$i].Trim() -eq 'return queued;') { $retAt = $i }
        }
        if ($setAt -lt 0 -or $retAt -lt 0 -or $retAt -le $setAt) {
            throw ('MUT-CB：找不到 TryStart 的置位与返回（置位在第 ' + $setAt +
                ' 行、返回在第 ' + $retAt + ' 行），代码结构与预期不符')
        }
        $out = @()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($i -eq $setAt) {
                $out += '            // MUT-CB：置位搬到了请求返回之后（原来的形状）'
                continue
            }
            if ($i -eq $retAt) {
                $out += '            if (queued)'
                $out += '            {'
                $out += '                _pending = true;   // MUT-CB'
                $out += '            }'
            }
            $out += $lines[$i]
        }
        SetLines $GATE $out
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
