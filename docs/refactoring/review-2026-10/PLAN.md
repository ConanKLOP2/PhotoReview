# Whole-code review 2026-10-01: fix plan

**Base:** `origin/master` `151f4964` · **Created:** 2026-10-01 · **Status:** PLANNED (nothing implemented yet)

Source: a read-only, function-by-function review of all of `src/` (8 parallel reviewers, ~40k lines, ~1,100 methods),
with every MED finding re-read by the lead against the code. No HIGH finding. This directory is the single plan for
fixing every finding and adding the missing tests. When everything is DONE: one line in
[`../HISTORY.md`](../HISTORY.md), then delete this directory (AGENTS.md "Task completion").

| File | Content |
|---|---|
| `PLAN.md` (this file) | IDs, decisions, PR waves, gates, tracker |
| [`TASKS-CORE.md`](TASKS-CORE.md) | RV-C* (FileActions/Catalog/Session) and RV-S* (Settings/IO/Instance/Diagnostics) |
| [`TASKS-APP.md`](TASKS-APP.md) | RV-A* (Coordinators, Input, ViewModels, windows, services) |
| [`TASKS-IMAGING.md`](TASKS-IMAGING.md) | RV-I* (Decoding, Metadata, TurboJpeg, Caching, Preload) |
| [`TASKS-RAW-PLATFORM.md`](TASKS-RAW-PLATFORM.md) | RV-R* (Imaging.Raw/LibRaw) and RV-P* (Platform.Windows, Localization.Generator) |
| [`TASKS-TESTS.md`](TASKS-TESTS.md) | RV-T* test gaps with no code defect behind them |

## 1. Conventions for every task

- **ID:** `RV-<area><nn>`. Severity `MED`/`LOW`; evidence `CONFIRMED` (lead re-read the code path) or `PLAUSIBLE`
  (needs the reproducer test of step 1 to prove it; if the test cannot be made to fail on current `master`, close the
  task as `NOT-A-BUG` with one line why, and keep the test only if it pins a useful contract).
- **Order inside a task (TDD):** (1) write the reproducer test and see it FAIL on unchanged code; (2) apply the fix;
  (3) see it pass; (4) mutation check: revert just the fix (or break the guarded line) and confirm the new test fails
  again, then restore (AGENTS.md > Tests). Never mutate Recycle Bin code against the real bin (use fakes).
- **Line numbers** are for `151f4964`; re-locate by method name if they drift.
- **Test rules:** `Method_Scenario_ExpectedResult` names; no fixed delays, no `Task.Yield()` polling, no wall-clock
  asserts; real-OS tests get `Category=Native`/`Slow` and clean up; WPF tests are `Category=UI` +
  `[Collection("GlobalState")]`.
- **Code rules:** explicit `StringComparison`; `CultureInfo.InvariantCulture` for anything machine-read;
  no new analyzer suppression without a one-line reason; new UI text = new key appended to `en.json` and `vi.json`
  (run `tools/i18n-check.ps1`).
- **Agent/model** (user rule): `sonnet` for routine fixes with a clear spec, `haiku` for mechanical edits/doc lines,
  strongest model only for the tasks marked **[strong]** (data safety, concurrency); a strong-model agent delegates
  its mutation checks and extra tests to `sonnet` sub-agents.

## 2. Decisions needed from the user before the affected task starts

Each decision gets `decisions/RV-D<n>.md` (frontmatter + detail) and `tools/generate-open-decisions.ps1` in the PR
that implements it. Recommended option first.

| ID | Blocks | Question | Options (recommended first) |
|---|---|---|---|
| RV-D1 | RV-C01 | How should Move-undo decide "destination unchanged" when the destination volume rounds mtime (FAT 2 s, exFAT 10 ms)? | **A** size equal and \|mtime delta\| <= 2 s · B re-stat destination right after the move and fingerprint that stamp (in-session only; journal-restored undo still needs A) · C size only (same as the 2026-09-30 group-Copy decision) |
| RV-D2 | RV-S01 | An unknown/garbage enum value in `config.json` loads as the enum's zero member, not the `AppSettings` default (6 settings affected). Contract? | **A** reset to the `AppSettings` default and list it in `LastLoadRepairs` (startup dialog) · B same reset, silent · C keep zero member, document it |
| RV-D3 | RV-A03 | Plain-key shortcuts ignore Ctrl/Shift (Ctrl+Delete recycles, Ctrl+Enter / Ctrl+F5 run actions; Alt combos already never match because WPF reports them as `Key.System`). Rule? | **A** file-changing commands (Recycle, every user Action) fire only with NO modifier; Move/Copy-to-folder keep "no Ctrl, Shift = picker"; navigation/zoom/toggles unchanged · B every plain-key command rejects Ctrl (Shift still allowed); only Undo/OpenFolder use Ctrl · C keep as is, pin with tests |
| RV-D4 | RV-A12 | Changing "Image order" in Settings does not re-sort the open folder. | **A** reload the open folder on change (same path as RawSupport/RawPairMode) · B keep, change the hint text to "applies to the next folder you open" |
| RV-D5 | RV-A10 | Duplicate batch-recycle finishing after the user opened another folder returns silently and is not undoable. | **A** report it through the late-completion status sink (like `FileActionController.ReportLateCompletion`), stay non-undoable, document · B also register a group Undo for the batch |

