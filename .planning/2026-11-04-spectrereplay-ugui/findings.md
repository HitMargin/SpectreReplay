# Findings

## Baseline

- Release build: 0 warnings, 0 errors.
- `tools/verify.ps1 -Ci`: 165 assertions, 16 failures in the offline suite, 2 failing gate sections.
- The worktree already contained the UGUI migration skeleton before this session; do not revert it.

## Current Failures

- `ui.open_ugui` is referenced by the native IMGUI panel but missing from `tools/i18n/source.json`.
- The current UGUI shape checks still require `_draw.Begin(...)`, `_draw.End()`, and `DrawWindow()` in
  `SettingsWindowUgui.cs`, although the implementation uses persistent native UGUI controls.
- `deadcode-audit.ps1` still reports a zero-call static method; classify the actual obsolete helper
  only after running it in report mode.

## StutterFix UGUI Reference

- `UiDraw` keeps one GameObject per reusable visual slot and changes only the used prefix each frame.
- Each slot caches geometry, color, text value, font style, radius, and alignment; unchanged fields are not assigned.
- Unused slots are disabled in `End()`.
- `UiDraw.Warm(...)` requests dynamic-font glyphs at the exact size/style before first display.
- `UiInputBlock` does not disable the game EventSystem. It places transparent raycast blockers in a
  high-sorting canvas because disabling `EventSystem.current` caused game-side NullReferenceExceptions.
- `UiBars` batches many changing rectangles into one mesh and throttles rebuilds.

## SpectreReplay Migration State

- `SettingsWindowUgui` currently uses a persistent native UGUI tree for header, sidebar, and tabs.
- Page `Refresh` delegates currently only refresh the header, so external state changes do not sync into
  toggles, sliders, dropdowns, replay/cloud rows, or status text.
- The window currently owns or disables an EventSystem. This conflicts with the shared EventSystem rule.
- Dynamic replay/cloud/keymap rows are created during page construction and have no reusable slot owner.
- Input fields commit only on end-edit; closing during editing must explicitly commit before config flush.
- Replay info is created as a fresh UGUI subtree at call sites instead of binding an existing view.
