# 部署 SpectreReplay 到游戏 Mods 目录。
# 与 Spectre 的部署完全独立——两个模组可以各自单独部署、单独回滚。
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $projectRoot 'SpectreReplay'
$binDir = Join-Path $projDir "bin\$Config"

$gameRoot = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
$modDir = Join-Path $gameRoot 'Mods\SpectreReplay'

$dll = Join-Path $binDir 'SpectreReplay.dll'
if (-not (Test-Path $dll)) { throw "build output missing: $dll" }

# 游戏在跑时覆盖 dll 会失败（文件被锁），先探一下再动手
$running = Get-Process -Name 'A Dance of Fire and Ice' -ErrorAction SilentlyContinue
if ($running) { throw 'game is running, close it before deploying' }

New-Item -ItemType Directory -Path $modDir -Force | Out-Null

# UMM 会在进程启动时把加载过的 dll 缓存成 <name>.dll.<pid>.cache，
# 下次启动优先用那份缓存。**不删它的话，游戏里跑的一直是几轮之前的
# 旧代码**——部署看起来成功，改动却毫无反应。
# 曾因此误判成"改了没效果"，白查了两天。
Get-ChildItem -Path $modDir -Filter "$([System.IO.Path]::GetFileName($dll)).*.cache*" -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Output "removed stale cache: $($_.Name)"
    }

Copy-Item $dll (Join-Path $modDir 'SpectreReplay.dll') -Force
if (Test-Path (Join-Path $binDir 'SpectreReplay.pdb')) {
    Copy-Item (Join-Path $binDir 'SpectreReplay.pdb') (Join-Path $modDir 'SpectreReplay.pdb') -Force
}
Copy-Item (Join-Path $projDir 'info.json') (Join-Path $modDir 'info.json') -Force

# ffmpeg 是外部程序，没有它视频渲染会在启动编码器时直接失败。
# 优先用工程 tools/ 下的那份；没有就从 Spectre 的模组目录**复制**一份过来
# （只读 Spectre，不改它），免得两个模组引用不同版本导致输出参数对不上。
$ffmpegPlaced = $false
$ownFfmpeg = Join-Path $projectRoot 'tools\ffmpeg.exe'
if (Test-Path $ownFfmpeg) {
    Copy-Item $ownFfmpeg (Join-Path $modDir 'ffmpeg.exe') -Force
    $ffmpegPlaced = $true
    Write-Output "ffmpeg: from project tools/"
} else {
    $spectreFfmpeg = Join-Path $gameRoot 'Mods\Spectre\ffmpeg.exe'
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