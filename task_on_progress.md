# Current Work — PhotoReview

**Updated:** 2026-09-23 | **Base:** `master@b2048b6` (#23) | **Working branch:** `docs/ar07-docs-repair` (AR07 §1-4, PR pending)

## Now: AR07 §1-4 done (this branch); AR01-AR06 + AR07 §5 remain

- AR00 merged to master via PR #23 (`bff22d9`). Review verdict: layering is sound, no redesign. Problems are at boundaries. Summary + findings F1–F9: [`docs/refactoring/ARCH-REVIEW-SUMMARY.md`](docs/refactoring/ARCH-REVIEW-SUMMARY.md). Per-task plans: `docs/refactoring/arch-review/AR0x-*.md` (read only the one you work on).
- AR07 §1-4 (this branch): restored ADR evidence (`docs/archive/evidence/`), fixed remaining broken links, added `tools/check-doc-links.ps1` gate (wired into `verify-all.ps1`), fixed `docs-budget.ps1` T0 match for `docs/INDEX.md`. §5 triage waits on Q-AR5.
- **Waiting on user:** Q-AR1 (ship TurboJpeg?), Q-AR2 (accept ADR 0005), Q-AR3 (single composition root, supersedes ST06 fields), Q-AR4 (release path), Q-AR5 (group triage). See `docs/refactoring/OPEN-DECISIONS.md`.
- Next (no decision needed): AR03, AR06 prep. After Q-AR1: AR01. After Q-AR3: AR02a→b→c→e→d. After Q-AR2 + AR02e: AR04.

## Previous: nav hot-path perf pass — DONE (#20 `94c5aeb..20300b4`, fix #21 `9161c35`)

Details: `docs/archive/progress-log-2026-09.md` (2026-09-23 entry). Still deferred, **not fixed**:
- `ConfigureAwait(false)` in App / catalog mutated off UI thread → now **AR04** (ADR 0005).
- RAM-budget accuracy, disk-cache value, `SourceBytesCache` + preview budget > physical RAM, lazy EXIF/TurboJpeg transform → need real-folder numbers; use the **AR02e** baseline (production graph), not older `--perf-session` numbers (those ran without preload — finding F2).
- `GetOriginalDimensionsAsync` key reuse: implemented then reverted (race trade-off), keep reverted.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00 merged (#23); 🔄 AR07 §1-4 on branch | 5 decisions pending; AR01-AR06 + AR07 §5 TODO. |
| ST | ✅ (ST08/09 wait OC14) | ST06 fields to be replaced by AR02d if Q-AR3 = yes. |
| TS | ✅ TS00-04 | TS05-10 remain. |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | 🔄 | TC04, TC09 open; TC06/07 location → AR07 triage. |
| **OC** | 🔄 ~65% | OC14 blocks ST08/09, WD03-06, OC15-18 (not WD01 if Q-AR2 = yes). |
| **WD** | 🔄 | WD01 → AR04; WD02 low-risk part → AR03c. |
| **IO** | 🔄 | Blocked on IO01 contract. |
| **D** | 🔄 | Re-evaluate from AR02e baseline (AR07 triage). |
| **DT** | 🔄 | DT00-03, 08, 09 done; DT04-07, 10 open. |

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01 (→ replaced by ADR 0005 rule once AR04 is DONE)
- ❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02
- ❌ No OS SendInput/SetForegroundWindow in test harnesses
- ❌ Do not compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [AR summary](docs/refactoring/ARCH-REVIEW-SUMMARY.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [Structure status](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md) · [T89 summary](docs/refactoring/T89-FIT-SUMMARY.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
tools/verify-all.ps1
```
