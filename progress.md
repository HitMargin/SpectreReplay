# Progress Log

## Session: 2026-11-01

### Current Status
- **Phase:** 5 - 交付
- **Started:** 2026-11-01

### Actions Taken
- 反编译 StutterFix，提取 UGUI Canvas、批量 Graphic 和输入拦截模式。
- 实现时间索引、动态区间、任意时间 seek 和 UGUI 时间轴覆盖层。
- 将区间接入 VideoRender/VideoRenderJob，并补充输入隔离。
- 新增离线时间轴检查、形状断言和 MUT-TL1~MUT-TL7 变异。
- 运行完整 verify 闸门并确认全部通过。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| 主项目构建 | 0 错误 0 警告 | 0 错误 0 警告 | pass |
| 离线断言 | 非 0 且 0 失败 | 164 条，0 失败 | pass |
| 只读审计 | 全过 | 全过 | pass |
| 时间轴变异 | 每条都变红 | MUT-TL1~TL7 全部检出 | pass |
| git diff --check | 无问题 | 无问题 | pass |

### Errors
| Error | Resolution |
|-------|------------|
| apply_patch 调用格式反复失败 | 改用 PowerShell 写入文件 |
| 新增断言首轮漏掉两条接线 | 补判据并重跑 MUT-TL1/T MUT-TL4 |
| mutate.ps1 写回丢失 BOM | 用 UTF-8 BOM 字节写回并统一 LF |
# Progress

## 2026-11-02 — Phase 6 start

- Re-read project constraints and planning state.
- Preserved all uncommitted timeline work.
- Confirmed the product code has not yet been migrated.
- Identified the reusable UGUI pieces in `SpectreReplay/Ui/Timeline`.
- Confirmed StutterFix patterns for hit blocking, pooled graphics, and runtime sprite creation.
- Next: implement the settings window skeleton and shared UGUI controls.

## Session: 2026-11-12

### Current Status
- **Task:** 渲染成片音画偏移修复（方案 A：mux 侧 -itsoffset）
- **Result:** 已完成并实机验证对齐

### Actions Taken
- 根因确认：实机画面比音频滞后校准量（inputOffset），离线渲染没有扬声器延迟补偿，校准被烧进成片。
- MuxWith 增加音频偏移参数：正值音频侧 itsoffset、负值视频侧（避开负 PTS）、零值命令行与历史一致；抢救命令同步。
- 新增设置 RenderAudioOffsetMs（默认 -1 跟随 scrConductor.calibration_i，0..500ms 显式覆盖），界面开关+滑条，i18n 4 键 × 14 语。
- 新增 MuxOffsetCheck（20 行断言）+ MUT-MUX1/MUT-MUX2 变异，闸门 188 条全绿（commit 4140e18），文档同步（bdfeb2f）。
- 实机重渲 Starlight Ignition：tick 整体 +0.124s，第一击视觉锚点 2.767s 与音乐首响 2.741s 残差 ≈26ms（修复前 ≈150ms），对齐确认。

## Session: 2026-11-12 (2)

### Current Status
- **Task:** 对外按键接口（KeyViewer 拉取 API + ReplayInput 通用推桥）
- **Result:** 已完成，已部署

### Actions Taken
- SpectreAPI 新增按键查询面（ApiVersion 0.5→0.6）：IsReplayActive / IsReplayKeyHeld / WasReplayKeyPressedThisFrame / GetHeldReplayKeys（never-null）/ ReplayKeyChanged 事件（主线程封送 + 逐订阅者隔离）/ UnityKeyToVk；ManagedInput.Note 翻转即发事件。
- 反编译 RainingKeys 0.5.0：确认 ReplayInput 推式契约（OnStartInputs/OnKeyPressed/OnKeyReleased/OnEndInputs，受控模式停物理轮询）为跨模组通用约定；按用户要求按类型名扫描全部程序集，找到几个驱动几个，不绑某一家。
- ReplayInputBridge 五处接线（Start/Stop/PumpInput 边沿/Rebuild 重播种/Main 复位）；VK→Unity KeyCode 走 SkyHook 逆链 + VK_APPS=93 互逆特判；Reseed 用「重建容器 + 净按住态补 Down」。
- 实测修正三处事实错误：KeyCode.Menu 实为 319（反射实测，旧注释 276/文档 277 均错）；文档版本探测改成员反射（字符串比较在 0.10 会误判）；暂停语义核实（无暂停钩子停回放，IsReplayActive 保持 true）。
- 新增 ApiSurfaceCheck（7 行）+ MUT-API1/2/3、ReplayInputBridgeCheck（7 行，含「不许绑死一家」反向判据）+ MUT-RI1/RI2，全部实测变红。闸门 202 条全绿，已部署。

