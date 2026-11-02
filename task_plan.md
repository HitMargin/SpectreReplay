# Task Plan: SpectreReplay 全设置界面 UGUI 迁移

## Goal
将当前 UMM/IMGUI 设置界面迁移为可交互的 UGUI 设置窗口，保留录制、回放、渲染、去特效、关于五个页面的全部行为，完成构建、验证、变异回归和部署。

## Next Step
实现 UGUI 窗口骨架与共享控件层，随后逐页迁移五页内容。

## Current Phase
Phase 6

## Phases

### Phase 1: 规格与现状
- [x] 从上一轮设计推导时间轴、区间、seek 和渲染需求
- [x] 核对当前工作区、游标模型、渲染入口和遗留 TimelineOn
- **Status:** complete

### Phase 2: 纯逻辑核心
- [x] 实现 ReplayTimelineIndex / TimelineSelection / TimelineState
- [x] 实现 ReplayPlayer.SeekToTime 和按键状态重建
- [x] 为时间查询、区间约束和 seek 行为添加离线断言
- **Status:** complete

### Phase 3: UGUI 时间轴
- [x] 实现 Canvas 宿主、批量 Graphic、播放头与区间手柄
- [x] 实现拖拽、缩放、平移和输入隔离
- [x] 接入已装载回放的显示与卸载清理
- **Status:** complete

### Phase 4: 区间渲染
- [x] 将 RangeStart/RangeEnd 传入 VideoRenderJob
- [x] 对齐播放起点、捕获终点、尾巴和硬停止
- [x] 渲染收尾保持现有取消/失败语义
- **Status:** complete

### Phase 5: 验证与交付
- [x] 运行离线套件、主项目构建和只读审计
- [x] 对新增断言执行变异验证
- [x] 审查 Git diff 并交付结果
- **Status:** complete

### Phase 6: 全设置界面 UGUI 迁移
- [ ] 盘点五页控件、动作、配置提交和动态状态
- [ ] 实现持久 UGUI 宿主与共享控件层（Toggle/Button/Slider/Dropdown/Input/Scroll/Tooltip）
- [ ] 迁移 Record、Playback、Render、Effects、About 页面
- [ ] 将 UMM OnGUI 收口为隐藏宿主桥接，保留 OnShow/OnHide/OnSave 与配置落盘语义
- [ ] 添加 UGUI 形状断言及对应变异
- [ ] Release 构建、verify、变异回归并部署到指定 UMMMods 目录
- **Status:** in_progress

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| 时间轴状态与 ReplayData 分离 | 选择区间是查看/渲染会话状态，不应写回原始回放 |
| 歌曲时间是唯一主时间 | UI、回放游标和渲染帧通过显式换算连接 |
| UGUI 负责整个设置界面 | 时间轴作为独立覆盖层保留，但设置页不再绘制可见 IMGUI |
| 先实现纯逻辑再接 UI | 让 seek 和区间约束可以离线验证 |

## Errors Encountered
| Error | Resolution |
|-------|------------|
| apply_patch 调用格式反复失败 | 改用原生 apply_patch 修改计划与产品文件 |
| 新增断言首轮未覆盖两条接线 | 补充对应形状判据并重跑 MUT-TL1/T MUT-TL4 |
| PowerShell 写回 mutate.ps1 丢失 BOM | 用 UTF-8 BOM 字节写回并统一 LF |
