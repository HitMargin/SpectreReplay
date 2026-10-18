<#
  生成 tools\UiPreview\fixtures\legacy-sprp.bin —— 上游 Spectre 旧格式回放夹具。

  为什么需要它：ReplayCryptoCheck 的 ⑥ 要证明"上游存量 .sprp 仍能解开"。
  最初这条直接扫本机 `Mods\Spectre\*.sprp`，结果 CI 里永远跳过（没有游戏安装），
  而**跳过与通过看着一样**——第一次 CI 只跑出 66 条断言，本地 68 条。

  为什么不直接提交一份真回放：真文件里有时间戳、关卡名与模组清单，
  属于玩家数据，不该进公开仓库。这里生成一份**格式完全相同、内容合成**的。

  格式取自上游 D:\Projects\Spectre\Spectre\Features\Replay\ReplayCrypto.cs
  （v0.4.3，commit 1d84227）：
    key = SHA256("qwerty")[0..32]
    IV  = SHA256("potato")[0..16]
    cipher = AES-256-CBC( gzip( plain ) )
  plain 以魔数 "SPRcrpl" 开头（读侧据此判定解压成功）。

  这个夹具**只在读侧有意义**：本模组的写侧只产出 v2/v3 新容器。
  上游若改了旧格式常量，本脚本与 ⑥ 需一起更新——那也正是"读侧回落坏了"的信号。
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'tools\UiPreview\fixtures'
$outFile = Join-Path $outDir 'legacy-sprp.bin'

# 合成明文：魔数 + 可辨识但无意义的负载。长度取 1KB 量级，
# 与真实小回放同数量级（真文件 880B~2KB），避免"只有空文件才解得开"。
$ascii = New-Object System.Text.ASCIIEncoding
$utf8 = New-Object System.Text.UTF8Encoding $false
$magic = $ascii.GetBytes('SPRcrpl')
$payload = $utf8.GetBytes("synthetic-fixture-for-ci|" + ('x' * 900) + "|end")
$plain = New-Object byte[] ($magic.Length + $payload.Length)
[Array]::Copy($magic, 0, $plain, 0, $magic.Length)
[Array]::Copy($payload, 0, $plain, $magic.Length, $payload.Length)

# gzip 压缩
$gzMs = New-Object IO.MemoryStream
$gz = New-Object IO.Compression.GZipStream($gzMs, [IO.Compression.CompressionMode]::Compress)
$gz.Write($plain, 0, $plain.Length)
$gz.Dispose()
$gzBytes = $gzMs.ToArray()

# 旧格式密钥派生（逐字照抄上游）
$sha = [Security.Cryptography.SHA256]::Create()
$key = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('qwerty'))[0..31]
$iv  = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('potato'))[0..15]

$aes = [Security.Cryptography.Aes]::Create()
$aes.Key = $key
$aes.IV = $iv
$encMs = New-Object IO.MemoryStream
$enc = New-Object Security.Cryptography.CryptoStream(
    $encMs, $aes.CreateEncryptor(), [Security.Cryptography.CryptoStreamMode]::Write)
$enc.Write($gzBytes, 0, $gzBytes.Length)
$enc.FlushFinalBlock()
$cipher = $encMs.ToArray()

New-Item -ItemType Directory -Force $outDir | Out-Null
[IO.File]::WriteAllBytes($outFile, $cipher)

Write-Output ("已写入 {0}" -f $outFile)
Write-Output ("  明文 {0}B → gzip {1}B → 密文 {2}B" -f $plain.Length, $gzBytes.Length, $cipher.Length)