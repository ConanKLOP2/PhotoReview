# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `dd85670` (v2.0.90) | **Review branch:** `codex/review-function-audit-20260925`

## Now: function-level codebase review

- **What:** [Function audit](docs/refactoring/REVIEW-2026-09-25-FUNCTION-AUDIT.md) at `dd85670`: independent Core/Platform, Imaging/Benchmarking, App, and tools/tests lanes. Read-only code review; no production edits.
- **Git:** PR #86 integration and PR #87 defaults are on `master`; `develop` fast-forwarded to `dd85670` on 2026-09-25. Do not reuse older "PR open" statements.
- **Validation:** `dotnet build PhotoReview.slnx -c Release --nologo` passed, 0 warnings; default filtered `dotnet test` passed 1779, skipped 6. Native/Slow/Manual and GUI were not run. One corrupt-JPEG CLI repro returned exit 0 with 0 valid groups.
- **Caution:** the default test run included `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder`, which writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; the path is now absent. Its prior state is unknown. Do not rerun this test until isolated.
- **Next:** finish function/test coverage, review findings and decisions in the audit; Q-R19 defaults, GUI checks in [WORK-2026-09-25](docs/refactoring/WORK-2026-09-25-ROUND7-FEATURES.md), and Q-R17 real-machine perf remain open.

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
