<#
  审计辅助（只读）：找出"定义了但全仓零外部调用"的静态方法。
  这类方法 = 移植时搬了数据类、却没搬驱动它的补丁的典型形态。

  **2026-10-06 修三处（否则它是一份永远全绿、且结论错的报告）**：
  ① 退出码原先恒为 0 ⇒ 接进 verify.ps1 后不管查出什么都显示 ✓。现在按
     「需人工判定的条目数」返回非 0，让闸门能真的因为它红。
  ② 调用点正则原先写 `(?<![\w.])Name\s*\(`，**把 `Class.Method()` 这种带点前缀的
     调用排除在外**，于是大量真调用被漏算成零调用（误报一轮，白查）。
     改成 `\bName\s*\(`，带点前缀也计数。
  ③ 不剥注释 ⇒ 注释里提一句方法名就算调用（漏报一轮，假阴性）。
     现在统一用 SrcUtil.Clean 剥掉 // 与 /* */ 再判。

  **为什么允许它"红"，以及为什么不是把所有零调用都判死**：
  227 个零调用方法里绝大多数是**设计使然**——Harmony 补丁类（由 GetTypes 扫
  [SpectrePatch] 发现）、公开 API 面（给别的 mod 反射）、UI 回调（UMM 反射）。
  这些都不该改。所以本脚本按**白名单类别**分组报数，只对"疑似漏接线"的那组
  返回非 0；白名单组零调用是正常的，仅作展示。
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'SpectreReplay'
# 读不到源目录时 Get-ChildItem 会去递归遍历父目录、在无关路径抛 AccessDenied
# （round 38 实测），报错指不到"路径指错了"这个根因。先在此处拦住。
if (-not (Test-Path $src)) {
    Write-Output "✗ 源目录不存在：$src"
    exit 3
}

# —— 剥注释（// 与 /* */），否则注释里提一句方法名就算"有调用" ——————————
$files = @(Get-ChildItem $src -Recurse -File -Filter *.cs -ErrorAction Stop |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
if ($files.Count -eq 0) {
    Write-Output "✗ 在 $src 下没扫到任何 .cs——计数为 0 之前先证明自己扫到了（假干净比误报危险）"
    exit 3
}

$stripped = @{}
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $t = [regex]::Replace($t, '(?m)//.*$', '')
    $t = [regex]::Replace($t, '/\*.*?\*/', '', 'Singleline')
    $stripped[$f.FullName] = $t
}

# 白名单类别：这些零调用是设计使然
$whitelistClass = @(
    'Patch_WipeToBlack','Patch_SwitchToEditMode','Patch_Restart','Patch_QuitToMainMenu',
    'Patch_StartRewind','Patch_TogglePauseGame','Patch_ConductorUpdate','Patch_CancelSaveKey',
    'Patch_MarkFail','Patch_LightUp','Patch_UpdateFreeroam','Patch_Won_Update',
    'Patch_PlayerControlUpdate','Patch_HoldKeysCrashGuard','Patch_Fail2Action',
    'Patch_OnLandOnPortal','Patch_Hit','Patch_UpdateInputConsumeAsync',
    'Patch_RenderClockConductor','Patch_RenderClockAsyncPosition','Patch_VideoPinSet',
    'Patch_VideoPinGet','Patch_VideoLockAsyncAngle'
)

# ── 分类规则（决定退出码，也决定输出可读性）────────────────────────────
# 零调用分三类，只有第三类算"疑似漏接线"：
#   A. Plugin 回调      —— UMM/Harmony 按名反射调用（OnGUI/OnSave/Prefix/Postfix…）
#   B. 公开 API 面      —— public + 在 SpectreAPI.cs 等对外契约文件里，给别的 mod 用
#   C. 需人工判定       —— 剩下的，本类返回非 0
# A/B 两类零调用是设计使然，仅展示；C 才是"搬了没接线"的候选。
$pluginCallbackNames = @(
    'OnGUI','OnShow','OnHide','OnSave','OnSaveGUI','OnToggle','Load',
    'Prefix','Postfix','Finalizer','Transpiler',
    'Run','Start','Stop','Reset','Check','Apply','Refresh','Dispose'
)

# C 类清单：Spectre 侧确有调用方、但那些调用方所属特性本项目按裁决"不补"。
# 每个都核过 Spectre 那边的调用点存在（见 2026-10-06 的 we/them 计数对比）。
#   结算附加文本（MaxCombo、GameCompat.DeadTiles） → ResultsText 不补
#   ReplayManager.UnLoadReplay           → Spectre 在设置页"卸载当前回放"里调，我们无该页
#   LocalizationManager.CodeToLang 等    → 语言管理工具函数，Spectre 的设置 UI 用
# (2026-10-17：CustomJudge 整套按用户裁决删除，档位/TierJudgeState/BoundaryRaw 等
#   名字已从本项目消失，故不再列在"不补"名单里。GameCompat.Boundary 这套**走游戏
#   函数**的反射机制原本只服务已删的档位布局，也一并删了。)
# (2026-10-18：连 MarginBoundary.Raw 本地复刻也删了——用户问「不能改成完全用游戏
#   自带的代码？」，查实 scrMisc.GetAdjustedAngleBoundaryInDeg 是 public static、
#   复刻里没有自研逻辑，ReplayAnalyzer 现在直接调它。)
$cSpectreCallerNotPorted = @(
    'MarkCheckpoint','RevertToCheckpoint',
    'MaxCombo','ComputeStats','CapturePending',
    'DeadTiles','IsCounted','IsJudgeTracked','IsPlayerHitMargin','IsTimeless','XAccWeight',
    'FilterName','ColorIndex','CachedName','Has','IsPerfect','IsInnerPerfect','IsFail','IsComboPerfect',
    'UnLoadReplay','IsNewFormat',
    'CodeToGoogleDocCode','CodeToLang','FillColor','Save'
)

