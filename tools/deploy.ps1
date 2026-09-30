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
if (-not (Test-Path $dll)) { throw "找不到 $dll —— 请先构建（dotnet build SpectreReplay.csproj -c $Config）" }

# 游戏在跑时覆盖 dll 会失败（文件被锁），先探一下再动手
$running = Get-Process -Name 'A Dance of Fire and Ice' -ErrorAction SilentlyContinue
if ($running) { throw '游戏正在运行，关闭后再部署（dll 会被占用）' }

New-Item -ItemType Directory -Path $modDir -Force | Out-Null

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
    Write-Output "部署完成：ffmpeg.exe（工程内）"
} else {
    $spectreFfmpeg = Join-Path $gameRoot 'Mods\Spectre\ffmpeg.exe'
    if (Test-Path $spectreFfmpeg) {
        Copy-Item $spectreFfmpeg (Join-Path $modDir 'ffmpeg.exe') -Force
        $ffmpegPlaced = $true
        Write-Output "部署完成：ffmpeg.exe（自 Spectre 复制，只读取）"
    }
}
if (-not $ffmpegPlaced) {
    Write-Warning '未找到 ffmpeg.exe：录制可用、视频渲染会失败。请把 ffmpeg.exe 放到工程 tools\ 下。'
}

foreach ($d in 'Replays', 'Videos') {
    New-Item -ItemType Directory -Path (Join-Path $modDir $d) -Force | Out-Null
}

Write-Output "部署完成：$modDir"
Get-ChildItem $modDir | ForEach-Object {
    if ($_.PSIsContainer) { Write-Output ("  {0}/" -f $_.Name) }
    else { Write-Output ("  {0}  ({1:N0} B)" -f $_.Name, $_.Length) }
}