## 3. PR waves (each PR based on `origin/master`, no stacking)

Files were assigned so that PRs of the same wave touch disjoint files and can run in parallel worktrees
(`git worktree add .claude/worktrees/<name> -b <branch> origin/master`).

### Wave 0
| PR | Branch | Content | Model |
|---|---|---|---|
| 0 | `docs/review-2026-10-plan` | this plan | - |

### Wave 1 — MED findings and their neighbours (parallel)
| PR | Branch | Tasks | Main files | Model |
|---|---|---|---|---|
| 1 | `fix/rv-fileactions-undo` | RV-C01, C02, C03, C06, C07, C08 | `UndoService.cs`, `FileActionService.cs`, `RecoveryRetryService.cs` | **[strong]** |
| 2 | `fix/rv-settings-load` | RV-S01, S03, S04 | `LenientEnumConverter.cs`, `SettingsNormalizer.cs`, `SettingsStore.cs` | sonnet |
| 3 | `fix/rv-raw-containers` | RV-R01, R02, R03, R04 | `Cr3ContainerReader.cs`, `TiffHeaderNavigator.cs`, `NefContainerReader.cs`, `JpegMarkerProbe.cs`, `DngContainerReader.cs`, `PreviewSelector.cs` | sonnet |
| 4 | `fix/rv-decoding-hardening` | RV-I01, I03, I04, I05, I06, I07, I08 | `EmbeddedThumbnailReader.cs`, `TurboJpegDecoder.cs`, `ImageDecoderFactory.cs`, `TiffStructure.cs` | sonnet |
| 5 | `fix/rv-fileaction-gate` | RV-A01 | `FileActionGate.cs` | **[strong]** |
| 6 | `fix/rv-cache-preload` | RV-I02, I09, I10, I11, I12, I13, I14, I15, I16 | `ThumbnailCache.cs`, `NavigationPace.cs`, `PreloadScheduler.cs`, `SourceBytesCache.cs` | **[strong]** |

### Wave 2 — remaining LOW findings (parallel; start after wave 1 merges to avoid test-file conflicts)
| PR | Branch | Tasks | Main files | Model |
|---|---|---|---|---|
| 7 | `fix/rv-app-coordinators` | RV-A03, A04, A05, A06, A07, A08, A09, A10 | `ShortcutRouter.cs`, `ImagePresenter.cs`, `FolderLoadCoordinator.cs`, `SiblingFolderNavigator.cs`, `FileActionController.cs`, `DuplicateCleanupController.cs` | sonnet (A05/A06 reviewed by strong) |
| 8 | `fix/rv-app-shell` | RV-A02, A11, A12, A13, A14, A15, A16, A17 | `MainWindow.xaml.cs`, `ViewerState.cs`, `MainViewModel.cs`, `BatchReviewWindow.xaml.cs`, `WpfDialogService.cs`, `WindowPlacementService.cs`, `WpfPresentationSink.cs` | sonnet |
| 9 | `fix/rv-core-misc` | RV-C04, C05, C09, S02, S05, S06, S07 | `OperationJournal.cs`, `SessionStore.cs`, `ReviewCatalog.cs`, `DiagOptions.cs`, `ForwardedPathProtocol.cs`, `PhysicalFileSystem.cs`, `ImmediateUiScheduler.cs` | sonnet (C04 **[strong]**) |
| 10 | `fix/rv-platform` | RV-P01..P08 | `Platform.Windows/*`, `TrGenerator.cs` | sonnet (P08 haiku) |

### Wave 3 — test-only gaps (parallel, one PR per test project)
| PR | Branch | Tasks |
|---|---|---|
| 11 | `test/rv-core-gaps` | RV-T01..T12 (Core.Tests) |
| 12 | `test/rv-app-gaps` | RV-T20..T32 (App.Tests) |
| 13 | `test/rv-imaging-gaps` | RV-T40..T58 (Imaging.Tests) |
| 14 | `test/rv-integration-gaps` | RV-T60..T67 (Integration.Tests, UI/Native) |

