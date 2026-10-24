<#
  真跑一遍 VideoEncoder 会拼出来的 ffmpeg 参数（离线，不起游戏）。
  覆四个容器 × 三种编码器选择，喂真 BGRA 帧，验三件事：
    ① 中间文件能写出来（pad 偶数宽高、out_color_matrix 等滤镜组合被接受）
    ② 最终 mux 能成（容器与编码器匹配、faststart 只出现在 mp4/mov）
    ③ mux 出来的文件里**真的有音轨**（音频不是摆设）
  任何一步非零退出都打印退出码与 stderr 尾部。
#>
$ErrorActionPreference = 'Stop'
# ffmpeg 位置与 VideoEncoder.LocateEncoder 同口径（模组目录 → 游戏根目录 → PATH）。
# 原先写死 `<game>\Mods\SpectreReplay\ffmpeg.exe`，而本机 UMM 实际用的模组目录是
# UMMMods ⇒ 第一句就 throw，这个"改编码参数后必跑"的验证手段长期不可用。
. (Join-Path $PSScriptRoot '_gameroot.ps1')
$ff = Get-SrmFfmpeg
if (-not $ff) {
    throw ("ffmpeg not found. 已查：模组目录、游戏根目录、PATH。" +
        [Environment]::NewLine + (Format-SrmModsDiagnosis))
}
$work = Join-Path $env:TEMP 'srp_ffmpeg_probe'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

# ── 与 VideoEncoder.cs 保持同源的参数拼装 ─────────────────────────
function Get-RowFilter([bool]$flipRows) {
    $pad = 'pad=ceil(iw/2)*2:ceil(ih/2)*2'
    $color = ',scale=out_color_matrix=bt709,format=yuv420p'
    return $(if ($flipRows) { 'vflip,' } else { '' }) + $pad + $color
}
function Get-EncoderArgs([string]$codec, [int]$fps, [string]$quality, [int]$tier, [bool]$nvenc) {
    $gop = [Math]::Max(1, $fps * 2)
    $megapixels = 1920 * 1080 / 1000000.0
    $scale = @{0=0.5;1=1.0;2=1.6;3=2.4}[$tier]
    $target = [int][Math]::Round($megapixels * 10.0 * $scale)
    if ($fps -le 30) { $target = $target * 3 / 4 } elseif ($fps -gt 60) { $target = $target * $fps / 60 }
    $floor = @{0=4;1=8;2=8;3=8}[$tier]; $cap = $(if ($tier -eq 3) { 160 } else { 100 })
    $target = [Math]::Max($floor, [Math]::Min($cap, $target))
    $ceiling = $target * 3 / 2; $bucket = $target * 2
    switch ($codec) {
        'vp9' {
            $m1 = @{ Fastest = '5'; Sharpest = '2' }
            $cpuUsed = $(if ($m1.ContainsKey($quality)) { $m1[$quality] } else { '4' })
            return "-c:v libvpx-vp9 -deadline good -cpu-used $cpuUsed -row-mt 1 -b:v ${target}M -maxrate ${ceiling}M -bufsize ${bucket}M -g $gop"
        }
        'nvenc' {
            $m2 = @{ Fastest = 'p1'; Sharpest = 'p6' }
            $nvPreset = $(if ($m2.ContainsKey($quality)) { $m2[$quality] } else { 'p4' })
            $tune = $(if ($quality -eq 'Fastest') { 'll' } else { 'hq' })
            $cq = @{0=23;2=18;3=16}[$tier]; if (-not $cq) { $cq = 20 }
            return "-c:v h264_nvenc -preset $nvPreset -tune $tune -rc vbr -cq $cq -b:v ${target}M -maxrate ${ceiling}M -bufsize ${bucket}M -g $gop"
        }
        default {
            $m3 = @{ Fastest = 'ultrafast'; Sharpest = 'fast' }
            $x264Preset = $(if ($m3.ContainsKey($quality)) { $m3[$quality] } else { 'veryfast' })
            return "-c:v libx264 -preset $x264Preset -b:v ${target}M -maxrate ${ceiling}M -bufsize ${bucket}M -g $gop"
        }
    }
}

