# AR — Architecture Review 2026-09-23 (Summary)

**Status:** PLANNED (AR00 done in branch `docs/arch-review-plan`) · **Tier:** T1 · **Base:** `master@fbdf48e`
**Detailed per-task plans:** `docs/refactoring/arch-review/ARxx-*.md` — read ONLY the one for the task you are doing.

## Verdict

The layering (Core → Imaging/Platform → App, Core free of WPF, DI, INV-1..12, NetArchTest rules) is sound. ADR 0002 (keep WPF) and ADR 0004 (no Presentation project) stand. **No redesign.** The problems are at *boundaries*: the release does not contain what the UI offers, tests/benchmarks run a different object graph than production, and the UI-thread rule is a comment rather than an enforced rule.

## Verified findings (all checked against source at `fbdf48e`)

| # | Finding | Evidence | Severity | Task |
|---|---|---|---|---|
| F1 | TurboJpeg is selectable in Settings but absent from the release; selection silently falls back to WPF | `PhotoReview.App.csproj` has no ref to `Imaging.TurboJpeg`; `ImageDecoderFactory.cs:56-60` uses `Type.GetType(string)`; publish dir has no `PhotoReview.Imaging.TurboJpeg.dll`/`turbojpeg.dll`; `verify-release.ps1:9` does not check them | P0 | AR01 |
| F2 | `--perf-session` and `--ui-next-probe` measure a **non-production** graph: no preload (`DummyPreloadController`), 64 MiB RAM cache, fixed 1920 px target, disk cache off, default backend only | `PerfSession.cs:210`, `LocalUiNextProbe.cs:17` call `new MainWindow{...}` → `MainWindowHelpers.CreateTestViewModel` (`:113-166`) | P0 | AR02 |
| F3 | Two composition roots diverge. Production passes viewport `(0,0)` to `ApplyInitialViewMode`; the test root passes the real viewport | `MainViewModelCompositionRoot.cs` sink `onApplyInitialViewMode: ... (…, 0, 0)` vs `MainWindowHelpers.cs:148` | P1 (Fit/T89 related) | AR02 |
| F4 | Preview/thumbnail disk caches ignore `IAppPaths` (and `PHOTOREVIEW_DATA_ROOT`) and always write to `%LOCALAPPDATA%\PhotoReview\cache` | `PreviewImageService.cs:88-89`, `ThumbnailCache.cs:53`; `IAppPaths.PreviewCacheDir/ThumbnailCacheDir` exist but are never passed in `App.ConfigureServices` | P1 | AR02a |
| F5 | `ReviewCatalog` is documented UI-thread-only, yet App code mutates it after `ConfigureAwait(false)` continuations, e.g. `FolderLoadCoordinator` calls `_catalog.Reset(entries)` after `await Task.Run(...).ConfigureAwait(false)` | `ReviewCatalog.cs:11`; `FolderLoadCoordinator.cs:85,111` then `_catalog.Reset/SetCurrent/ReplaceOrder`; 47 `ConfigureAwait(false)` in `src/PhotoReview.App` | P0 (data race) | AR04 |
| F6 | `MainWindow` keeps 8 public shadow fields synced by hand from the VM; used by `Benchmark.Cli` (not by tests) | `MainWindow.xaml.cs:36-48`, `PerfSession.cs:301,368,471,473,492,497,501,540`, `LocalUiNextProbe.cs:23,40,74` | P2 | AR02d |
| F7 | `UseSourceBytesCache ? … : null` duplicated 4× in DI; `Platform.Windows` sets `UseWPF` without using WPF; startup `MessageBox` bypasses `IDialogService` | `App.xaml.cs` (FileHashService, ThumbnailCache, PreviewImageService, PreloadScheduler registrations); `Platform.Windows.csproj`; `App.xaml.cs:182` | P2 | AR03 |
| F8 | Stale/broken release outputs and leftovers | `outputs/release/PhotoReview-framework-dependent` has 5 files (no `Core.dll` → cannot run); root `PhotoReview.App/`, `PhotoReview.Tests*/` are bin/obj only; 2 prunable git worktrees | P2 | AR06 |
| F9 | Docs contradict code/each other: 17 broken links; ADR 0001/0002 evidence deleted in `4afee69`; T89 said "not on master" but PR #15 merged it; DT said "all TODO"; T0 budget 15.5 KB > 12 KB; `docs-budget.ps1` never counts `docs/INDEX.md` as T0 | link scan; `git log --diff-filter=D`; `git branch --contains 10ff31f` | P1 (process) | AR00 (partly), AR07 |

**Correction to the first review draft:** it stated the `MainWindow` public fields were unused. They are unused by `tests/` but **used by `tools/PhotoReview.Benchmark.Cli`** (F6). The plan replaces them with read-only properties instead of deleting the surface.

## Tasks

| ID | Title | Decision | Depends on | Needs real machine/GUI | Status |
|---|---|---|---|---|---|
| AR00 | Docs baseline: this summary, plans, ADR 0005 draft, fix stale status, T0 budget | — | — | No | DONE (branch) |
| AR01 | Ship TurboJpeg (explicit registration + release check) | Q-AR1 | — | Yes (1 check) | TODO |
| AR02 | One composition root for app, tests and benchmarks | Q-AR3 | T89 GUI state known | Yes (Fit + perf re-baseline) | TODO |
| AR03 | Small DI/boundary cleanup (SourceBytes policy, UseWPF, startup dialog) | — | — | No | TODO |
| AR04 | Enforce UI-thread affinity in App layer (ADR 0005) | Q-AR2 | AR02a–c, AR02e baseline | Yes (GUI + perf) | TODO |
| AR06 | Release output + workspace cleanup | Q-AR4 | — | No | TODO |
| AR07 | Docs repair and task-group triage | Q-AR5 | AR00 | No | TODO |

(AR05 was folded into AR03c.)

## Order and parallelism

```text
AR00 ─┬─> AR01 ─────────────────────┐
      ├─> AR03 (a,b,c independent) ─┤
      ├─> AR06                      ├─> AR07 (final docs pass)
      └─> AR02a → AR02b → AR02c → AR02e(baseline) → AR02d
                                     └─> AR04 ────────┘
```

AR01, AR03, AR06 can run in parallel. AR04 must wait for AR02c+AR02e because the only way to prove AR04 did not slow `key → present` is a perf session that runs the production graph.

## Relationship to existing groups

- **WD01 (UI-thread audit)** — AR04 is the concrete implementation of WD01's audit. If Q-AR2 = yes, WD01 no longer waits for OC14 (thread affinity does not depend on Undo semantics). WD03–WD06 still wait for OC14.
- **OC14 (Undo gate)** — AR02d keeps `_fileActionInProgress` semantics unchanged (only visibility changes). No OC14 logic is touched.
- **T89 (Fit)** — AR02a changes the production `ApplyInitialViewMode` input from `(0,0)` to the real viewport. It must be validated together with T89's GUI acceptance, and `ApplyFitViewAsync` must stay two-pass (existing rule).
- **D06/D07/D12 and T66 numbers** — any number produced by `--perf-session`/`--ui-next-probe` before AR02c came from the non-production graph (F2). Keep them for history, do not compare them with post-AR02c numbers.

## Rules added by this plan (in force once the linked task is DONE)

1. App layer does not use `ConfigureAwait(false)` (AR04, ADR 0005).
2. No second composition root: tests and tools build the app through `AppHost.BuildServices(overrides)` (AR02).
3. Every `DecoderBackend` value offered in Settings has a registered provider in the release (AR01).
