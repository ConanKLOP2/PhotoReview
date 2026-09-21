# PhotoReview — Current Status

- **Updated:** 2026-09-21, fixed the PerfAnalysis namespace mismatch reported by CI.
- **Verified Checkout:** `master`, `ba1317e54702e3af8bd32c0bf5d953b3766f1ba0` (PR #9 merged); working tree was clean before review.
- **Session Objective:** Repo-wide review and execution of the first three P1 packages in the optimize/clean plan. Also translated root documentation files to 100% English.
- **Plan:** `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`; main correctness and benchmark semantics implemented; T89 GUI/STA and native Recycle Bin live acceptance pending evidence.

## Recent Review / Validation

- Reviewed Core/Platform.Windows, Imaging/TurboJpeg, App/coordinators/UI, Benchmarking/CLI/scripts/CI and related tests using 3 agents + coordinator.
- **Release build + xUnit:** `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` exit 0; 745 PASS (7 Architecture + 305 Core + 203 Imaging + 59 Integration + 171 App), 0 fail/skip. Some analyzer warnings exist.
- **Runtime probes:** DuplicateFinder selected 2/2 duplicates with identical hash in both all-original and all-numbered groups; selection only, no recycle/delete. PerfAnalyze merged two 0/8 worker runs into 1 group; R-CONT reported missing data. Details/temporary artifacts in plan.
- **Source findings requiring regression testing:** native buffer sizing unchecked; preload exit/restart drain not guaranteed; batch dialog following ConfigureAwait(false); WPF catch-all retry; folder remap O(n²)/re-stat; display state; cache invalidation/quota; Undo tail-limit; benchmark semantics.
- **Unverified runtime behaviors:** race Session/journal failures, native recycle identity, GUI T89. Do not treat static concerns as confirmed runtime defects.
- `tools/verify-all.ps1` PASS after API/lifecycle/culture wave: 7 Architecture + 314 Core + 205 Imaging + 62 Integration + 174 App = 762/762; smoke, fault-injection, publish, and verify-release all PASS.
- UI/clean-code review recorded as OC14–OC18 in `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`; **not yet implemented**: Undo entry-point unification, pan threshold short-circuit, `_files.Capacity`, pattern cleanup, and Fit one-pass.
- `WpfDialogService` review recorded as WD01–WD06 in the same plan; **not yet implemented**: UI-thread dispatch, DI refactor, MessageBox helper, and BenchmarkWindow lifecycle change. WD01 requires a call graph audit first.
- I/O durability/performance review recorded as IO01–IO07 in the same plan; **not yet implemented**: removing `WriteThrough`/`Flush(true)`, async filesystem migration, `IgnoreInaccessible`, or buffer/temp-name optimization. IO01/IO02 require locked-in contract and benchmark baseline first.
- Software structure review recorded as ST00–ST12 in `docs/refactoring/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md` and `STRUCTURE-OPTIMIZE-TASKS.md`; **not yet implemented**, not rebuilt/tested in this round. Key points: `MainViewModel` has 21 parameters/13 optional; `App.xaml.cs` contains VM factory and `GetTotalSourceBytes` still re-stats entire folder (latter half of F06, divergent from OC08 DONE); marker `IProgressiveExplorerOrderProvider` is redundant; `ThumbnailModels.cs` is dead code; infrastructure (`FileHashService`, `PerfCsvListener`, `PhysicalMemory`) resides in App; `PerfAnalyze*` is tightly coupled to the WPF project; `Benchmark.Cli` uses reflection in 26 places against `MainWindow`.
- Decisions needed before proceeding: Q-ST1 (PerfAnalysis project), Q-ST2 (move PerfCsvListener to Core), Q-ST3 (keep Cli→App dependency), Q-ST4 (Ctrl+Z semantics, part of OC14). ST04/ST05/ST08 are currently `BLOCKED` on these questions.
- Test cleanup plan prioritizing hot paths documented in `docs/refactoring/TEST-CLEANUP-PLAN-2026-09-20.md` (TC00–TC11); **not yet implemented, no runtime benchmarks run**. Main findings: `InterleavedFileActionSequenceTests`, `BenchmarkScenarioTests`, and `FileActionConcurrencyTests.InterleavedSequence…` simulate Next/Move/Delete on `List<string>` + `File.Move` rather than invoking production code; no tests cover continuous Next/Move/Delete sequences or sequential disk read invariants; test images are only 1×1 PNG/text; source read path `new FileStream` bypasses `IFileSystem`, making read count dependent on self-reported metrics; `IsBusy` gate silently ignores second action. Decisions needed for Q-T1..Q-T4 (drop/queue keys when busy, read-count seam, real photo directory via env var, allow real Recycle Bin) — TC06/TC07 are currently `BLOCKED`.
- Codebase token reduction plan: `docs/refactoring/DOCS-TOKEN-DIET-PLAN-2026-09-20.md` (DT00–DT10), **not yet implemented**. Measurements: docs 564 KB (~190k estimated tokens), 5 historical/planning files take up ~59%; cold start per current guidelines ≈ 126 KB. Decisions needed for Q-D1..Q-D4 (edit `AGENTS.md`, `git mv` to `docs/archive/`, `CLAUDE.md`, `.ignore`).

## Decisions & Remaining Work

1. UI dispatcher, file outcome/journal durability, SessionWriter ordering, Recovery retry, native candidate identity, benchmark action/report semantics, API token ordering, disposal lifecycle, culture formatting, and focused clean-code warnings are implemented; T89 layout/GUI acceptance, live Recycle Bin acceptance, and real-image optimization measurements remain.
2. T89 wheel/anchor/pan and transaction Fit merged via PR #9; `docs/refactoring/T89-FIT-LAYOUT-PLAN.md` remains **IN PROGRESS**, with pending work on unifying initial-mode viewport, STA layout, and GUI Fit → wheel → drag on vertical/horizontal images. Do not consider DONE based on pure math tests alone.
3. Keep WPF, DecoderBackend=Wpf, ScalingQuality=HighQuality; SourceBytesCache off by default. Only change according to approved measurements/plans; do not force RAM usage to cause paging/OOM.
4. T73 still requires Recovery retry/corrupt image coverage if fixtures are present; T74 previous release was marked DONE in historical records, not a new release in this review cycle.
5. Post-implementation process: `./tools/verify-all.ps1`, publish to `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, feature branch/push origin/PR to master per AGENTS. Do not commit directly to master.
6. Do not reduce `ApplyFitViewAsync` to a single pass based solely on source review; require STA/layout trace and GUI acceptance per OC18/T89.
7. For `WpfDialogService`, do not add `Dispatcher.Invoke` or convert all `GetService` calls to `GetRequiredService` before completing WD01 and locking down required/optional contracts.
8. For I/O, do not reduce journal durability, enable `IgnoreInaccessible`, or introduce wide-reaching `IAsyncFileSystem` before IO01/IO02 and supporting evidence.

## Master Task List

**→ [`docs/ACTIVE-TASKS-2026-09-20.md`](docs/ACTIVE-TASKS-2026-09-20.md)** — Complete consolidated list of all pending work (TC/OC/WD/IO/DT/T89/D tasks), blockers, and critical path.

---

## Next Steps

### ST10 — DONE (2026-09-20)
- **Fix build:** `HEAD` ST09 (`8c80f19`) did not compile (`DuplicateCleanupController.cs` was missing `using PhotoReview.Core.Model`); fixed. Baseline numbers recorded in commit `a65be80` (774) ran against stale binaries and are **invalid**.
- **True baseline (0 build errors):** 775 PASS / 1 FAIL = 776 (Architecture 13, Core 321+1 FAIL, Imaging 205, App 174, Integration 62). The single failure is `OperationJournalTests...IsBoundedAndFast` (assert < 100 ms): failed 2/2 when running the full solution in parallel (112, 166 ms), passed 8/8 when executed standalone. Not yet checked against `master`. Details: `docs/refactoring/TC00-BASELINE-REPORT.md`.
- **ST10(a) rule ST06 (new):** `Benchmark.Cli` does not use reflection into App types (`StructureOptimizeRulesTests`). Rule initially FAILED at exactly the 3 remaining spots after ST06: `MainWindow._placementRestored`, `MainWindow.Window_Closing`, `App.PerfDispatcherHooks` (lookup nested returned null from ST03 so CLI's `DispatcherLongOp` was silently disabled). Replaced with public `MainWindow.SuppressWindowPlacement()` and public `PerfDispatcherHooks`; rule PASS.
- **Real CLI test execution (synthetic fixtures, no user photos):** `--perf-session` PASS (5/5 keys, errors=0, logged "PerfDispatcherHooks attached"); `--benchmark-list-profiles` OK; `--ui-next-probe` FAIL "Next image did not reach RAM cache" **also on `master` `5dc5cda`** (`new MainWindow()` path uses `DummyPreloadController` so it does not preload): pre-existing issue, unaddressed, requires a dedicated task.
- **TC00 baseline complete:** test inventory, G1–G7 classification, timing profiles established. TC01–TC11 ready for implementation.

### ST11 — DONE (2026-09-20)
- **Consolidate documentation:** Created final summary and decision documents
  - `docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md` — consolidated ST01–ST12 status, decision summary, next phases
  - `docs/refactoring/STRUCTURE-DECISIONS-Q-ST1-ST4.md` — detailed rationale for all four ST architectural decisions
  - `task_on_progress.md` updated to reflect ST10 DONE, ST11 DONE, ST12 APPROVED
- **Status:** All ST tasks are either DONE or BLOCKED pending OC14 (Ctrl+Z semantics)
- **Next:** OC14–OC18 can proceed independently; TC01–TC11 ready for implementation

### CI namespace fix — DONE (2026-09-21)
- **Objective:** Fix CI compilation errors for `PhotoReview.PerfAnalysis` and `NavRecord` in the benchmark CLI and integration tests.
- **Change:** Updated the namespace declaration in all six files under `src/PhotoReview.PerfAnalysis/` from `PhotoReview.Benchmarking.PerfAnalysis` to `PhotoReview.PerfAnalysis`, matching existing project references and `using` directives.
- **Validation:** `dotnet build PhotoReview.slnx -c Release --no-restore` passed with 0 errors; focused `PerfAnalyzeTests` validation pending/recorded with this change.
- **Continuation:** Keep the project namespace and consuming `using` directives aligned if PerfAnalysis files are moved or regenerated.

**Decisions finalized (2026-09-20):**
- **Q-T1:** Queue keypresses when action is running (UX priority: don't silently drop). TC05 tests queue order and final state.
- **Q-T2:** Yes, add small seam to detect blind spots in source-read measurement. TC02.
- **Q-T3:** Yes if real-photo folder is available via env var; skip TC07 if not present on this machine.
- **Q-T4:** Yes if Recycle Bin is functioning normally. TC06 self-cleans via Undo, so safe.

### ST12 — APPROVED (2026-09-20)
- **Decision:** Do not split Presentation project. ADR `docs/adr/0004-presentation-project-separation.md` approved.
- **Reason:** Technically viable (18 types, ~2.3k lines, all public, no cycles), but **0 projects** can drop the App reference. Build-time benefit unmeasured; cost is non-zero. Accept Cli→App dependency under Q-ST3 instead.
- **No implementation required** — this task is completed as a design investigation only.

---

- Read AGENTS, new plans, and T89 plan; re-verify branch/SHA/clean tree before proceeding.
- Implementation commits: `4a81ba8` preload, `6a4ec01` decoder, `00cad65` duplicate, `3db9ae6` cache, `5edd15d` display, `b8b97fb` PerfAnalyze grouping, `295f04b` Undo history, `a16b327` folder/catalog, `fb5155f` benchmark profile propagation, `42b92e2` SessionWriter, `481c130` file outcome, `abecebc` UI dispatcher, `32156f5` recovery retry, `6dee023` benchmark action, `6ad5c15` benchmark report/manifest, `3a5c168` recycle candidate identity, `4b6b5ad` Core clean, `77c4441` Imaging clean, `8249fd2` App serializer clean, `e301271` API token ordering, `89019a1` disposal lifecycle, `6fadeb2` culture formatting. Plan docs and this file are updated following validation.
- Retain frames when Move/Delete is loading; no hidden retries. Avoid OS SendInput/SendKeys/SetForegroundWindow for test harnesses; use dedicated fixtures, do not alter user configurations/actions.
- Upcoming tasks: ST00 baseline → ST01 → ST02 (`MainViewModel`/`App.xaml.cs` single-owner: do not interleave with OC14/WD03); OC14 semantics + Undo tests; followed by OC15–OC17 clean-code wave; OC18 awaits T89 evidence before any Fit changes.
- Next dialog group: WD01 audit UI-thread/call graph; WD02 low-risk DRY refactor; WD03–WD05 only after DI/dispatcher/window shutdown contracts are proven.
- Next I/O group: IO01 lock down durability/completeness contract, IO02 benchmark baseline, IO05 reproduce permission; only then consider IO03/IO04/IO06.