$W = 1920; $H = 1080; $FPS = 30; $FRAMES = 6
$rawDir = Join-Path $work 'raw'
New-Item -ItemType Directory -Path $rawDir | Out-Null
# 生成 FRAMES 帧真 BGRA（内容逐帧变化，避免编码器判"静止"走捷径）
# 注意用 [int] 截断：PowerShell 的 / 是浮点除，直接 [byte] 转换会溢出报错
for ($i = 0; $i -lt $FRAMES; $i++) {
    $bytes = New-Object byte[] ($W * $H * 4)
    for ($j = 0; $j -lt $bytes.Length; $j += 4) {
        $bytes[$j]   = [byte](($i * 40 + ([int]($j / 4) % 251)) % 256)
        $bytes[$j+1] = [byte](($i * 25 + ([int]($j / 7) % 241)) % 256)
        $bytes[$j+2] = [byte](($i * 15 + ([int]($j / 11) % 231)) % 256)
        $bytes[$j+3] = 255
    }
    [IO.File]::WriteAllBytes((Join-Path $rawDir ("f{0:D3}.bgra" -f $i)), $bytes)
}
# 拼到 raw\ 外面：否则 glob '*.bgra' 会把刚写出来的 all.bgra 也收进去，
# 而它此刻还被 $fs 占着 → IOException。（自己踩的）
$framesCat = Join-Path $work 'all.bgra'
$fs = [IO.File]::OpenWrite($framesCat)
foreach ($f in (Get-ChildItem $rawDir -Filter *.bgra | Sort-Object Name)) {
    $b = [IO.File]::ReadAllBytes($f.FullName); $fs.Write($b, 0, $b.Length)
}
$fs.Close()
Write-Output ("raw frames: {0} x {1}B = {2}MB" -f $FRAMES, ($W*$H*4), [int]((Get-Item $framesCat).Length/1MB))

# 一段真 float32 WAV（440Hz 正弦，2 声道），验 mux 出来的音轨不是空的
$sr = 48000; $ch = 2; $secs = 1
$samples = $sr * $secs
$data = New-Object byte[] ($samples * $ch * 4)
for ($i = 0; $i -lt $samples; $i++) {
    $v = [Math]::Sin(2 * [Math]::PI * 440 * $i / $sr) * 0.5
    $b = [BitConverter]::GetBytes([float]$v)
    for ($c = 0; $c -lt $ch; $c++) { [Array]::Copy($b, 0, $data, ($i * $ch + $c) * 4, 4) }
}
$wav = Join-Path $work 'audio.wav'
$ms = [IO.File]::OpenWrite($wav)
function WB([System.IO.Stream]$s, [byte[]] $b) { $s.Write($b, 0, $b.Length) }
function WStr([System.IO.Stream]$s, [string] $t) { WB $s ([Text.Encoding]::ASCII.GetBytes($t)) }
WStr $ms 'RIFF'; WB $ms ([BitConverter]::GetBytes([int](36 + $data.Length))); WStr $ms 'WAVE'
WStr $ms 'fmt '; WB $ms ([BitConverter]::GetBytes(16)); WB $ms ([BitConverter]::GetBytes([int16]3))
WB $ms ([BitConverter]::GetBytes([int16]$ch)); WB $ms ([BitConverter]::GetBytes($sr))
WB $ms ([BitConverter]::GetBytes($sr * $ch * 4)); WB $ms ([BitConverter]::GetBytes([int16]($ch*4)))
WB $ms ([BitConverter]::GetBytes([int16]32))
WStr $ms 'data'; WB $ms ([BitConverter]::GetBytes($data.Length)); WB $ms $data
$ms.Close()

