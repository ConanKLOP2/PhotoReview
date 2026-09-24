# Review 2026-09-25 — Docs, tests, repo hygiene (summary of 3 sub-reviews)

## Docs (DOC)
- DOC-01 Critical — task_on_progress.md:3-11, docs/ACTIVE-TASKS.md:3-4,20-22,129,151-153 say PR batch #64–#70 "open"; all #64–#71 are merged (master cbd24b8). Violates AGENTS rule #1. Fix: move to "Previous (merged)", refresh Next. S
- DOC-02 High — STRUCTURE-OPTIMIZE-STATUS.md:29-30, REFACTOR-STATUS.md:11, OPTIMIZE-CLEAN-SUMMARY.md:180,200-210, ACTIVE-TASKS.md:20,129-130 still say ST08/09 & OC15-18 blocked by OC14; OC14 merged (#64, FileActionGate). Branch refactor/st08-oc18 in progress. Fix: mark unblocked / in progress. S
- DOC-03 High — TEST-CLEANUP-SUMMARY.md:307 says TC06/TC07 in Integration.Tests; they are in tests/PhotoReview.App.Tests/HotPath (commit 440527f). S
- DOC-04 Medium — ADR 0007 header still "to be implemented after i18n"; IO03 #65, IO04/05 #66 merged. Add implemented-by addendum. S
- DOC-05 Medium — No ADR for zoom decision Q-Z1 (100 % = source pixel, decode to viewport box, on-demand original; #43/#47). Write ADR 0008, link from architecture.md Zoom + OPEN-DECISIONS. M
- DOC-06 Medium — ADR 0005 rule 2 (Core/Imaging/Platform always ConfigureAwait(false)) not test-enforced; known violation UndoService.cs:185,227 (CORE-01). Document + add arch test. S
- DOC-07 Low — T0 = 12,176 B of 12,288 budget (~1 % headroom). Trim to ≤ 11 KB. S
- DOC-08 Low — TC00-BASELINE-REPORT.md stale snapshot in T1; archive or mark superseded. S
- DOC-09 Medium — OPTIMIZE-CLEAN-SUMMARY.md OC14 row self-contradictory ("PARTIAL, critical blocker") vs OPEN-DECISIONS Q-OC14 ✅. S
- DOC-10 Medium — Group status duplicated in 5 files (task_on_progress, ACTIVE-TASKS, REFACTOR-STATUS, OPTIMIZE-CLEAN-SUMMARY, STRUCTURE-OPTIMIZE-STATUS) → recurring staleness. Make ACTIVE-TASKS the single source; others link. M
- DOC-11 Low — No RELEASING.md / CONTRIBUTING.md / user guide (versioning only in AGENTS.md). M
- DOC-12 Low — CQ-WARNINGS-PLAN.md (done, PR #18) never compressed per token-diet rule. S
- DOC-13 Low — OPEN-DECISIONS.md:33 "Most critical blocker: OC14" is false now. S

## Tests (TEST)
Coverage of new features is good (Recovery check, zoom detail, Explorer order, burst preload, disk cache v5 all have unit + integration tests).
- TEST-01 High — tests/PhotoReview.App.Tests/HotPath/RealPhotosManualTests.cs:47-56 sets PHOTOREVIEW_DATA_ROOT without [Collection("GlobalState")] and never restores it → cross-test leak. Add collection + save/restore. S
- TEST-02 Medium — CI filter (ci.yml) excludes Category=Integration everywhere ⇒ PhotoReview.Integration.Tests effectively never runs in CI (Explorer order, Recovery window, zoom detail integration). Add a CI job for Integration (excl. Manual/Native) or document the mandatory local gate. M
- TEST-03 Medium — NativeRecycleBinTests use the real Recycle Bin via Shell COM; a hard kill skips DisposeAsync → orphans. Add a sweep of TC06_RecycleBin_* orphans at test-run start (own items only). M
- TEST-04 Medium — MainWindowBehaviorTests.Explorer.cs:29-37,82,137,148,206 "NeverWindow = 1 s" negative asserts by wall clock (false negatives under load). Replace with a settled/generation signal. M
- TEST-05 Low — Imaging BurstPreloadTests.cs:284 Task.Delay(100) to prove absence; use deterministic gate. S
- TEST-06 Medium — No integration test for Settings window journal-durability control → AppSettings.JournalDurability → FileActionService mode. M
- TEST-07 Low — Category "Stress" referenced in AGENTS.md, verify-all.ps1:35, ci.yml but no test uses it; remove or restore. S
- TEST-08 Low — (same as DOC-01) stale status docs.
- TEST-09 Medium — three GlobalStateCollection definitions; add an Architecture rule: test classes calling Environment.SetEnvironmentVariable must be in [Collection("GlobalState")]. M
- (from earlier session) benchmark `action-delete` profile (BenchmarkWorkloadRunner) leaves ~2 items/run in the real Recycle Bin.

## Repo hygiene (HYG)
- HYG-01 Low — Architecture.Tests.csproj:8 redundant NoWarn CA1707 (already in .editorconfig). S
- HYG-02 Low — TestSupport(.Windows).csproj lack <Platforms>AnyCPU;x64</Platforms>. S
- HYG-03 Low — PhotoReview.slnx folders inconsistent (/src/ empty; only one test project under /tests/; Benchmark.Cli ungrouped). S
- HYG-04 Medium — work/ accumulates logs/reports with no retention (gitignored). Add tools/clean-work.ps1 or doc note. S
- HYG-05 Low — .gitignore `*.log` + single `!` exception for tools/diag/samples/applog-sample.log without comment. S
- HYG-06 Low — outputs/ mixes tracked install scripts/example config with ignored outputs/release/; add README or move to tools/ or deploy/. S
- HYG-07 Low — `/Claude outputs/` ignore entry for a stray folder; remove once confirmed gone. S
- HYG-08 Low — ci.yml repeats the same test filter 5× (plus verify-all.ps1); use env var. S
- HYG-09 Low — no .gitattributes (eol=crlf, binary samples). S
- HYG-10 Low — Directory.Packages.props: Microsoft.CodeAnalysis.CSharp must not exceed SDK compiler — only a comment guards it; add check or doc. M
