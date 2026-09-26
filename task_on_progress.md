# Current Work — PhotoReview

**Updated:** 2026-09-26 (evening) | **Base:** master `fac8347` (PRs #94–#102 merged) | **Open:** PR #103 `integration/policy-fixes-2026-09-26` (5 fix branches, CLEAN, not merged)

## Now

- **Merged to master 2026-09-26:** #94–#97 (overnight review + wave5), #98 (AGENTS decision format), #99/#100 (Actions pinned by SHA + Dependabot), #101 (Codex full-source audit, F2 by design), #102 (architecture review plan). Their branches and worktrees are deleted.
- **Open — PR #103** (`integration/policy-fixes-2026-09-26`): Q-R25 policy decisions + audit fixes — Undo after folder change (APP-03), canonical shortcut keys (Enter/Return), Esc cancels duplicate-check hashing, F0/F1/F3/F4 from the source audit, thread-pool pre-warm in tests. Worktrees `app03-undo`, `dup-cancel`, `f3-size`, `f4f1f0`, `shortcut-canonical`, `integ-policy` still exist for it; delete after merge.
- **Architecture review plan (in master, #102):** [ARCH-REVIEW-2026-09-26-SUMMARY](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md) — tasks AR10–AR19 all TODO. AR12 and AR15a/b touch no file of #103 and can start now; AR11, AR10, AR13, AR14, AR19 overlap #103 (`MainWindow`, `MainViewModel`, `Settings*`) — start only after #103 merges. Q-AR6..Q-AR10 wait for the user (options + recommendation in each `arch-review/AR1x` file).
- **User:** GUI checks still open from the overnight review: Settings > General > Updates, Defaults button (WIC, EXIF off), close during a folder scan, Recycle Bin undo on a non-English Windows (`undelete` fallback unverified), decoder fallbacks with damaged EXIF/ICC; real-machine perf run for natural sort / snapshot validator on F4.
- **Local build:** `src/PhotoReview.App/bin/Release/net10.0-windows` rebuilt from master `fac8347` (see the session note for the version).

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
