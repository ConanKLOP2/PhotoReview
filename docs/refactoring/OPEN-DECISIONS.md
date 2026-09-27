# Decisions (Q-*)

Status of work: [`../ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) · finished groups: [`HISTORY.md`](HISTORY.md).
Full rationale of older rows: `git show 1de561c:docs/refactoring/OPEN-DECISIONS.md` and `...:docs/refactoring/archive/OPEN-DECISIONS-detail.md`.

**Adding a decision (avoid merge conflicts across parallel PRs):** put ALL detail (method names, file:line, measurements) in a new
file under [`decisions/`](decisions/) named after the ID, never inline here. Add exactly ONE new row below in the Decided table
with a true one-sentence summary and a Markdown link to that new file -- a long inline row is exactly what caused repeated
multi-round merge conflicts on 2026-09-27 (a growing shared file that most concurrent PRs all append to).

## Open

| ID | Question | State |
|---|---|---|
| **Q-R29** | NAS (500 x 3-7 MB JPEG over wifi): (1) `ThumbnailCache.BuildKey` UI-thread stat per cold navigation; (2) whole-folder preload has no I/O priority/bandwidth cap. Both parts narrowed to negligible/no-sync-gap on local disk (see [R01-R13](decisions/R01-R02-R03-R13.md), [R04-R14](decisions/R04-R14.md)); the NAS/slow-link case itself is still untested and open. | OPEN 2026-09-26 - awaiting user. Present options with pros/cons (see AGENTS.md) before deciding. |

## Decided (one line each)

**Older groups** (rationale in ADRs, `HISTORY.md`, git): Q-D1..D4, Q-ST1..ST4, Q-T1..T4, Q-OC14/15, Q-S3, Q-AR1..AR5, Q-L1..L8, Q-IO1 - all decided and implemented.

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
