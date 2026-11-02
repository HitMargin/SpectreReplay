# Findings & Decisions

## Requirements
- 动态时间轴必须支持播放头、缩放、平移、任意时间 seek、区间选择和渲染裁剪。
- UGUI 时间轴独立于现有 IMGUI 设置页，不修改原始 ReplayData。
- 歌曲时间是主时间，渲染帧号由区间和 fps 派生。

## Research Findings
- StutterFix 的可复用模式是 Canvas + pooled slots + MaskableGraphic 批量绘制 + 输入拦截。
- 当前仓库已有遗留 Settings.TimelineOn，但此前没有时间轴实现、seek 或区间渲染接口。
- ReplayPlayer 此前只支持按砖块 FastForward；HitSnapshot 没有精确歌曲时间。

## Implemented Findings
- 新增 ReplayTimelineIndex、TimelineSelection、ReplayTimelineState 和 TimelineController。
- 新增 ReplayPlayer.SeekToTime、TryGetSongPosition 与楼层/按键游标重建。
- 新增 TimelineOverlay/TimelineMarkersGraphic UGUI 时间轴，支持播放头、区间手柄、滚轮缩放和平移。
- 新增 VideoRender.RenderRange，并将区间传入 VideoRenderJob，按区间起点选砖、按区间终点停止。
- 拖动时间轴时通过 TimelineController.InputCaptured 隔离玩法输入查询。
- 新增 ReplayTimelineCheck、7 条时间轴形状断言与 MUT-TL1~MUT-TL7 变异验证。

## Verification
- tools/verify.ps1 -Ci -SkipDeploy：全部通过。
- 主项目 Release：0 错误，0 警告。
- 离线套件：164 条断言，0 失败。
- 只读审计：全部通过。
- MUT-TL1 至 MUT-TL7：全部被检出。

## UGUI Settings Migration Findings
- The visible settings surface is still UMM `OnGUI`; `OnGui.OnGUI` draws tabs and calls five IMGUI page methods.
- There are already working UGUI primitives under `Ui/Timeline`: overlay Canvas, GraphicRaycaster, `Text`, `InputField`, pointer hit tests, drag/resize, rounded sprites, and pooled UI meshes.
- The existing page methods combine visual rendering and business actions. A UGUI migration should reuse those business actions instead of duplicating list/cloud/render state.
- Preserve `OnShow`, `OnHide`, `OnSave`, `Pages.FlushPending`, `Settings.CommitSafe`, delayed slider commits, hotkey capture, native file dialogs, cloud confirmations, and render cancellation.
- `D:\Projects\StutterFix\SettingsWindow.cs` still uses IMGUI `OnGUI`; reference its `UiDraw` canvas/slot/batch drawing patterns rather than copying the entire settings window.

## Resources
- D:/Projects/SpectreReplay/AGENT.md
- D:/Projects/SpectreReplay/docs/verify.md
- D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice/UMMMods/StutterFix
# Phase 6 — 全设置界面 UGUI

## Current architecture

- UMM callbacks remain in `Ui/OnGui.cs`.
- `Pages.cs` is a single static class containing all page state, settings bindings, and actions.
- Existing IMGUI helpers in `OnGui.cs` are presentation-only, but page actions are interleaved with IMGUI drawing.
- `Settings` and `ConfigManager` remain the only persistence path. PlayerPrefs is forbidden.
- Existing timeline UGUI proves the project can host `ScreenSpaceOverlay` canvases and runtime-created `UnityEngine.UI` controls.

## Migration decision

- Change `Pages` to `internal static partial class Pages`.
- Add `Pages.Ugui.cs` in the same class so existing private fields/actions remain the single source of business state.
- Add a persistent `SettingsWindowUgui` MonoBehaviour and a reusable `SettingsUiKit`.
- `OnGui.OnGUI` becomes a bridge that ensures the UGUI window is shown; it must not emit visible IMGUI widgets.
- Preserve `OnShow`, `OnHide`, `OnSave`, `FlushConfig`, and close/save semantics.

## Runtime requirements

- Canvas: `ScreenSpaceOverlay`, high sorting order, `CanvasScaler` 1920x1080.
- Full-screen hit-block image prevents interaction with the game behind the window.
- Canvas hiding must use `canvas.enabled = false`, not deactivation.
- Ensure one `EventSystem` and `StandaloneInputModule`.
- Font atlas warm-up happens before first user-visible frame.
- Scroll views require `RectMask2D`, transparent viewport graphic, and ScrollRect.
- Slider edits update memory while dragging; release commits through `Pages.Commit()`.
- Text fields commit on Enter, blur, panel close; Esc cancels.
