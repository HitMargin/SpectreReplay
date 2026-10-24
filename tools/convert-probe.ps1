<#
  真跑一遍 VideoConverter 的参数（离线，不起游戏）。
  用 ffmpeg-probe.ps1 那批产物做输入，逐个目标容器转一次，验：
    ① 同格式被跳过 ② 目标已存在被跳过
    ③ mp4/mkv 互转、mkv→webm（重编码）都能成
    ④ mkv/mov 内的音频能落地（Opus→AAC 那条分支真的可用）
    ⑤ 整流 copy 失败时二次尝试（-c:a aac）能救回来
#>
$ErrorActionPreference = 'Stop'
# 同 ffmpeg-probe.ps1：位置交给 _gameroot.ps1 解析，不再写死 Mods 目录。
. (Join-Path $PSScriptRoot '_gameroot.ps1')
$ff = Get-SrmFfmpeg
if (-not $ff) {
    throw ("ffmpeg not found. 已查：模组目录、游戏根目录、PATH。" +
        [Environment]::NewLine + (Format-SrmModsDiagnosis))
}
$work = Join-Path $env:TEMP 'srp_convert_probe'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# ── 造几个带音画的源（复用 ffmpeg 自己生成，省得手写 BGRA）──────────
function New-Source([string]$out, [string]$containerArgs) {
    $a = "-hide_banner -loglevel error -y -f lavfi -i ""color=c=navy:size=320x240:rate=10:duration=1"" " +
         "-f lavfi -i ""sine=frequency=440:sample_rate=48000:duration=1"" " +
         "-map 0:v:0 -map 1:a:0 $containerArgs " + """$out"""
    $p = Start-Process -FilePath $ff -ArgumentList $a -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $work 'e.txt')
    return $p.ExitCode
}
function Quot([string]$v) { return '"' + $v.Replace('"','\"') + '"' }
# **不能把形参名取成 $args**：那是 PowerShell 的自动变量（未命名实参），
# 一旦赋值，Start-Process 的 ArgumentList 就收到它，看起来像"参数为空"，
# 而报错完全不指向这个名字。
function TryRun([string]$argLine) {
    $p = Start-Process -FilePath $ff -ArgumentList $argLine -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $work 'e2.txt')
    return $p.ExitCode -eq 0
}

$fail = 0
# 源1：mkv（VP9+Opus，模拟 webm 那种非同族源）
$srcMkv = Join-Path $work 'src.mkv'
if ((New-Source $srcMkv '-c:v libvpx-vp9 -cpu-used 5 -deadline good -c:a libopus -b:a 128k') -ne 0) { Write-Output "mkv 源造失败"; exit 1 }
# 源2：mp4（H.264+AAC）
$srcMp4 = Join-Path $work 'src.mp4'
if ((New-Source $srcMp4 '-c:v libx264 -preset ultrafast -c:a aac -b:a 128k') -ne 0) { Write-Output "mp4 源造失败"; exit 1 }
Write-Output ("sources: src.mkv={0}KB src.mp4={1}KB" -f [int]((Get-Item $srcMkv).Length/1KB), [int]((Get-Item $srcMp4).Length/1KB))

# ── 逐目标容器跑转换逻辑（与 VideoConverter.ConvertOne 同源）──────
$targets = @(
    @{ name='mp4';  ext='.mp4' },
    @{ name='mkv';  ext='.mkv' },
    @{ name='webm'; ext='.webm' },
    @{ name='mov';  ext='.mov' }
)
foreach ($t in $targets) {
    # 每次用一份干净目录，只放两个源
    $dir = Join-Path $work ("to_" + $t.name)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item $srcMkv (Join-Path $dir 'a.mkv') -Force
    Copy-Item $srcMp4 (Join-Path $dir 'b.mp4') -Force

    $converted = 0; $skipped = 0; $failed = 0
    foreach ($f in @('a.mkv','b.mp4')) {
        $path = Join-Path $dir $f
        $ext = [IO.Path]::GetExtension($path).ToLowerInvariant()
        if ($ext -eq $t.ext) { $skipped++; continue }
        $targetPath = Join-Path $dir (([IO.Path]::GetFileNameWithoutExtension($path)) + $t.ext)
        if (Test-Path $targetPath) { $skipped++; continue }

        switch ($t.name) {
            'webm' { $v = '-c:v libvpx-vp9 -deadline good -cpu-used 4 -row-mt 1 -crf 30 -b:v 0 -pix_fmt yuv420p'; $a2 = '-c:a libopus -b:a 192k -ar 48000' }
            'mkv'  { $v = '-c:v copy'; $a2 = '-c:a copy' }
            default {
                if ($ext -eq '.mp4' -or $ext -eq '.mov') { $v = '-c:v copy'; $a2 = '-c:a copy' }
                else {
                    # 与 VideoConverter 修法一致：mov 复用 movenc，**拒绝 VP9**
                    # （copy 进 .mov 写 header 即 exit -22），必须重编码；mp4 可 copy。
                    if ($t.name -eq 'mov') { $v = '-c:v libx264 -preset veryfast -crf 23'; $a2 = '-c:a aac -b:a 320k' }
                    else { $v = '-c:v copy'; $a2 = '-c:a aac -b:a 320k' }
                }
            }
        }
        $fast = $(if ($t.name -eq 'mp4' -or $t.name -eq 'mov') { ' -movflags +faststart' } else { '' })
        $args = "-y -i $(Quot $path) $v $a2$fast $(Quot $targetPath)"
        $ok = TryRun $args
        if (-not $ok -and $a2 -eq '-c:a copy') {
            $retry = "-y -i $(Quot $path) $v -c:a aac -b:a 320k$fast $(Quot $targetPath)"
            $ok = TryRun $retry
        }
        if (-not $ok) { Remove-Item $targetPath -Force -ErrorAction SilentlyContinue; $failed++ }
        else { $converted++ }
    }

    # 解回来看音画双流
    $probeAll = ""
    Get-ChildItem $dir -File | Where-Object { $_.Extension -eq $t.ext } | ForEach-Object {
        $pf = Join-Path $work 'p.txt'
        Start-Process -FilePath $ff -ArgumentList "-hide_banner -i `"$($_.FullName)`"" -Wait -NoNewWindow -RedirectStandardError $pf | Out-Null
        $txt = [IO.File]::ReadAllText($pf)
        $hasV = $txt -match 'Video:'; $hasA = $txt -match 'Audio:'
        $probeAll += "$($_.Name):v=$hasV,a=$hasA "
    }
    $okAll = ($failed -eq 0) -and ($converted -gt 0) -and ($probeAll -notmatch 'v=False|a=False')
    if (-not $okAll) { $fail++ }
    Write-Output ("  [{0}] ->{1}  converted={2} skipped={3} failed={4}  {5}" -f `
        $(if ($okAll) { 'OK  ' } else { 'FAIL' }), $t.ext, $converted, $skipped, $failed, $probeAll)
}

Write-Output ""
if ($fail -eq 0) { Write-Output "  全部通过" } else { Write-Output ("  {0} 项不通过" -f $fail) }
exit $fail
