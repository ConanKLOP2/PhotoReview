# Decisions (Q-*)

Status of work: [`../ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) Â· finished groups: [`HISTORY.md`](HISTORY.md).
Full rationale of older rows: `git show 1de561c:docs/refactoring/OPEN-DECISIONS.md` and `...:docs/refactoring/archive/OPEN-DECISIONS-detail.md`.

**Adding a decision (avoid merge conflicts across parallel PRs):** the Decided table below is GENERATED -- never hand-edit it.
Put ALL detail (method names, file:line, measurements) in a new file under [`decisions/`](decisions/) named after the ID, with a
frontmatter block at the top (`id:` = the ID(s) as they should appear in the table, `order:` = an integer placing the row, `summary:`
= a true one-sentence prose summary, no link). Then run `tools/generate-open-decisions.ps1`, which rewrites the generated region
from every `decisions/*.md` file's frontmatter, and commit the result. A routine PR therefore only ever adds its OWN new file under
`decisions/` plus the regenerated table -- it can never conflict with another PR's own new file, which is what the old
"add exactly one new row inline" convention could not guarantee (repeated multi-round merge conflicts on 2026-09-27, a shared file
every concurrent PR appended to). `tools/check-open-decisions.ps1` (wired into CI and `tools/verify-all.ps1`) fails the build if the
generated region doesn't match what the generator produces right now -- e.g. a hand-edited row, or a new `decisions/*.md` file
added without frontmatter or without re-running the generator.

## Open

| ID | Question | State |
|---|---|---|

## Decided (one line each)

**Older groups** (rationale in ADRs, `HISTORY.md`, git): Q-D1..D4, Q-ST1..ST4, Q-T1..T4, Q-OC14/15, Q-S3, Q-AR1..AR5, Q-L1..L8, Q-IO1 - all decided and implemented.

<!-- BEGIN GENERATED DECIDED TABLE (tools/generate-open-decisions.ps1 -- do not hand-edit below) -->
| ID | Decision |
|---|---|
| Q-Z1 | Zoom 100 % = 1 source pixel, original decoded on demand (#43, #47, [ADR 0008](../adr/0008-zoom-source-pixel.md)). |
| Q-R1..R6 | (a) for all (2026-09-24, #76): never persist previews with alpha; relative action destinations stay inside the photo folder; Integration tests run in CI, `Stress` dropped; scripts/example config in `deploy/`; wait up to 2 s for the session write at shutdown; accessibility scope = user-facing windows. |
| Q-R7 | Opaque PNG/WebP previews may be disk-cached (alpha scanned on the downscaled bitmap, #77). |
| Q-R8 | Recycle on removable/network/UNC drives refused by default; opt-in "allow permanent delete" (default off), #79. |
| Q-R9 | DECLINED: no Recycle Bin orphan sweep (would delete from the real bin). Tests only clean their own items. |
| Q-R10 | Opening a photo from Explorer while the app runs is forwarded to the running instance over a named pipe (#78); Q-R12: unknown outcome exits without the "already open" dialog. |
| Q-R11 | Leave as is: session resume discarded when Explorer order applies; benchmark profiles run with disk cache off; dead `MemoryReserveBytes`; per-solution version computation. |
| Q-R13..R16 | Single-step undo kept; duplicate cleanup on drives without a Recycle Bin stays refused (one up-front message); group B/C fixes all done. |
| Q-R17 | Whole-folder preload estimate: box bound `entries x w x h x 4` until 8 previews are measured, then measured mean x 1.25 (capped at the bound); Q-R26 fixed the resulting UI-thread cost (`preloadKick`). |
| Q-R18 | Setting `InstanceMode`: SingleWindow (default) or PerFolder. |
| Q-R19..R22 | Defaults reviewed by the user: RAM cache 50 % (applies after restart, Q-AR6), keys End/1/I/M/Y, Move/Copy-to asks each time; `ExifInfoFields` default excludes FileName/Dimensions, `ShowFolderInfo` and `ClickToZoomEnabled` default off; `DecoderBackend=WicDirect`, `ShowExifInfo=false`, Defaults button reads `new AppSettings()`; default action names from the catalog, folders `Group-2/3/4`. |
| Q-R23 | Updates: manual "Check for updates" button only (GitHub Releases API); no auto-check/download. |
| Q-R24 | CI tags `v2.0.N` after each merge; releases are published manually (Actions > Release > Run workflow, tag input), no automatic draft. |
| Q-R25 | Overnight review merged (#96, #99, #103); follow-ups Q-R26/Q-R27. |
| Q-AR6..AR10 | RAM % keeps restart (AR10 closed); readability probe runs in the background after the first frame (c, AR16); TurboJpeg labelled experimental; benchmark window kept; AR14 comment only, AR19 `ShowSkippedFiles` via `IDialogService`. |
| Q-R26 | Keep Q-R17 whole-folder preload; cause of the higher first-visual was the per-navigation cache lookup on the UI thread, fixed (`PreloadKickOffCallerTests`). |
| Q-R27 | Each running operation holds a named kernel event (`WindowsLiveOperationRegistry`) from Prepared to outcome; startup reconcile skips live operations (#116). |
| Q-R28 | Settings > Export strings writes every key (untranslated first); Reload translations reports file/entry problems (#120). |
| Q-R30 | UI feedback: dark title bar + scrollbars, toolbar auto-hide, Open folder/Settings in the context menu, title-bar fields setting, optional Modified-date EXIF field, info font size, click-zoom key `2`, glide smoothing `Predict`; Shift+arrow panning declined (#119, #123). |
| Q-R31 | Preload window configurable (Settings > Performance, defaults 32 ahead / 8 behind, 1-500 / 0-500, applies after restart), #125. |
| Q-R32 | Arrow keys on a zoomed image only pan, never navigate; setting `ArrowKeyNavigatesAtZoomEdge` (default off) restores edge navigation (#126). Pan step = `ArrowPanStepPercent` (default 10 %, 1-100, #133). |
| Q-R33 | Sort modes `Default` (file-system order, no sort), `NameAscending`/`NameDescending` (app natural order) ignore Explorer order; `Name` follows Explorer. Default for new installs changed from `Name` to `Default` (2026-09-27, user request, PR #186). |
| Q-R34 | `ToolbarAutoHide` default off (saved true kept); new `InfoOverlayAutoHide` (default off, own delay 3000 ms) fades info on the photo, never while a message/progress/compare/dialog needs it (#129). |
| F-WIN-2 | (A) 2026-09-27: on a fixed drive whose Recycle Bin is off, too small for the file, or unreadable, Recycle is refused before journaling (no silent permanent delete; "allow permanent delete" does not apply), [ADR 0007](../adr/0007-io-durability-contract.md) amendment; branch `fix/recyclebin-quota-guard`. |
| Q-R35 | Keyboard zoom anchors at the pointer; kinetic arrow panning (#177). [Detail](decisions/Q-R35.md) |
| Q-R36 | Initial zoom presets incl. Fit width/height; `KeepZoomAcrossImages` (#178). [Detail](decisions/Q-R36.md) |
| Q-R37 | Optional crossfade on photo change, default off (#181). [Detail](decisions/Q-R37.md) |
| R01/R02/R03/R13 | UI-thread metadata I/O in navigation/Compare/file-actions: measured negligible, no fix (#191). [Detail](decisions/R01-R02-R03-R13.md) |
| Q-R38 | Context menu redesigned (Undo first, Zoom submenu, folder group togglable) (#183). [Detail](decisions/Q-R38.md) |
| R09/R10 | Recovery-dismiss race fixed; stale glide-stop flag traced as a non-issue (#192). [Detail](decisions/R09-R10.md) |
| R04/Q-R29 (partial) / R14 | Preload contention: no sync gap found locally (NAS still open); startup temp-file sweep moved to background (#195). [Detail](decisions/R04-R14.md) |
| R06/R07/R08/R11 | Benchmark/cache misc fixes; instance-forward ambiguity reclassified to `Unknown` (#194). [Detail](decisions/R06-R07-R11-R08.md) |
| Q-R05 / Q-R12 | Navigation notification count deduped; stale doc-comment links retargeted (#193). [Detail](decisions/Q-R05-Q-R12.md) |
| R15/R16/R17/R18 | Hash-service stat, catalog snapshot, drag-drop `Exists` checks and diagnostics-metrics sort all measured negligible; no fix. [Detail](decisions/R15-R16-R17-R18.md) |
| P03 | Undo limited to the current session; journal history no longer seeds Ctrl+Z at startup (user, 2026-09-27). [Detail](decisions/P03.md) |
| P02 | Recovery retry re-checks the journal's latest entry and live marker so a concurrent retry in another window can no longer journal a completed Move as Failed. [Detail](decisions/P02.md) |
| Q-R29 | User chose B then C-if-needed (2026-09-27); C part 1 (stat off UI thread) done here, part 2 (preload/viewer bandwidth contention) done in Q-R29-C2. [Detail](decisions/Q-R29-C.md) |
| GUI-CHECK-AUTOMATION | 5 of the 11 "GUI checks (user)" items now need no manual check; the rest reduced to one short visual/feel step each (new context-menu-structure, crossfade and KeepZoomAcrossImages UI tests). [Detail](decisions/GUI-CHECK-AUTOMATION.md) |
| TEST-HANG-GUARD | `tests/test.runsettings` (wired in via `tests/Directory.Build.props`) bounds every `dotnet test` to a 120 s per-test hang timeout and a 20 min session timeout with no CLI flags required; CI/verify-all.ps1's own `--blame-hang*` flags coexist without a duplicate-collector error (2026-09-27). [Detail](decisions/TEST-HANG-GUARD.md) |
| FLAKY-FolderLoad | CI flake in the folder-switch race test was a fixture bug (session sweep listing took the scan-block hook), not a production race; fake fixed (#209). [Detail](decisions/FLAKY-FolderLoad.md) |
| Q-R39 | New `ShowZoomMenuItems` setting hides the context menu's zoom cluster (Fit/Zoom to N%/Zoom submenu) as one unit; default off, unlike `ShowFolderMenuItems` (user request). [Detail](decisions/Q-R39.md) |
| CI-CROSSFADE-ANIMATION-FLAKE | Confirmed via real CI diagnostics the compositor ticks fine on `windows-latest`; the fixed-poll assertion could miss a fast 40ms fade under load -- replaced it with a value-changed watcher (no source change). [Detail](decisions/CI-CROSSFADE-ANIMATION-FLAKE.md) |
| TEST-SUITE-REVIEW-2026-09-27 | External test-suite review's 9 findings independently re-verified: 6 confirmed and fixed (test-only, no `src/` changes), 2 rejected as false positives/already-sanctioned, 1 left as a documented coverage gap. [Detail](decisions/TEST-SUITE-REVIEW-2026-09-27.md) |
| SEC-03 | Publish-guard containment made component-aware (path segments, not a raw string prefix) and resolves reparse points before checking approval, with a new `-SelfTest` gate in `verify-all.ps1`. [Detail](decisions/SEC-03-publish-guard-hardening.md) |
| SEC-02 | External review of `InstanceForwardClient`/`ForwardedPathProtocol`: pipe `FlushAsync`-after-successful-`WriteAsync` misreport traced as unreachable (named-pipe flush is a local no-op, no source change); `Encode`/`TryDecode` validation asymmetry confirmed and fixed (`Encode` now shares `IsAcceptablePath`). [Detail](decisions/SEC-02-forward-protocol-review.md) |
| TITLE-BAR-INSTANCE-LABEL | `MainViewModel.InstanceLabel` tags the title bar `[label] ...`: `TestAppHost.CreateMainWindow` sets it unconditionally (`[TEST]`) for every `Category=UI` window, and `PHOTOREVIEW_DIAG_INSTANCE_LABEL` opts a manually/agent-launched real exe into the same marker; unset is byte-for-byte unchanged. [Detail](decisions/TITLE-BAR-INSTANCE-LABEL.md) |
| SEC-01 | A relative Move/Copy destination through an existing junction/symlink could resolve outside the photo folder; `ActionDestinationPolicy`/`FileActionService` now also check the resolved (reparse-point-followed) path, via a new `IFileSystem.ResolveRealPath` seam. [Detail](decisions/SEC-01-symlink-destination-escape.md) |
| Q-R40 | Mouse-release kinetic glide velocity is scaled by a new `KineticScroller.PointerReleaseSpeedFactor` (0.65) to feel calmer, without touching the already-measured `KineticGlideSmoothing.Predict` frame-timing default or the keyboard-panning impulse path. [Detail](decisions/Q-R40-kinetic-release-damping.md) |
| Q-R29-C2 | ISourceReader seam built; preload/viewer bandwidth contention on a slow link measured material and fixed (PreloadScheduler caps to 1 concurrent preload decode instead of ~4 when the link is slow). [Detail](decisions/Q-R29-C2.md) |
| UX-FIXES-ROUND1 | Four UX findings addressed: Move to Recycle Bin added to context menu, gated behind an off-by-default setting (high); the dead, hardcoded-hidden shortcut hint line removed outright instead of un-hiding it (high); Recycle Bin terminology standardized (medium); the hint-wording fix (medium) is moot now that the hint itself is gone. [Detail](decisions/UX-FIXES-ROUND1.md) |
| Q-R41..Q-R52 | v2.0.203 user feedback triage: configurable zoom step, Open Folder/Custom Zoom shortcuts, confirm-before-delete, zoom-% HUD, empty-folder notice and settings export/import approved (default off/unconfigured where applicable); Refresh, in-viewer rename, trackpad gesture tuning and new format/RAW support declined. [Detail](decisions/Q-R41-Q-R52-user-feedback.md) |
| UX-FIXES-ROUND1 (terminology) | User chose to finish UX-FIXES-ROUND1 finding #3: the two English labels still reading "Recycle" (`action.recycle.name`, `enum.fileOperation.recycle`) now read "Move to Recycle Bin" like every other English string; Vietnamese already said "Đưa vào Thùng rác" everywhere. [Detail](decisions/UX-FIXES-ROUND1-TERMINOLOGY.md) |
| Q-RAW-01..07 | Camera RAW support implemented in open PR #239 (reverses Q-R52's RAW part): embedded JPEG preview, ORF LibRaw thumbnail fallback, optional full LibRaw decode on zoom, 100 % = sensor size, configurable JPG+RAW pairing (default Separate), 8 formats, Adobe RGB conversion, CC0 corpus + synthetic tests; RAW-62 real-machine check waived, not passed. [Detail](decisions/Q-RAW.md) |
| Q-RAW-COMPARE-GROUP | With Compare open on a JPG+RAW capture, Delete/Move act on the whole capture; provisional agent-chosen safe fix (not yet user-confirmed): always show a confirmation that names both files, regardless of ConfirmBeforeDelete. [Detail](decisions/Q-RAW-COMPARE-GROUP.md) |
| RV-D1 | Move undo and group-Move compensation treat the moved file at its destination as unchanged when its size is equal and its write time is within 2 s of the recorded one (FAT rounds to 2 s, exFAT to 10 ms); compares of files that never left their volume stay exact (option A, 2026-10-01, RV-C01). [Detail](decisions/RV-D1.md) |
| RV-D3 | Decided 2026-10-01 (option A): `ShortcutRouter.TryResolve` fires Recycle and every user Action only with NO modifier (Ctrl+Delete, Shift+Delete, Ctrl+Enter, Ctrl+F5 no longer delete/move/copy); Move/Copy-to-folder keep "no Ctrl, Shift = picker", Undo/OpenFolder keep Ctrl, navigation/zoom/toggles still accept Ctrl. [Detail](decisions/RV-D3.md) |
| RV-D4 | Changing "Image order" in Settings reloads the open folder at the current image (the same path as RawSupport/RawPairMode), so the new order applies immediately instead of at the next folder (option A, 2026-10-01). [Detail](decisions/RV-D4.md) |
| RV-D5 | Decided 2026-10-01 (option A): a duplicate batch-recycle that finishes after the user opened another folder reports a late-completion status (`IDuplicateCleanupSink.ShowLateActionStatus`, text `status.lateBatchRecycled`: counts, "cannot be undone with Ctrl+Z") instead of returning silently; it stays non-undoable and reloads nothing. [Detail](decisions/RV-D5.md) |
| RV-D2 | An unknown or garbage enum value in config.json (LoadingMode, ImageSortMode, ScalingQuality, DecoderBackend, KeyboardZoomAnchor, KineticGlideSmoothing) now loads as the AppSettings default and is listed in LastLoadRepairs (startup dialog), instead of silently becoming the enum's zero member (option A, 2026-10-01). [Detail](decisions/RV-D2.md) |
| COPY-PARTIAL-01 | A Copy that throws after writing part of its destination (disk full) keeps that partial file, because File.Copy gives no proof the destination was created by this call; Recovery shows a Conflict and the user deletes the file by hand (option c, user decision 2026-10-04). [Detail](decisions/COPY-PARTIAL-01.md) |
| W2-CC-02 | Decided 2026-10-06 (option B): PageUp/PageDown sibling-folder navigation always skips Hidden/System sibling folders (the current folder is kept even if hidden); no setting. Code lands in a separate PR (branch fix/sibling-nav-skip-hidden). [Detail](decisions/W2-CC-02.md) |
| SETTINGS-SAVE-MULTI-INSTANCE | Decided 2026-10-06 (option A): `SettingsStore.Save` across several windows in `InstanceMode.PerFolder` stays last-writer-wins (each process writes its whole in-memory settings, no reload, no merge); the trade-off is accepted and documented. [Detail](decisions/SETTINGS-SAVE-MULTI-INSTANCE.md) |
| F06 | Decided 2026-10-06 (option B): config.json is backed up before a repair write-back rewrites repaired values, like the salvage path does. Code lands in a separate PR (branch fix/settings-repair-backup). [Detail](decisions/F06.md) |
| P-DISP-02 | Decided 2026-10-06 (option A): leave the vblank thread alone until there is evidence; `D3DKMTWaitForVerticalBlankEvent` may block forever (display off or monitor change) but this is not proven. Revisit if the arrow-key panning re-check still fails or a display-sleep run shows the stall. [Detail](decisions/P-DISP-02.md) |
| TUNE-DEVICE-DEFAULTS | Decided 2026-10-07 (user: follow the recommendations): keep the shipped defaults for this PC (device config bench option D); the DOTNET_TieredPGO=0 re-run was done and it is NOT adopted (median -8.6 %, P95 +8 %, below the bar); the PerfCsvListener.Enqueue race stays as is (accepted); local branch codex/review-all-20261004 deleted (ledger stays in tag review-20261004-archive). [Detail](decisions/TUNE-DEVICE-DEFAULTS.md) |
| CLICK-ZOOM-KEY | The Click-zoom shortcut no longer toggles back to Fit when the image is already at the click zoom level (default: does nothing; use the Fit key); new setting `ClickZoomKeyTogglesFit` (default off) restores the old toggle. The mouse click-to-zoom still toggles. [Detail](decisions/CLICK-ZOOM-KEY-NO-TOGGLE.md) |
<!-- END GENERATED DECIDED TABLE -->
