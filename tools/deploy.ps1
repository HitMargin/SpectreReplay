# 部署 SpectreReplay 到**加载器真正扫描的**模组目录。
# 与 Spectre 的部署完全独立——两个模组可以各自单独部署、单独回滚。
#
# 目录不再硬编码成 `<game>\Mods\SpectreReplay`：本机 UMM 0.33 实际扫的是
# `<game>\UMMMods`，写死 Mods 会让部署变成空操作（详见 tools\_gameroot.ps1
# 开头的事故记录）。解析顺序 = Mods → UMMMods，判据是「该目录下真有模组」。
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release',
    # 手动指定模组父目录（不给就走 Mods → UMMMods 自动解析）
    [string]$ModsRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $projectRoot 'SpectreReplay'
$binDir = Join-Path $projDir "bin\$Config"

. (Join-Path $PSScriptRoot '_gameroot.ps1')
$gameRoot = Get-SrmGameRoot
$resolved = Get-SrmModsRoot -ModsRoot $ModsRoot
$modDir = Join-Path $resolved.Root 'SpectreReplay'

# 部署完要能说清「写到哪、加载器认不认这个目录」。
# 上一轮的症状正是"部署成功但游戏跑旧代码"，沉默比报错更难查。
if (-not $resolved.IsValid) {
    Write-Warning ("目标目录 {0} 里没有任何模组，UMM 可能根本不扫描它。" -f $resolved.Root)
    Write-Warning '（若你的加载器用的是别的目录，用 -ModsRoot 指定）'
    Format-SrmModsDiagnosis -ModsRoot $ModsRoot | ForEach-Object { Write-Warning $_ }
}

$dll = Join-Path $binDir 'SpectreReplay.dll'
if (-not (Test-Path $dll)) { throw "build output missing: $dll" }

# 游戏在跑时覆盖 dll 会失败（文件被锁），先探一下再动手
$running = Get-Process -Name 'A Dance of Fire and Ice' -ErrorAction SilentlyContinue
if ($running) { throw 'game is running, close it before deploying' }

New-Item -ItemType Directory -Path $modDir -Force | Out-Null

# 顺手清掉模组目录里的 <name>.dll.<pid>.cache*。
#
# **旧注释在这里写错了，2026-10-25 实测更正。** 它原来说「UMM 启动时把加载过的
# dll 缓存成 .cache，下次优先用那份缓存；不删就一直跑几轮之前的旧代码」——
# 这个结论站不住，三条证据：
#   ① UnityModManager.dll 里没有任何 ".cache" 字符串（只有反射用的 mCache /
#      fieldInfoCache），UTF-16 格式串里也找不到拼这个文件名的痕迹；
#   ② 实测 9 个模组的 .cache 与对应 dll **逐字节相同**（SHA256 一致）、
#      LastWriteTime 也相同——它是 dll 的完整副本，不是"更早的版本"；
#   ③ Spectre.dll 与 Spectre.dll.<pid>.cache 的 PE 时间戳相同。
# 所以即便真有谁去读 .cache，读到的也是同一份字节，行为不会有差别。
# 生成者至今没定位到（version.dll / 0Harmony / UMM 里都没有对应逻辑，
# 可能来自带 bootstrap 的第三方 loader），但那是另一个问题。
#
# 那为什么还留着这段删除？因为这些文件是纯冗余副本——删掉只会省空间，
# 不会改变任何加载行为。**别再把它当成"修了没效果"的解释**：那个现象的真因
# 通常更朴素（dll 没覆盖成功 / 游戏没重启 / 部署到了加载器不扫描的目录，
# 后者见 tools\_gameroot.ps1 开头那次事故）。
Get-ChildItem -Path $modDir -Filter "$([System.IO.Path]::GetFileName($dll)).*.cache*" -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Output "removed redundant cache copy: $($_.Name)"
    }

Copy-Item $dll (Join-Path $modDir 'SpectreReplay.dll') -Force
if (Test-Path (Join-Path $binDir 'SpectreReplay.pdb')) {
    Copy-Item (Join-Path $binDir 'SpectreReplay.pdb') (Join-Path $modDir 'SpectreReplay.pdb') -Force
}
Copy-Item (Join-Path $projDir 'info.json') (Join-Path $modDir 'info.json') -Force

# ffmpeg 是外部程序，没有它视频渲染会在启动编码器时直接失败。
# 优先用工程 tools/ 下的那份；没有就从 Spectre 的模组目录**复制**一份过来
# （只读 Spectre，不改它），免得两个模组引用不同版本导致输出参数对不上。
# Spectre 的目录也在同一个 mods root 下，所以顺着 $resolved 取，别再写死 Mods。
$ffmpegPlaced = $false
$ownFfmpeg = Join-Path $projectRoot 'tools\ffmpeg.exe'
if (Test-Path $ownFfmpeg) {
    Copy-Item $ownFfmpeg (Join-Path $modDir 'ffmpeg.exe') -Force
    $ffmpegPlaced = $true
    Write-Output "ffmpeg: from project tools/"
} else {
    $spectreFfmpeg = Join-Path $resolved.Root 'Spectre\ffmpeg.exe'
    if (Test-Path $spectreFfmpeg) {
        Copy-Item $spectreFfmpeg (Join-Path $modDir 'ffmpeg.exe') -Force
        $ffmpegPlaced = $true
        Write-Output "ffmpeg: copied from Spectre (read-only)"
    }
}
if (-not $ffmpegPlaced) {
    Write-Warning 'ffmpeg.exe not found: recording works, video render will fail.'
}

foreach ($d in 'Replays', 'Videos') {
    New-Item -ItemType Directory -Path (Join-Path $modDir $d) -Force | Out-Null
}

Write-Output "deployed: $modDir"
Get-ChildItem $modDir | ForEach-Object {
    if ($_.PSIsContainer) { Write-Output ("  {0}/" -f $_.Name) }
    else { Write-Output ("  {0}  ({1} bytes)" -f $_.Name, $_.Length) }
}