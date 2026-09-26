# Progress Log — September 2026

Historical commits, validation details, and detailed task status from 2026-09-20/21 review round archived here.

## Task validation log (2026-09-20/21)

### DF00-DF07: Double-Click-to-Fit
- **Status:** DONE (PR #14 merged to master)
- **Commits:** see git history, branch `feature/Fit-Layout-Status` (now merged)
- **Validation:** DF00 (gesture detection), DF01-02 (VM layer), DF03-07 (performance/stability) all verified in real-build

### ST01-ST12: Structure Optimize
- **Completed:** ST01-ST07, ST10-ST12 merged
- **Remaining:** ST08, ST09 (blocked on OC14 — Undo unification)
- **Details:** [STRUCTURE-OPTIMIZE-STATUS.md](../refactoring/STRUCTURE-OPTIMIZE-STATUS.md)
- **Decisions archived:** [STRUCTURE-DECISIONS-Q-ST1-ST4.md](./STRUCTURE-DECISIONS-Q-ST1-ST4.md)

### TS00-TS04: Test Speed (Gate Fix)
- **Status:** DONE (gate no longer hangs)
- **Target:** 23s (reached)
- **Commits:** 4 commits on `codex/test-speed-plan`
- **Remaining:** TS05-TS10 (profiling/optimization work)

### TC00: Test Cleanup Baseline
- **Baseline:** [TC00-BASELINE-REPORT.md](historical/TC00-BASELINE-REPORT.md) — 4 PASS with 0 flakes, established 2026-09-21
- **Status:** TC01-TC11 not started; awaiting TS10 re-audit

### Code Review Sessions
- **Merged:** [MERGED-CODE-REVIEW-PLAN.md](./archive/MERGED-CODE-REVIEW-PLAN.md), [MERGED-CODE-REVIEW-TASKS.md](./archive/MERGED-CODE-REVIEW-TASKS.md)
- **Details:** See `docs/refactoring/archive/` for full session reports

## Current blockers (as of 2026-09-22)

1. **OC14** (Undo unification) gates ST08/ST09, all WD tasks, OC15-18
2. **T89** GUI/STA acceptance — implemented on `feature/Fit-Layout-Status`, not merged to `master`
3. **IO01/IO02** journal contract gates remaining I/O durability work
4. **D06-D12** perf diagnosis needs Procmon/ETW session data

## Process constraints maintained

- Branch → PR → review → merge (no direct master commits)
- Keep `ApplyFitViewAsync` multi-pass until T89 evidence exists
- No broad `Dispatcher.Invoke` or `GetRequiredService` conversions before WD01
- No journal durability reduction before IO01/IO02 evidence
- No generic `IAsyncFileSystem` before proper contract proof

---

<!-- Moved from task_on_progress.md on 2026-09-23 (AR00, T0 budget) -->
## 2026-09-23 — Done: navigation hot-path perf pass (merged via #20, #21)

Requested as a general "analyze and optimize" pass; not tied to an existing task ID. Findings and
a batch plan (4 batches) were proposed first, then batches 1–2 and part of 3/4 were implemented,
merged as PR #20. A post-merge self-review then found and fixed two real bugs introduced by #20
(case-sensitive compare-pair grouping; an unsynchronized cache race in `ImagePresenter`), merged
as PR #21. Both are on `master` now.

**Done (commits 94c5aeb, a654012, 1f982fe, 20300b4 → PR #20; 9161c35 → PR #21):**
- `ImagePresenter`: preview decode now starts before the thumbnail await instead of after
  (Preview loading mode), so the two run concurrently.
- `ReviewCatalog`: O(1) `IndexOf`/`Find` via a lazily-rebuilt path→index dictionary (was O(n)
  per call); `PathAt(index)` avoids allocating the whole `Paths` array just to read one entry;
  new `StructuralVersion` counter (bumped on membership/order change) lets other layers cheaply
  detect "nothing changed" instead of diffing a rebuilt collection every call.
- `ComparePairService.BuildIndex`: O(n log n) once instead of `Find`'s O(n log n) *every*
  navigation; `ImagePresenter` caches it against `ReviewCatalog.StructuralVersion`. `Find` itself
  is untouched (still has its own unit tests); `BuildIndex` has new parity tests asserting it
  agrees with `Find` for every path in a mixed catalog.
- `SourceSizeTracker`: caches against `StructuralVersion` instead of rebuilding a `HashSet` of
  every path on every call.
- `PreviewImageService`: caches one decoder instance per backend (was constructing a fresh
  `FallbackImageDecoder` + primary/fallback, including TurboJpeg's `Activator.CreateInstance`,
  on every single decode — decoders are stateless, confirmed by inspection); added
  `HasInflightPreview(ImageCacheKey)` to avoid a redundant stat.
- `WpfBitmapImageDecoder` / `WicDirectDecoder`: decode from a `SourceBytesCache` buffer via a
  non-owning `MemoryStream` (`ReadOnlyMemoryStreamFactory`) instead of `ToArray()`-copying it.
- `PreloadScheduler`: takes `CatalogEntry[]` (Length/LastWriteUtc already known from the folder
  scan) instead of `string[]`; builds the `ImageCacheKey` once per candidate via
  `IPreloadTarget.GetCurrentCacheKey(entry)`, cutting 2 of the 3 stats per candidate examined
  during a scan (the post-decode key rebuild still re-stats — the identity actually cached is
  only known after decode). Progress/paused-for-memory logging no longer takes a
  `GlobalMemoryStatusEx` syscall when `ILog.Enabled` (and, for the paused branch,
  `PhotoReviewPerf.Log.IsEnabled()`) are both false.
- `IFileSystem.EnumerateFilesWithStat`: default-interface method (`EnumerateFiles` +
  `GetFileStat` per item — unchanged behavior for every existing implementer);
  `PhysicalFileSystem` overrides it with `DirectoryInfo.EnumerateFiles()`, which already carries
  Length/LastWriteTimeUtc from the same directory-listing syscall. `FolderLoadCoordinator`'s
  folder scan uses it instead of a separate `GetFileStat()` per image file.
- Verified after every batch: `dotnet build PhotoReview.slnx -c Release` (0 warnings/errors) and
  `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` (829/829 passed, 7
  skipped, same skips as before this work).

**Deliberately deferred (do not assume these are fixed):**
- `GetOriginalDimensionsAsync` reusing the current navigation's cache key instead of a fresh
  stat: implemented, then **reverted** — it changes which of two pre-existing narrow races (file
  vanishes/changes mid-navigation) hits the graceful-removal path vs. a generic error status.
  Not worth the risk for one stat saved per navigation (non-Original loading mode only).
- Batch 3 (`ImagePresenter`/`MainViewModel` `ConfigureAwait(false)` removal to cut the number of
  `Dispatcher.Invoke` round-trips `WpfPresentationSink` does per present): **not attempted.**
  This is a real, currently-present issue — e.g. `ImagePresenter.RemoveMissingCatalogItemAsync`
  calls `ReviewCatalog.Remove`/`UpdateMetadata` from a background-thread continuation, even
  though `ReviewCatalog`'s own doc comment says UI-thread-only — but it's pre-existing (not
  introduced by this session), and per this file's own Critical Process Rules, broad
  `Dispatcher.Invoke`-adjacent threading changes are gated behind **WD01**, which needs real
  GUI/STA acceptance verification this sandbox can't do headlessly. Flagging for whoever picks
  up WD01, not fixing ad hoc here.
- RAM-budget accuracy (`RamBudgetPolicy.ShouldPreloadWholeFolder`'s flat ×10 JPEG-expansion
  factor not accounting for downscaled preview target width), disk-cache effectiveness (whether
  PNG-encoding preview-sized downscales actually saves I/O vs. re-decoding the source JPEG with
  DCT scaling), and `SourceBytesCache`/`PreviewImageService` RAM budgets summing to more than
  physical RAM when both are enabled: **not attempted.** These need before/after numbers from
  `PhotoReview.Benchmark.Cli` against a real photo folder, which this sandbox doesn't have: a
  wrong constant here risks *causing* memory pressure, not just missing a speedup.
- EXIF-orientation / TurboJpeg fine-scale lazy `TransformedBitmap` (render-thread cost on first
  frame): not attempted, same "needs real measurement" reasoning.

**Fixed post-merge (PR #21, commit 9161c35):**
- `ComparePairService.BuildIndex` grouped candidates by `(Folder, Extension)` with the default
  (ordinal, case-**sensitive**) tuple comparer, while `Find` — the function it replaces on the
  hot path — compares both with `OrdinalIgnoreCase`. Silently broke pairing for files whose
  extension case differs (e.g. `DSC0001.JPG` / `DSC0001 (1).jpg`) or whose folder path differs
  only by case. Fixed with an explicit `OrdinalIgnoreCase` comparer; two regression tests added.
- `ImagePresenter._compareIndex`/`_compareIndexVersion` had no lock, but `PresentAsync` bodies
  for overlapping navigations run concurrently (async-void key handlers don't serialize a
  held-down arrow key). Two concurrent calls could race to rebuild the index redundantly.
  Wrapped in a lock.

**Next step (nothing further planned unless requested):** the deferred items below are real gaps,
not bugs in what shipped. Picking any of them up needs either GUI/STA acceptance (WD01, for the
`ConfigureAwait` item) or `PhotoReview.Benchmark.Cli` numbers from a real photo folder (for the
RAM-budget/disk-cache items) — this sandbox can do neither, so they weren't attempted rather than
guessed at.

## 2026-09-26 (late)

Handoff bullets moved here from `task_on_progress.md` when #109–#112 merged (verbatim):

- **Benchmark speed-up — merged (#109):** `run-matrix.ps1` one warm-up per (fixture, mode, condition) per batch (`-WarmupEveryCell` = old behaviour), `-Profile quick` uses new `s2-next-slow-quick` (40 keys); `BenchmarkWindow` "Quick check" button (fast-sequential, 10 iterations), image limit (default 64, 0 = all), non-blocking cache teardown. F4: gate 393 → 260 s (−34 %), quick 209 → 158 s (−24 %), in-app quick check 2.6 s vs default 8.2 s. **User GUI check:** Quick check button, image-limit box, close the window mid-run (no crash, no leftover `%TEMP%\PhotoReview-Benchmark-Cache\*`).
- **Architecture review 2026-09-26 — merged (#106):** [summary](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md). Four agent branches merged without conflicts: AR11 (static `AppSettings` persistence removed, Settings round-trip test, Core `File.*` boundary test), AR12 (+AR17a, AR18a: `.gitignore` native rule, Release warnings-as-errors, GC note, TurboJpeg "experimental" label), AR15 (dead overloads, one atomic cache writer, `SourceBytesCache` reads on the calling thread), AR13 (+AR19a, AR14a: `PointerInputController`, `FitViewController` — Fit loop moved verbatim, still ≤ 3 passes; `MainWindow.xaml.cs` 715 → 404 lines; `ShowSkippedFiles` via `IDialogService`). Decisions Q-AR6/8/9/10 = recommended options (user said "run everything"); Q-AR7 = (c) (user, "chọn c"), see below.
- **Measured 2026-09-26 (PERF-STATUS):** AR15c not worse with the source-bytes cache on (new CLI flag `--source-bytes-cache`); AR16 probe ≈ 114 ms per folder open on F4 (≈ 68 % of the old 166 ms first visual) → **Q-AR7 decided (c)** by the user; merged in #107 (below).
- **AR16 / Q-AR7 (c) — merged (#107):** folder open lists without the per-file open probe and presents the first image; the probe runs in the background, is applied after the Explorer order settles, removes unreadable files (current kept, or advanced like Delete) and still reports them (ADR 0007). GUI check for the user: folder with a locked file → first image at once, then the "skipped" warning.
- **Still to do:** **user GUI check for AR13**: pan + glide, wheel zoom at cursor, Ctrl+wheel navigation, click-to-zoom, Fit after resize and DPI change, skipped-files list opens.
- **User (GUI / real machine), still open from the overnight review:** Settings > General > Updates, Defaults button, close during a folder scan, Recycle Bin undo on a non-English Windows, decoder fallbacks with damaged EXIF/ICC; perf runs for natural sort / snapshot validator and Q-R17 on F4.
- **Test-host stability — merged (#112):** the one-off `JournalConcurrencyTests` crash was an `IOException` (append retry budget exhausted on a loaded machine) escaping a raw `Thread`, which kills the whole test host; thread failures are now captured and rethrown on the test thread, and the test only asserts acknowledged commits. Product code already handles this (action reports a journal error; startup reconcile catches). FA-01 itself is covered deterministically by `JournalReconcileRaceTests`.
- **Slow MainWindow tests fixed (#110):** the 5 failures on master were stale tests, not app bugs — actions were installed by mutating `_settings` (the `ShortcutRouter` only rebuilds on `SettingsStore.Changed`; now via `SettingsStore.Save`), the fake Explorer provider was registered as an instance (DI never disposes those; now a factory), and INV-5 still expected no Undo for a late Move (APP-03/Q-R25 B registers it). New test: a late *failed* Move is not restored into the new folder. 14/14 pass ×3.
- **Source audit 2026-09-26:** all five findings closed — F2 by design (#101), F3 (#101, `b547ce8`), F0/F1/F4 (#103); verdicts in [the audit](docs/refactoring/REVIEW-2026-09-26-FULL-SOURCE-AUDIT.md#follow-up-claude-2026-09-26).
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Decision log:** handoff and decisions live on `master` only (no `develop` since #104).