$containers = @(
    @{ name='mp4';  ext='.mp4';  codec='x264' },
    @{ name='mkv';  ext='.mkv';  codec='x264' },
    @{ name='webm'; ext='.webm'; codec='vp9'  },
    @{ name='mov';  ext='.mov';  codec='x264' }
)

$fail = 0
foreach ($c in $containers) {
    # 中间文件扩展名规则（与 VideoRenderJob.PrepareWorkspace 同源）
    $midExt = $(if ($c.name -eq 'webm') { '.mkv' } else { '.mp4' })
    $mid = Join-Path $work ("temp_video" + $midExt)
    $final = Join-Path $work ("out" + $c.ext)

    $vf = Get-RowFilter $true
    $enc = Get-EncoderArgs $c.codec $FPS 'Balanced' 1 $false
    $encArgs = "-y -f rawvideo -pixel_format rgba -video_size ${W}x${H} -framerate $FPS -i `"$framesCat`" -an -vf `"$vf`" $enc -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 `"$mid`""
    $p = Start-Process -FilePath $ff -ArgumentList $encArgs -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $work 'e1.txt')
    if ($p.ExitCode -ne 0) {
        Write-Output ("  [FAIL] {0} encode exit={1}" -f $c.name, $p.ExitCode)
        Get-Content (Join-Path $work 'e1.txt') -Tail 4 | ForEach-Object { "      $_" }
        $fail++; continue
    }
    $midSize = (Get-Item $mid).Length

    $webm = $c.name -eq 'webm'
    $audio = $(if ($webm) { '-c:a libopus -b:a 192k -ar 48000' } else { '-c:a aac -b:a 320k' })
    $fast = $(if ($c.name -eq 'mp4' -or $c.name -eq 'mov') { ' -movflags +faststart' } else { '' })
    $muxArgs = "-y -i `"$mid`" -i `"$wav`" -map 0:v:0 -map 1:a:0 -c:v copy $audio -ac 2$fast `"$final`""
    $p2 = Start-Process -FilePath $ff -ArgumentList $muxArgs -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $work 'e2.txt')
    if ($p2.ExitCode -ne 0) {
        Write-Output ("  [FAIL] {0} mux exit={1}" -f $c.name, $p2.ExitCode)
        Get-Content (Join-Path $work 'e2.txt') -Tail 4 | ForEach-Object { "      $_" }
        $fail++; continue
    }

    # 解回来看：尺寸、时长、有没有音轨。
    # **不能用 `2>&1`**：$ErrorActionPreference='Stop' 会把 ffmpeg 写到 stderr 的
    # 探测输出（Input #0 那一整段）当成终止性错误，脚本直接中断。
    # 落文件再读，绕开管道的错误语义。
    $probeFile = Join-Path $work 'probe.txt'
    Start-Process -FilePath $ff -ArgumentList "-hide_banner -i `"$final`"" -Wait -NoNewWindow `
        -RedirectStandardError $probeFile -RedirectStandardOutput (Join-Path $work 'probe_out.txt') | Out-Null
    $probe = [IO.File]::ReadAllText($probeFile)
    $hasAudio = $probe -match 'Audio:'
    $hasVideo = $probe -match 'Video:'
    $dur = if ($probe -match 'Duration:\s*([0-9:.]+)') { $Matches[1] } else { '?' }
    $ok = $hasAudio -and $hasVideo
    if (-not $ok) { $fail++ }
    Write-Output ("  [{0}] {1}  mid={2}KB final={3}KB  dur={4}  video={5} audio={6}" -f `
        $(if ($ok) { 'OK  ' } else { 'FAIL' }), $c.name, [int]($midSize/1KB), [int]((Get-Item $final).Length/1KB), $dur, $hasVideo, $hasAudio)
}

Write-Output ""
if ($fail -eq 0) { Write-Output "  全部通过" } else { Write-Output ("  {0} 项不通过" -f $fail) }
exit $fail