$rows = @()
foreach ($f in $files) {
    $text = $stripped[$f.FullName]
    $relForCat = $f.FullName.Replace("$src\", '')
    foreach ($m in [regex]::Matches($text, '(?m)^\s*(?:internal|public)\s+static\s+(?!class|readonly|partial)[\w\<\>\[\]\?\.,\s]+?\s+(\w+)\s*\(')) {
        $name = $m.Groups[1].Value
        if ($name -in @('Main', 'ToString', 'Equals', 'GetHashCode')) { continue }
        # **必须统计同文件内的其他调用，只排除声明那一行。**
        # 原始脚本把整个声明文件跳过（`if ($g.FullName -eq $f.FullName) continue`），
        # 于是**类内互相调用全被算成零调用**——实测 62 个"需人工判定"里绝大多数
        # 是这么来的假警报：Pages.ReplayList 在同文件 :76 被调、
        # HitMarginCompat.IsPerfect 在同文件 :845-849 被调、Loc.Cat /
        # Settings.NewColorArray 同理。
        # 这类误报比漏报更糟：它让看报告的人习惯性忽略整份审计。
        # 剔除**声明行**本身（按行内容匹配，不能用 $m.Value 做整行相等比较——
        # $m.Value 内含换行符，与文件里任何一行都不相等，删除会静默失效，
        # 于是声明行里的 `Cat(` 被当成一次调用，所有方法都"有调用"）
        $declPat = '(?m)^\s*(?:internal|public)\s+static\s+(?!class|readonly|partial)[\w\<\>\[\]\?\.,\s]+?\s+' + [regex]::Escape($name) + '\s*\('
        $calls = 0
        foreach ($g in $files) {
            $hay = $stripped[$g.FullName]
            if ($g.FullName -eq $f.FullName) {
                $hay = [regex]::Replace($hay, $declPat, '')
            }
            $calls += ([regex]::Matches($hay, '\b' + [regex]::Escape($name) + '\s*\(')).Count
        }
        if ($calls -ne 0) { continue }

        $cat = '需人工判定'
        if ($relForCat -match '\\Patch') { $cat = 'A 补丁（Harmony 扫属性发现）' }
        elseif ($pluginCallbackNames -contains $name) { $cat = 'A 插件回调（反射调用）' }
        elseif ($relForCat -match 'SpectreAPI') { $cat = 'B 公开 API 面' }
        elseif ($relForCat -match 'VideoRenderClockPatches' -and $name -in @('ConductorTranspiler','Delta','DspTime','TimeD','TimeF')) { $cat = 'A IL 直调（Transpiler 改写调用点，源码里没有这个调用字样）' }
        elseif ($name -in @('PatchManual','Defer','OnKeyUpdated')) { $cat = 'A 反射调用' }
        # C 类：随移植搬来、**调用方在 Spectre 侧而本项目按裁决"不补"**（见
        # docs\SPECTRE_COMPARISON.md 第七之二节：档位 UI / 结算附加文本 / 编辑器
        # 特性均不补）。这些方法在本仓无人调用是**裁决的结果**，不是漏接线——
        # 但必须逐个列明理由，否则"不补"会悄悄扩大成"忘了搬"。
        # 判据：方法名在下列清单内（清单 = 已核过 Spectre 侧确有调用方）。
        elseif ($cSpectreCallerNotPorted -contains $name) { $cat = 'C Spectre 有调用方、按裁决不补' }

        $rows += [PSCustomObject]@{ File = $relForCat; Method = $name; Category = $cat }
    }
}

$needReview = @($rows | Where-Object { $_.Category -eq '需人工判定' })
$byDesign   = @($rows | Where-Object { $_.Category -ne '需人工判定' })

Write-Output "=== 零外部调用点的静态方法 ==="
Write-Output ("  设计使然（补丁/回调/API）: {0} 个" -f $byDesign.Count)
Write-Output ("  需人工判定              : {0} 个" -f $needReview.Count)
if ($needReview.Count -gt 0) {
    Write-Output ""
    Write-Output "── 需人工判定的条目 ──"
    $needReview | Sort-Object File, Method | Format-Table -AutoSize | Out-String | Write-Output
}
Write-Output ("结论: 需人工判定 {0} 个（0 = 干净）" -f $needReview.Count)
exit $needReview.Count
