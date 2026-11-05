# SpectreReplay UGUI Settings Migration

## Goal

Move every SpectreReplay settings page from IMGUI to fully functional UGUI while preserving all pages and interactions. Keep the native UMM IMGUI panel and its "open UGUI" entry. Finish with a Release build, repository verification, mutation verification for new checks, deployment, and target-directory hash verification.

## Phases

### Phase 1: Current-state audit and build baseline

**Status:** complete

- Inspect current worktree without reverting existing changes.
- Confirm the UGUI entry point, native IMGUI entry, and shared Settings model.
- Establish a clean Release build baseline.

### Phase 2: Shared state and input bridges

**Status:** in_progress

- Unify IMGUI and UGUI hotkey capture state.
- Route UGUI directory selection through the existing deferred native picker.
- Commit pending text and configuration when UGUI closes.

### Phase 3: Persistent settings pages

**Status:** pending

- Build each page once and switch pages by active state.
- Refresh values without destroying controls.
- Use persistent slots for dynamic replay, cloud, and keymap rows.
- Keep replay details inline and never load a replay merely to inspect it.
- Avoid synchronous encoder probes on the render page.

### Phase 4: Text, verifier, and mutation gate

**Status:** pending

- Add all required locale entries through the i18n sources and generator.
- Replace obsolete UGUI assertions with checks for real persistent control contracts.
- Add a mutation that fails the new contract when persistence is removed.
- Run the complete CI verifier.

### Phase 5: Build, deploy, and verify deployment

**Status:** pending

- Run the Release build.
- Deploy with `tools/deploy.ps1 -Config Release`.
- Verify the target DLL timestamp and hash.

## Constraints

- Do not use PlayerPrefs.
- Do not call `scrController.Restart()` from `scnEditor`.
- Keep the native UMM IMGUI panel and its UGUI opening button.
- Do not manually edit generated `Ui/LocData.cs`.
- Modify files only with the native `apply_patch` tool.
- Do not remove or revert unrelated existing worktree changes.

## Next Step

Replace the owned EventSystem and one-shot control assumption with the shared EventSystem,
persistent control bindings, and close-time text/config flush.
