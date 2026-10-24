# 共用：定位游戏根目录 / 运行时模组目录 / ffmpeg。
#
# **为什么要有这个文件。** 2026-10-25 查出一条真事故：deploy.ps1 / verify.ps1 /
# ffmpeg-probe.ps1 / convert-probe.ps1 四个脚本各自硬编码了同一个路径
# `<game>\Mods\SpectreReplay`，而本机 UMM 0.33 实际扫描的是 `<game>\UMMMods`
# （见 UnityModManager\Log.txt 的 `[Manager] Mods path:` 一行）。于是：
#   - deploy.ps1 一直在把一个没人读的目录当部署目标（空操作）；
#   - verify.ps1 的「部署一致性」永远报「线上 dll 不存在」，
#     而 docs/verify.md 把它误解释成「本机从没部署过」，于是长期用 -Ci 跳过，
#     **唯一的实机一致性检查等于常年关闭**；
#   - 两个 probe 脚本第一句 Test-Path 就 throw，编码参数链路没有可执行验证。
# 四处各写一份是这件事能发生的根因，所以路径解析只留这一份，其它脚本点源引用。
#
# ── 解析顺序（先 Mods，找不到退 UMMMods）──────────────────────
#   1. 显式参数 -ModsRoot <path>（最高优先，调试/异机用）
#   2. 环境变量 SRM_MODS_ROOT
#   3. <game>\Mods\<模组>       —— 存在且**看起来是真模组目录**时用
#   4. <game>\UMMMods\<模组>    —— 同上
#   5. <game>\Mods              —— 都没有时作为**写入默认值**（首次部署要往哪放）
#
# **不能用「目录存在」当判据。** 本机 `Mods` 目录是存在的、只是空的
# （用户把真正的模组目录改名成了 UMMMods）。只判存在会挑中一个空的 Mods，
# 于是部署"成功"、加载器却看不见——症状与本 bug 完全一样。所以判据是
# 「该目录下有至少一个 *\Info.json」，即它真的能被 UMM 扫出模组来。
#
# ── 用法（点源，不执行任何有副作用的事）────────────────────────
#   . (Join-Path $PSScriptRoot '_gameroot.ps1')
#   $mods  = Get-SrmModsRoot        # @{ Root=...; IsValid=$true/$false; ... }
#   $mod   = Get-SrmModDir          # <mods>\SpectreReplay
#   $ff    = Get-SrmFfmpeg          # 与 VideoEncoder.LocateEncoder 同口径
#
# 注意：本文件必须存成 **UTF-8 带 BOM**。deploy.ps1 由 verify.ps1 用
# `powershell`（5.1）调用，5.1 按 ANSI 读没有 BOM 的脚本会把中文读成乱码
# 并直接 ParserError。写完记得补 BOM（见 tools/ 下各脚本开头同一段警告）。

$script:SrmGameRootDefault = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
$script:SrmModFolderName = 'SpectreReplay'

function Get-SrmGameRoot {
    # 环境变量优先，便于换 Steam 库位置或 CI；默认值是本机实际安装路径。
    if ($env:ADOFAI_GAME) { return $env:ADOFAI_GAME }
    return $script:SrmGameRootDefault
}

function Test-SrmLooksLikeModsRoot {
    <#
      判据：该目录下至少有一个子目录带 Info.json / info.json。
      UMM 就是按 <mods>\<名字>\Info.json 发现模组的，所以这条判据
      「像不像加载器会扫的目录」与真实行为同口径，而不是靠名字猜。
    #>
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    try {
        foreach ($d in (Get-ChildItem -LiteralPath $Path -Directory -ErrorAction Stop)) {
            foreach ($n in @('Info.json', 'info.json')) {
                if (Test-Path -LiteralPath (Join-Path $d.FullName $n) -PathType Leaf) { return $true }
            }
        }
    } catch {
        return $false
    }
    return $false
}

function Get-SrmModsRoot {
    <#
      返回一个描述对象，不做任何写入：
        Root      最终选中的模组父目录
        IsValid   Root 是否像真模组目录（$false = 只是写入默认值，加载器其实没有它）
        Source    选中的来源，用于日志：「显式参数 / 环境变量 / Mods / UMMMods / 默认」
    #>
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($ModsRoot)) {
        $candidates += @{ Path = $ModsRoot; Source = '显式参数' }
    }
    if ($env:SRM_MODS_ROOT) {
        $candidates += @{ Path = $env:SRM_MODS_ROOT; Source = '环境变量' }
    }
    $candidates += @{ Path = (Join-Path $game 'Mods');    Source = 'Mods' }
    $candidates += @{ Path = (Join-Path $game 'UMMMods'); Source = 'UMMMods' }

    foreach ($c in $candidates) {
        if (Test-SrmLooksLikeModsRoot -Path $c.Path) {
            return [pscustomobject]@{
                Root    = $c.Path
                IsValid = $true
                Source  = $c.Source
            }
        }
    }

    # 一个都不像模组目录：往 Mods 写（首次部署的常规落点）。
    # IsValid=$false 让调用方能把「要部署但加载器还不认这个目录」说清楚，
    # 而不是静默部署到一个没人读的地方——那正是上一轮踩的坑。
    return [pscustomobject]@{
        Root    = (Join-Path $game 'Mods')
        IsValid = $false
        Source  = '默认'
    }
}

function Get-SrmModDir {
    # 模组自己的目录：<mods root>\SpectreReplay
    param([string]$ModsRoot)
    $r = Get-SrmModsRoot -ModsRoot $ModsRoot
    return (Join-Path $r.Root $script:SrmModFolderName)
}

function Get-SrmFfmpeg {
    <#
      与 SpectreReplay.Features.VideoRender.VideoEncoder.LocateEncoder 同口径：
        ① 模组目录\ffmpeg.exe
        ② 游戏根目录\ffmpeg.exe
        ③ 系统 PATH 上的 ffmpeg
      模组那份代码在 :117-119 只认这两个文件位置，这里多一步 PATH 是因为
      离线 probe 脚本要能跑在没部署模组的机器上（CI / 新克隆）。
      找不到返回 $null——**不要 throw**，由调用方决定是报错还是提示。
    #>
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $modDir = Get-SrmModDir -ModsRoot $ModsRoot

    $candidates = @(
        (Join-Path $modDir 'ffmpeg.exe'),
        (Join-Path $game 'ffmpeg.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }

    # PATH 回退：最后一级，避免 probe 在未部署机器上完全跑不起来
    $onPath = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    return $null
}

function Format-SrmModsDiagnosis {
    # 解析失败时贴出全部候选，别再让人靠猜——本 bug 排查成本全在"到底读哪个目录"。
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $lines = @()
    $lines += "游戏根目录：$game"
    foreach ($name in @('Mods', 'UMMMods')) {
        $p = Join-Path $game $name
        if (-not (Test-Path -LiteralPath $p -PathType Container)) {
            $lines += ("  {0,-8} 不存在" -f $name)
        } elseif (Test-SrmLooksLikeModsRoot -Path $p) {
            $lines += ("  {0,-8} 存在且含模组 → 加载器会扫描" -f $name)
        } else {
            $lines += ("  {0,-8} 存在但没有模组（空目录）" -f $name)
        }
    }
    $chosen = Get-SrmModsRoot -ModsRoot $ModsRoot
    $lines += "选定：$($chosen.Root)（来源 $($chosen.Source)）"
    return ($lines -join [Environment]::NewLine)
}