### Wave 4 — close-out
| PR | Branch | Content |
|---|---|---|
| 15 | `docs/rv-close` | HISTORY line, delete this directory, docs-sync `ACTIVE-TASKS.md`/`task_on_progress.md`, real-machine check results |

## 4. Gates (every PR, before push)

1. `dotnet build PhotoReview.slnx -c Release` → 0 warnings, 0 errors.
2. `tools/verify-all.ps1 -Parallel -Hidden` (never exclude `Category=UI`; bounded by `tests/test.runsettings`).
   A single project while iterating: `tools/run-tests-hidden.ps1 tests/<Project>/<Project>.csproj --filter "FullyQualifiedName~<Class>"`.
3. Each new test was seen failing on the unfixed code and failing again under the mutation check (record the
   mutation in the PR description: "mutated X → test Y failed").
4. `tools/i18n-check.ps1` if any UI text changed; `tools/check-doc-links.ps1` and `tools/docs-budget.ps1 -Check` if docs changed;
   `tools/check-open-decisions.ps1` if a `decisions/` file was added.
5. PR 3 only: if the local RAW corpus exists (`tools/fetch-raw-samples.ps1`), run `Imaging.Tests` with
   `PHOTOREVIEW_RAW_CORPUS_STRICT=1` and `PHOTOREVIEW_LIBRAW_STRICT_CORPUS=1`.
6. PR 8 only: real-machine visual check of RV-A02 (fullscreen from a maximized window) on the user's PC, reported in the PR.
7. After merge: build Release in the main checkout (`CLAUDE.local.md`), report the version.

## 5. Tracker

