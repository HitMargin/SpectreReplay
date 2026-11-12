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
