# SteamCloudProbe —— Steam Cloud 可行性探针

一次性的研究脚手架，**不属于 `tools\verify.ps1` 闸门**，也不参与构建。

## 它回答了什么

回放文件要不要/能不能随 Steam Cloud 备份，取决于一个事实问题：**第三方 mod
能不能借用游戏（ADOFAI，AppID 977950）自己的云端配额**。上游不提供云同步，
所以在动手写 `SpectreReplay\Features\Cloud\` 之前，先用这个探针在真实 appid
上下文里走了一遍 Remote Storage 最短闭环：

写 → 查大小 → 读回 → 配额 → 列举 → 清理

结论：可行，`SteamAPI_InitSafe()` 在有 `steam_appid.txt` 的情况下不需要游戏
进程也能初始化，Remote Storage v016 接口可读写。该结论已落到
`Features\Cloud\SteamCloud.cs` 的实现与 `tools\port-diff-audit.ps1` 的差异登记里。

## 为什么仓库里没有 `steam_api64.dll`

它是 Valve 的 320 KB 原生二进制，属于游戏发行物。仓库里没有任何先例、也不该
由我们 redistribute。需要重跑时从本机游戏目录拷一份即可。

## 怎么重跑

```powershell
# 1) 从游戏插件目录（或游戏根目录）拷来 dll，放在与 exe 同级处
tools\SteamCloudProbe\steam_api64.dll

# 2) 前提：Steam 客户端已登录（不需要启动游戏）

dotnet run -c Release --project tools\SteamCloudProbe

# 期望：最后一行 [probe] RESULT = ALL PASS
```

`steam_appid.txt`（内容 `977950`）必须留在输出目录——`SteamAPI_InitSafe()` 靠它
确定 appid 上下文，csproj 已配 `CopyToOutputDirectory`。

## 子命令

| 命令 | 作用 |
| --- | --- |
| 无参数 | 完整闭环（写/查/读/配额/列表），全 PASS 才返回 0 |
| `check` | 只查 `SpectreReplayProbe/probe.bin` 是否存在 |
| `delete` | 删掉探针文件 |
| `names` | 列出云端文件名 |
| `clean` | 删掉所有 `SpectreReplayProbe/` 前缀的文件 |
| `seed <路径>` / `unseed <路径>` | 铺/拆多文件场景（验证同步行为） |
| `enable` / `disable` | 切换该 appid 的云开关（会改真实账号设置，慎用） |

验证是否被 Steam 收录：<https://store.steampowered.com/account/remotestorage>
或本机 `userdata\<账号>\977950\`。
