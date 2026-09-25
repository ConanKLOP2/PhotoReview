# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `2d52f6d` (#76 merged) | **Branch:** `docs/decisions-q-r7-r11`

## Now: round-2 decisions implemented, PRs #77 #78 #79 open

- **User:** review + merge #77 (Q-R7), #78 (Q-R10, needs a GUI check: multi-select open from Explorer), #79 (Q-R8); the real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven); visual checks: Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04.
- **Plan / evidence:** [plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

## Review round 3 (2026-09-25, branch `review/2026-09-25-round3`, PR pending)

- Base master `633ea3c` (#77-#80 merged). Fixed: permanent-delete dialog re-checks folder/busy/catalog after the nested loop; alpha-opacity scan moved to the persist worker; forward client (empty reply = NoInstance, >16 paths truncated); log shutdown after forward server; preload survives corrupt files; sibling nav with trailing `\`; malformed language file no longer throws; corrupt `config.json` is not overwritten if its backup fails; `EstimatedBytes` uses real bpp; compare key opens compare when a pair exists; F11 restores the previous window state.
- Gate: build 0 warnings, all non-Manual/Native/Slow tests green.
- **Not fixed (decide/verify):** client timeout after a delivered write shows the 'already open' dialog (needs a Delivered-vs-Unknown decision); `TryGetFileInfo` catch-all drops a file on transient IO error; Space stolen from compare panes/buttons; shortcut capture box traps Tab/Esc + accepts numeric keys; ZoomOut from Fit can enlarge tiny-FitZoom images; failed full-res zoom retried per step; Settings Save/Process.Start exceptions silent; `\photos` destination; DuplicateCleanup ignores AllowPermanentDelete; persist queue not in RAM budget; unknown journal enum -> first member; PreloadScheduler.Dispose can block UI; UndoLast is single-level (intent?).

## Status by Group

Group status: see [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md) (single source; do not copy it here).

## Critical Process Rules

- No direct `master` commits: branch → PR → review
- No `ApplyFitViewAsync` single-pass without T89 evidence
- No broad `Dispatcher.Invoke` / `GetRequiredService` (ADR 0005 rule, AR04 DONE)
- No silent `IgnoreInaccessible`; durability changes only per ADR 0007 (journal mode setting, session no-fsync)
- No OS SendInput/SetForegroundWindow in test harnesses
- Never run code that deletes/sweeps the user's real Recycle Bin (tests use fakes)
- Don't compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"
tools/verify-all.ps1
```
