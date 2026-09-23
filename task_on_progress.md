# Current Work — PhotoReview

**Updated:** 2026-09-23 | **Branch:** `master` (this session's work is on `perf/nav-hot-path-optimizations`, not yet merged) | **Last merge:** #18 (codex/cq-wave1-warnings — all warnings reduced 634→0)

## In-progress: navigation hot-path perf pass (`perf/nav-hot-path-optimizations`)

Requested as a general "analyze and optimize" pass; not tied to an existing task ID. Findings and
a batch plan (4 batches) were proposed first, then batches 1–2 and part of 3/4 were implemented
in one session (three commits). **Not yet reviewed or merged** — open a PR and get it reviewed
before treating any of this as done.

**Done (commits 94c5aeb, a654012, 1f982fe on `perf/nav-hot-path-optimizations`):**
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

**Next step:** open a PR from `perf/nav-hot-path-optimizations`, get it reviewed, and — if the
deferred RAM-budget/disk-cache items are wanted — run `PhotoReview.Benchmark.Cli` before/after on
a real folder first.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| ST | ✅ Mostly done (ST08/09 blocked) | ST01-07, ST10-12 merged. Blocked on OC14. |
| TS | ✅ TS00-04 done | Gate ~23s (no hang). TS05-10 remain. TS10: re-audit before TC. |
| DF | ✅ Done | PR #14 merged. |
| **CQ** | ✅ DONE | 634→0 warnings (PR #18 merged). |
| **T89** | 🔄 IN PROGRESS | GUI/STA acceptance on `feature/Fit-Layout-Status`. Not on `master` yet. |
| **TC** | 🔄 TODO | Blocked on TS10 re-audit. 23 plans per Q-T1..Q-T4. |
| **OC** | 🔄 ~65% done | OC14 (Undo) blocks ST08/09, WD, OC15-18. |
| **WD** | 🔄 TODO | Blocked on OC14. |
| **IO** | 🔄 TODO | Blocked on IO01/02 contract. |
| **D** | 🔄 Mixed | D06 Procmon data needed for D01/02/08/09. |
| **DT** | 🔄 TODO | Docs token diet: DT00 baseline taken, DT01+ in progress. |

## Key Blockers

1. **OC14** (Undo unification) — critical path for ST08/09, WD, OC15-18
2. **T89** GUI acceptance — needed before merging Fit to `master`
3. **IO01/02** journal contract — gates I/O durability work
4. **TS10** audit — required before trusting TC status

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01
- ❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02
- ❌ No OS SendInput/SetForegroundWindow in test harnesses

## Key Links

- **Master plan:** [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md)
- **Structure status:** [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md)
- **Test plan:** [`docs/refactoring/TEST-SPEED-PLAN-2026-09-20.md`](docs/refactoring/TEST-SPEED-PLAN-2026-09-20.md)
- **Fit layout:** [`docs/refactoring/T89-FIT-LAYOUT-PLAN.md`](docs/refactoring/T89-FIT-LAYOUT-PLAN.md)
- **Docs diet:** [`docs/INDEX.md`](docs/INDEX.md)
- **History:** [`docs/archive/progress-log-2026-09.md`](docs/archive/progress-log-2026-09.md)

## Documentation Diet Status (DT series)

| Task | Status | Impact |
|------|--------|--------|
| DT00 | ✅ DONE | Measurement baseline (docs-budget.ps1) |
| DT01 | ✅ DONE | Entry points: T0 = 7.7 KB (40% reduction) |
| DT02 | ✅ DONE | Status digests: 24 KB total |
| DT03 | ✅ DONE | Compress plans: 4 summaries (20 KB active vs 101 KB archived) |
| DT08 | ✅ DONE | .ignore for archive (ripgrep filtering) |
| DT09 | ✅ DONE | Budget gate in verify-all.ps1 |
| DT04, DT05, DT06, DT07, DT10 | 🔄 TODO | Code map, comment cleanup, test boilerplate, final measurement |

**Cold-start (T0 + typical T1 file):** ~17.7 KB (~6k tokens) — 75% reduction from baseline.

## Quick Checks

```powershell
# Budget check
tools/docs-budget.ps1 -Check

# Test gate  
dotnet test PhotoReview.sln --filter Category=Gate

# Build Release
dotnet build -c Release PhotoReview.sln

# Full verification
tools/verify-all.ps1
```
