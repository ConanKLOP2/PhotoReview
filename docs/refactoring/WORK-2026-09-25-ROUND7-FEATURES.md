# 2026-09-25 — Review round 7 fixes + viewer features (in progress)

Decision log for this batch lives here and in [OPEN-DECISIONS](OPEN-DECISIONS.md) (Q-R18, Q-R19). Branch `develop` carries these docs; code lands on master through one integration PR.

## Plan

1. Nine agent branches, each from master `ebf2bf3` (below), pushed but **no PR each**.
2. Integrate: merge all nine into `integration/2026-09-25-features` (from master), resolve append conflicts (AppSettings, ShortcutMappings, ReviewCommandType, MainWindow, `en.json`/`vi.json`), wire `ShowInfoOverlay` (key `I`) to also hide the EXIF line.
3. Redesign the Settings window on that branch (left navigation + pages: General, Display, Performance, Shortcuts, Files & safety, Diagnostics; every new option and every EXIF field toggle; ✕ to clear a shortcut; duplicate-key warning).
4. Full gate, one PR to master; then merge `develop` docs.

## Branches

| Branch | Content | Status |
|---|---|---|
| `feat/viewer-quick-features` | `End` last image, `D1` zoom 100 %, `I` toggle overlay (persisted `ShowInfoOverlay`), `ShowFileInfo`, `ShowFolderInfo` (bottom-right: current / PgUp / PgDn folder, computed in background, no I/O when hidden) | agent running |
| `feat/mouse-zoom` | `MouseWheelAction` {Zoom (default), Navigate; Ctrl+wheel zooms}, `ClickToZoomEnabled` (on), `ClickZoomPercent` 100 (10–800) anchored at the cursor, click again = Fit, `KineticPanEnabled` (on) | agent running |
| `feat/move-copy-to` | `M` Move to…, `Y` Copy to… with folder picker, `LastMoveToFolder`/`LastCopyToFolder`, `MoveCopyReuseLastFolder` (off; Shift forces picker); same journal/undo pipeline as action profiles | agent running |
| `feat/exif-info` | One EXIF line; `ShowExifInfo` (on); `ExifInfoFields` flags, one toggle per field: FileName, DateTaken, Dimensions, Camera, Lens, Iso, FocalLength, Aperture, ShutterSpeed (all on); EXIF read during decode, stored with the preview cache (format bump), no extra disk read | agent running |
| `feat/q-r18-instance-mode` | `InstanceMode` {SingleWindow (default), PerFolder}; PerFolder lock/pipe follow the shown folder; next start | agent running |
| `fix/r7-core-safety` | startup journal reconcile restored (INV-6, ADR 0003); cross-volume Move with source left = failure (keep both files); Recycle restore matches names with hidden extensions; runtime destination validation; journal `_tailChecked`, sharing-violation retry, journaled Undo of Move | agent running |
| `fix/r7-app` | **High:** stale cache key after an external edit; Undo of a Move from another folder; bounded shutdown flush (Q-R5 wiring); dialog re-checks; forward restore keeps window state; Esc with tools popup; close during a file action; Settings JSON destination check; export error; fullscreen placement; tests no longer overwrite `window-placement.json` | agent running |
| `fix/r7-imaging` | re-queue evicted near images; no source-bytes prefetch on disk-cache hit; TurboJpeg no double read; bounded `ReviewMetrics._sourceOpens`; orphan `*.tmp` cleanup | agent running |
| `fix/r7-tools-tests` | CLI benchmark excludes setup; `--ui-next-probe` temp data root; golden seeded tests; WarmNext race; Native bin assert; `TestAppHost` fake bin; Fit test TempRoot; CI runs doc-links/docs-budget, verify-all runs i18n-check; warmup excluded from metrics | agent running |

Every agent: no real Recycle Bin, no Native/Slow/Manual runs, no SettingsWindow edits (redesign comes after integration), mutation-checked tests, 0-warning gate.

## Review round 7 (2026-09-25) — findings

Read-only review of all code at `8de2ac4` by four agents (Core+Platform, Imaging, App, tools/tests). Fixed already: preload LRU thrash (#82, 90 % fill brake), docs-budget root/per-file (#83). All others are assigned to the `fix/r7-*` branches above. Not fixed, needs a decision: single-instance scope → Q-R18 (now a setting).

## Needs the user (GUI / real machine)

Perf run for Q-R17 on fixture F4; Recycle restore with Explorer "hide extensions" on; Settings look; click-zoom/kinetic feel; EXIF line; folder info; Explorer double-click in both instance modes.
