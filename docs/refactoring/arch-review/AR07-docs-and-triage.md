# AR07 — Docs repair and task-group triage

**Finding:** F9 · **Decision:** Q-AR5 (which groups to keep) · **Branch:** `docs/ar07-docs-repair` · **Size:** ~0.5 day (half with the user) · **GUI:** no

AR00 (branch `docs/arch-review-plan`) already fixed: T89 "not on master" (PR #15 merged `10ff31f`), DT status, T0 budget (task_on_progress compressed), `architecture.md` TurboJpeg arrow, AR entries in INDEX/ACTIVE-TASKS/OPEN-DECISIONS. AR07 does the rest.

## 1. Broken links (17 at `fbdf48e`, archive excluded; 12 fixed in AR00, 5 left = ADR evidence below)

| File | Broken target | Fix |
|---|---|---|
| `README.md` | `docs/refactoring/results/final.md` | restore (step 2) |
| `docs/adr/0001-image-decoder.md` (2×) | `results/decoder-bench.md` | restore |
| `docs/adr/0002-ui-framework.md` | `refactoring/diagnosis/REPORT.md`, `results/final.md` | restore |
| `docs/ACTIVE-TASKS.md` | `refactoring/{OPTIMIZE-CLEAN,T89-FIT-LAYOUT,TEST-CLEANUP,TEST-SPEED}-PLAN*.md` | → `archive/historical/…` (done in AR00) |
| `docs/refactoring/OPTIMIZE-CLEAN-SUMMARY.md`, `REFACTOR-STATUS.md`, `STRUCTURE-OPTIMIZE-STATUS.md` (2), `T89-FIT-SUMMARY.md`, `TEST-CLEANUP-SUMMARY.md`, `TEST-SPEED-SUMMARY.md` | `*-PLAN*.md` in same folder | → `../archive/historical/…` (done in AR00) |
| `task_on_progress.md` | `docs/refactoring/{T89-FIT-LAYOUT,TEST-SPEED}-PLAN*.md` | fixed in AR00 |

## 2. Restore ADR evidence
`docs/refactoring/results/final.md`, `results/decoder-bench.md`, `diagnosis/REPORT.md` were deleted in `4afee69` ("phase 1 cleanup"). ADRs must keep their evidence:
```bash
# Git Bash (Windows PowerShell 5 '>' would write UTF-16)
mkdir -p docs/archive/evidence
git show 4afee69^:docs/refactoring/results/final.md          > docs/archive/evidence/T66-final.md
git show 4afee69^:docs/refactoring/results/decoder-bench.md  > docs/archive/evidence/decoder-bench.md
git show 4afee69^:docs/refactoring/diagnosis/REPORT.md       > docs/archive/evidence/D-diagnosis-REPORT.md
```
Update the links in README, ADR 0001, ADR 0002. While there, record in `docs/archive/evidence/T66-final.md` header **which harness produced the S-scenario numbers** (BenchmarkEngine vs `--perf-session`); if `--perf-session`, add "legacy graph, no preload — see AR02 F2".

## 3. Link check as a gate
Add `tools/check-doc-links.ps1` (same logic as the Python scan used for this plan: markdown links and back-ticked `docs/…/*.md` paths, archive excluded, resolve relative to file, repo root and `docs/`) and call it from `verify-all.ps1` next to `docs-budget.ps1`.

## 4. Fix `docs-budget.ps1`
`Get-DocTier` strips only `docs/refactoring/`, so `docs/INDEX.md` never matches the T0 regex and is counted as "Other" although AGENTS.md lists it as T0. Change the T0 match to `'^(AGENTS|task_on_progress|docs/INDEX)\.md$'` on the repo-relative path, then re-evaluate the 12 KB budget (AGENTS 5.7 KB + task_on_progress + INDEX ≈ 3.6 KB). If over: shorten INDEX (drop the size column — it goes stale; `docs-budget.ps1` reports sizes).

## 5. Triage (with the user, Q-AR5)
For each group decide **keep (owner + next step)** or **close (move plan to `docs/archive/historical/`, one line in ACTIVE-TASKS)**. Proposed defaults:

| Group | Proposal | Reason |
|---|---|---|
| T89 | Keep — only GUI acceptance left; code on master; DF02 Fit tests skipped in `9f880d1` must be un-skipped or deleted | concrete |
| OC14 | Keep — but re-scope to "Undo gate location"; WD01 no longer waits for it (AR04) | unblocks |
| WD02–WD06 | Close WD02 (AR03c covers the only real case); keep WD03–06 only if a dialog bug exists | no user-facing issue known |
| IO01–IO07 | Keep IO01 (contract) only; close IO03–IO07 until IO01 exists | speculative |
| D01–D12 | Close; re-open from AR02e baseline if a bottleneck shows | numbers were from the legacy graph |
| TC04, TC09 | Keep (TC09 = the known flaky journal test) | concrete |
| TC06/TC07 location | Decide: keep in `Integration.Tests` (they need real Recycle Bin/real photos) — document instead of moving | cheap |
| TS05–TS10 | Keep TS05, TS06; close TS08/TS09 (CI filter already aligned, see `ci.yml` note) | done in practice |
| DT04–DT10 | Close except DT10 final measurement | diminishing returns |

## 6. Acceptance
0 broken links (gate green); ADR evidence reachable; `docs-budget.ps1 -Check` counts INDEX and passes; ACTIVE-TASKS lists only groups with an owner or an explicit "closed" line.