## Session: 2026-11-13

### Current Status
- **Task:** 特效事件驱动审计（只读）＋ 0.3.1 版本发布
- **Result:** 两件均完成；v0.3.1 已推送 origin（提交 64f2d80，tag v0.3.1）

### Actions Taken
- 特效事件驱动审计（只读扫 `adofai-libs/source/Assembly-CSharp`，不改代码）：游戏本体无 JRP 雨滴级别墙钟隐患。ffxPlusBase 事件派发走 `cond.songposition_minusi`（被钉歌曲时钟）；50+ ffx 子类动画全为 DOTween（吃被钉 dt）；4 处 `ParticleSystem.Simulate` 全部定性无害（编辑器 scrub / 常量预热 / Start 预热 / 菜单）；Stopwatch 0 命中；`scrConductor` 的 DateTime 在输入域（AsyncInputManager.currFrameTick），非特效；unscaledDeltaTime 44 文件按构造安全；videoBG 由模组 PinVideoPlayers 接管。
- 唯一外部时钟族：`GetSpectrumData` 谱线视觉（scrBarMaker / scrBgbarnew / scrVolumeTracker*）。模组 AudioRenderer 离线混音已把音频 DSP 纳入帧驱动权威，理论一致但未实测——建议实机验证带 `TrackColorType.Volume` 或背景条的关卡。
- 附带发现（游戏本体）：静音段 `Mathf.Log(0) = -Infinity`——scrVolumeTrackerFade 无保护；scrFloor Volume 走 Color.Lerp Clamp01 安全；scrVolumeTrackerScale / scrBgbarnew 自带 0 保护。
- 0.3.1 发布：三处版本文件 → Release 构建 0 警告 → verify 闸门 206 条全绿（后台跑，含部署一致性）→ 版本提交 64f2d80（只含版本文件，格式照 35c8e60）→ 附注 tag v0.3.1（SSH 签名）→ 用户确认后 `git push origin master v0.3.1`。
- AGENT.md 新增 §7.8 版本发布配方（版本文件三处、tag 必须 `-a -m`、推送先确认）。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| 主项目构建 | 0 错误 0 警告 | 0 错误 0 警告（3.17s） | pass |
| verify 闸门 | 非 0 断言且 0 失败 | 206 条断言，0 失败，全部通过 | pass |
| 版本提交范围 | 仅三处版本文件 | info.json / repo.json / AssemblyInfo.cs（5 处替换） | pass |
| tag v0.3.1 | 附注+SSH 签名，指版本提交 | 指向 64f2d80 | pass |
| 推送 | origin master + tag 更新 | 8a0b5c8..64f2d80、`* [new tag] v0.3.1` | pass |

### Errors
| Error | Resolution |
|-------|------------|
| 裸 `git tag v0.3.1` 弹 Vim 挂起（先吃满 run_code 120s 墙钟，再 60s 超时） | 全局 `tag.gpgSign=true` 把 tag 升级为附注+SSH 签名；改用 `git tag -a v0.3.1 -m "…"`，教训写入 AGENT.md §7.8 |
| bash 末尾 Remove-Item 被硬安全策略整条拦截，连带 git add/commit 未执行 | 删除类命令不与关键命令混排；临时提交信息文件留在 `.git/` 内不删 |

## Session: 2026-11-14

### Current Status
- **Task:** 清理审计 A-F7 死代码（ReplayKeys 常量类）
- **Result:** 已完成

### Actions Taken
- 按审计 A-F7 整套删除：`Features/Replay/Data/ReplayKeys.cs`（33 个旧字典格式键名常量，全仓 0 引用）+ 12 个文件里的 `using static` / `using K` 死引用（ReplayDecoder / ReplayFileInfo / Patch_Lifecycle / Patch_Playback / Patch_Recording / ReplayAnalyzer / ReplayManager / ReplayRecorder / VideoRender / VideoRenderJob / Pages / SpectreState）。
- UiPreview.csproj 同步清理：移除 ReplayKeys.cs 的 Compile 项与头部清单注释行。
- 审计文档 A-replay-core.md 的 F7 条目标注已修。
- 注意区分：SpectreAPI.GetHeldReplayKeys() 是活的对外按键查询 API，与被删常量类无关，未动。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.6s） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条断言，0 失败，全部通过（含死代码审计 6 项） | pass |
| 残留检查 | SpectreReplay 源码内 ReplayKeys 仅剩 GetHeldReplayKeys API | 仅 SpectreAPI.cs 一处 | pass |