Status values: TODO · DECISION (waiting for RV-D*) · IN-PR (#n) · DONE · NOT-A-BUG.

| ID | Sev | Ev | Title | PR | Status |
|---|---|---|---|---|---|
| RV-C01 | MED | CONF | Move undo fails on FAT/exFAT destination (exact mtime compare) | 1 | DECISION (D1) |
| RV-C02 | LOW | CONF | Single Move/Copy cancelled before start leaves a Recovery item | 1 | TODO |
| RV-C03 | LOW | PLAU | Single Copy failure leaves a partial destination | 1 | TODO |
| RV-C04 | LOW | PLAU | Journal torn-tail repair runs once per process | 9 | TODO |
| RV-C05 | LOW | PLAU | `SessionStore.Load` throws on a malformed folder string | 9 | TODO |
| RV-C06 | LOW | CONF | `UndoMoveAsync` clears an unrelated last-undo record | 1 | TODO |
| RV-C07 | LOW | PLAU | Single Move undo does not recreate the source folder | 1 | TODO |
| RV-C08 | LOW | CONF | Recovery retries bypass the INV-4 gate | 1 | TODO |
| RV-C09 | LOW | CONF | `ReviewCatalog.Remove` wrong `CurrentIndex` for a non-current removal | 9 | TODO |
| RV-S01 | MED | CONF | Unknown enum in config loads as zero member, not the default | 2 | DECISION (D2) |
| RV-S02 | LOW | CONF | `DiagOptions` parses ints with the current culture | 9 | TODO |
| RV-S03 | LOW | PLAU | Reset blank mandatory shortcut can collide with an action key | 2 | TODO |
| RV-S04 | LOW | PLAU | A throwing `Changed` handler makes `Load` fall back to defaults | 2 | TODO |
| RV-S05 | LOW | PLAU | Forward protocol: lone surrogate makes `Encode` throw | 9 | TODO |
| RV-S06 | LOW | PLAU | Circular reparse point makes `ResolveRealPath` throw | 9 | TODO |
| RV-S07 | LOW | PLAU | `ImmediateUiScheduler` throws instead of returning a faulted task | 9 | TODO |
| RV-A01 | MED | PLAU | `FileActionGate.RunQueuedAsync` runs work under the lock (re-entrancy breaks FIFO) | 5 | TODO |
| RV-A02 | MED | PLAU | Fullscreen from a maximized window may keep the taskbar | 8 | TODO |
| RV-A03 | MED | PLAU | Plain-key shortcuts ignore modifiers (Ctrl+Delete recycles) | 7 | DECISION (D3) |
| RV-A04 | LOW | CONF | Post-present stat failure leaves status "Loading" and skips session save | 7 | TODO |
| RV-A05 | LOW | PLAU | Superseded folder load lets exceptions escape | 7 | TODO |
| RV-A06 | LOW | CONF | Superseded load overwrites `ReadabilityProbe` | 7 | TODO |
| RV-A07 | LOW | PLAU | Folder scan ignores cancellation | 7 | TODO |
| RV-A08 | LOW | PLAU | Sibling navigation spins forever on direction 0 | 7 | TODO |
| RV-A09 | LOW | PLAU | "Moved to…" status raced by the next present | 7 | TODO |
| RV-A10 | LOW | PLAU | Duplicate batch silent when the folder changed | 7 | DECISION (D5) |
| RV-A11 | LOW | CONF | `ViewerState._fitAxisViewport` goes stale | 8 | TODO |
| RV-A12 | LOW | PLAU | Changing sort mode does not re-sort the open folder | 8 | DECISION (D4) |
| RV-A13 | LOW | CONF | `BatchReviewWindow` stats every file on the UI thread | 8 | TODO |
| RV-A14 | LOW | PLAU | `ShowRecovery` swallows a journal read error silently | 8 | TODO |
| RV-A15 | LOW | CONF | `WindowPlacementService` leaves a temp file on write failure | 8 | TODO |
| RV-A16 | LOW | CONF | `MainWindow` never unsubscribes `SettingsStore.Changed` | 8 | TODO |
| RV-A17 | LOW | PLAU | `TracePresented` Rendering handlers pile up while minimized | 8 | TODO |
| RV-I01 | MED | CONF | Embedded thumbnail buffer sized from untrusted header | 4 | TODO |
| RV-I02 | MED | PLAU | Thumbnail persist catches only IO errors | 6 | TODO |
| RV-I03 | LOW | CONF | TurboJpeg header grow stops at SOS without checking its payload | 4 | TODO |
| RV-I04 | LOW | CONF | Second EXIF orientation parser in TurboJpeg diverges | 4 | TODO |
| RV-I05 | LOW | CONF | `LoadBytes` assumes stable length and < 2 GB | 4 | TODO |
| RV-I06 | LOW | PLAU | No decompression-bomb / scan-limit guard in TurboJpeg | 4 | TODO |
| RV-I07 | LOW | PLAU | `ImageDecoderFactory.Create` may build a decoder twice | 4 | TODO |
| RV-I08 | LOW | PLAU | `TiffStructure.TryGetValueSpan` reads offset 0 on a short entry | 4 | TODO |
| RV-I09 | LOW | CONF | Preload direction flips on Home/End jumps | 6 | TODO |
| RV-I10 | LOW | CONF | Thumbnail load exception can go unobserved | 6 | TODO |
| RV-I11 | LOW | CONF | Dead `.jpg` branch in `ThumbnailCache.DecodeAsync` | 6 | TODO |
| RV-I12 | LOW | PLAU | Preload scheduler exit race drops one navigation | 6 | TODO |
| RV-I13 | LOW | PLAU | Running scheduler keeps a stale entries snapshot | 6 | TODO |
| RV-I14 | LOW | PLAU | Foreign `OperationCanceledException` ends the preload lifetime | 6 | TODO |
| RV-I15 | LOW | PLAU | `_preloadedKeys` grows across box changes | 6 | TODO |
| RV-I16 | LOW | CONF | `SourceBytesCache._pathVersions` never pruned | 6 | TODO |
| RV-R01 | MED | CONF | CR3 `trak` sample accepted as preview on SOI only | 3 | TODO |
| RV-R02 | LOW | CONF | Header-budget `InvalidDataException` skips the LibRaw fallback | 3 | TODO |
| RV-R03 | LOW | CONF | DNG stores negative width/height, truncates compression | 3 | TODO |
| RV-R04 | LOW | CONF | `PreviewSelector` resolves every preview (doc says stop early) | 3 | TODO |
| RV-P01 | LOW | PLAU | `InstanceForwardServer.Dispose` blocks up to 2 s | 10 | TODO |
| RV-P02 | LOW | PLAU | `TryRestore` can raise a modal shell dialog | 10 | TODO |
| RV-P03 | LOW | PLAU | Explorer window enumeration aborts on one COM failure | 10 | TODO |
| RV-P04 | LOW | CONF | `ExplorerOrderService` prefetch CTS leak / post-dispose prefetch | 10 | TODO |
| RV-P05 | LOW | PLAU | `InstanceScope.BeforeOpenAsync` throws on a malformed path | 10 | TODO |
| RV-P06 | LOW | CONF | Negative memory reserve disables preload | 10 | TODO |
| RV-P07 | LOW | PLAU | Generator crashes on two `en.json` files | 10 | TODO |
| RV-P08 | LOW | CONF | Dangling `<summary>` in `WindowsRecycleBin` | 10 | TODO |
| RV-T* | - | - | Test-only gaps, see [`TASKS-TESTS.md`](TASKS-TESTS.md) | 11-14 | TODO |
