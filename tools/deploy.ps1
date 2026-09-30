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

# ffmpeg 是外部程序：只在本模组目录里有才复制，绝不动 Spectre 那份，
# 免得两个模组的编码器版本不一致导致输出参数对不上。
$ffmpegSrc = Join-Path $projectRoot 'tools\ffmpeg.exe'
if (Test-Path $ffmpegSrc) {
    Copy-Item $ffmpegSrc (Join-Path $modDir 'ffmpeg.exe') -Force
    Write-Output "部署完成：ffmpeg.exe"
}

foreach ($d in 'Replays', 'Videos') {
    New-Item -ItemType Directory -Path (Join-Path $modDir $d) -Force | Out-Null
}

Write-Output "部署完成：$modDir"
Get-ChildItem $modDir | ForEach-Object {
    if ($_.PSIsContainer) { Write-Output ("  {0}/" -f $_.Name) }
    else { Write-Output ("  {0}  ({1:N0} B)" -f $_.Name, $_.Length) }
}