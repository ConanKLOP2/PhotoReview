# Review 2026-09-25 — Master findings table (verified)

Companion to [`../../refactoring/REVIEW-2026-09-25-PLAN.md`](../../refactoring/REVIEW-2026-09-25-PLAN.md). Raw reviewer reports (unedited evidence): [`review-2026-09-25/`](review-2026-09-25/) (`core.md`, `imaging.md`, `app.md`, `docs-tests.md`).

**Verification** was done on master `cbd24b8` (2026-09-24) by opening every cited `file:line`.
Legend for the *Verified* column: **V** = reproduced as reported · **V\*** = reproduced, but the report was corrected (location, scope or severity; see the note) · **NR** = not reproduced (dropped) · **NC** = not checked (Low/informational, taken from the report as-is; re-check before fixing).
Severity column: `original → final` when changed. Effort: S ≤ 2 h, M ≤ ½ day, L > ½ day. Risk = risk of the fix.

## Active findings (60)

| ID | Area | Sev | Verified | file:line | Problem (1 line) | Fix (1 line) | Eff | Risk | Depends on | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| IMG-01 | Imaging cache | High | V\* | `Imaging/Caching/PreviewImageService.cs:465-467`, `PreviewCacheFile.cs:121,215` | Downscaled previews are persisted as JPEG with `header[7]=0`; WicDirect emits `Pbgra32` for alpha formats (`WicDirectDecoder.cs:152,190`), so a transparent PNG/GIF/TIFF is baked to opaque black on every later disk-cache hit. Opaque PNGs are unaffected. | Gate `PersistToDiskCache` on "pixel format has no alpha" (Q-R1 option a); optional later: lossless v6 payload for alpha. | S | Low | Q-R1 | 1b |
| CORE-01 | Core threading | High | V\* | `Core/FileActions/UndoService.cs:185,215,227` | Three awaits without `ConfigureAwait(false)` (report missed `:215`) — violates ADR 0005 rule 2; `Task.Run` calls also take no `CancellationToken`. | Add `.ConfigureAwait(false)` to all three; enforce with CA2007 in Core/Imaging/Platform (W2a). | S | None | — | 1a (+2a rule) |
| CORE-06 | Core / Recovery | Medium → **High** | V\* | `Core/FileActions/RecoveryRetryService.cs:34-109`; caller `App/RecoveryWindow.xaml.cs:244` | Retry runs `Copy`/`Move` **and** journal `Append` synchronously on the UI thread: large cross-drive copies freeze the UI and, in Power-loss-safe mode, the fsync runs on the UI thread (contradicts ADR 0007 "off UI thread"). | Add `RetryMoveOrCopyAsync` (mutation + journal via `Task.Run(...).ConfigureAwait(false)`), make `Retry_Click` await it with the button disabled. | M | Low-Med | — | 1a |
| DOC-01 | Docs status | Critical → **High** | V | `task_on_progress.md:3-11`; `docs/ACTIVE-TASKS.md:4,20-22,129,151-153` | Say PR batch #64–#70 is "open"; all of #64–#71 are merged. Violates AGENTS rule #1. | `task_on_progress.md` fixed in this PR; `ACTIVE-TASKS.md` in W3 (after #72). | S | None | #72 | this PR + 3 |
| CORE-02 | Core session | High → **Medium** | V\* | `Core/Session/SessionWriter.cs:83-87,120` | `Dispose()`/`Flush()` block the calling (UI) thread on `_writeLock.Wait()` with no timeout while a debounced write is in flight; `_writeLock` never disposed. Not a deadlock (writer never waits on UI) and, since IO04, the write is small and not fsynced — hence Medium. | Bounded wait on the shutdown path (`Wait(2 s)` + log, Q-R5), dispose the semaphore. | S | Low | Q-R5 | 1a |
| CORE-03 | Core file actions | Medium | V | `Core/FileActions/FileActionService.cs:84-93`; `Core/Settings/ReviewAction.cs:10` | `Destination` from user-editable settings may contain `..` or any path; only "same folder" is rejected, so a mistyped/corrupt config moves files anywhere. | Policy per Q-R2: relative destinations must stay under the source folder; rooted paths allowed; validate at Settings save and at execute time (coded error). | M | Med | Q-R2, L12 merged | 1c |
| CORE-05 | Platform COM | Medium | V\* | `Platform.Windows/WindowsRecycleBin.cs:47-70,78-91` | Non-matching Recycle Bin `item` RCWs, non-matching `verb` RCWs and the `Verbs()` collection are never released (only candidates are). | Release every enumerated item/verb not kept, and the verbs collection, in `finally`. | S | Low | — | 1a |
| IMG-03 | Imaging preload | Medium | V | `Imaging/Preload/PreloadScheduler.cs:296-316` | Memory headroom re-checked only once per worker batch ⇒ up to `workers` (8) extra decodes past the 80 % line. | Cache the probe result for ~50 ms and check on every candidate (or every 2). | S | Low | — | 1b |
| IMG-05 | Imaging tests | Medium | V | `tests/PhotoReview.Imaging.Tests/Caching/PreviewCacheFileTests.cs:170-184` | No test at `PreviewImageService` level for a transparent source through the disk cache (the gap that let IMG-01 ship). | Add the IMG-01 regression test + quality-gate case. | S | None | IMG-01 | 1b |
| IMG-06 | Imaging structure | Medium | NC | `PreviewImageService.cs` (689 lines), `PreloadScheduler.cs` (519) | Too many concerns per class; invariant-critical shared state. | Extract `PreviewDiskPersistWorker` + bounded dims cache; incremental, green suite each step. | L | Med | W1b, W5b | Backlog |
| APP-01 | App a11y | Medium | V\* | `App/ActionProfilesWindow.xaml:18-34` | 0 `AutomationProperties` in this window (also 0 in `BatchReviewWindow.xaml`, `DiagnosticsWindow.xaml`). | `AutomationProperties.LabeledBy` to the visible label, `.Name="{loc:Tr …}"` for icon-only buttons. | S | Low | L12 (if new keys) | 4 |
| APP-02 | App a11y | Medium | V | `App/SettingsWindow.xaml` (33 controls, 5 named) | ~85 % of Settings controls unlabeled for screen readers. | Same as APP-01. | M | Low | L12 (if new keys) | 4 |
| TOOL-01 | Tools | Medium | V | `tools/verify-release.ps1:1-9` | Only script without `$ErrorActionPreference='Stop'` — release gate can continue past missing files. | Add it + explicit `$LASTEXITCODE` checks after native calls. | S | Low | — | 2a |
| DOC-02 | Docs status | High → **Medium** | V | `docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md:21,46,48,152`, `REFACTOR-STATUS.md:11`, `OPTIMIZE-CLEAN-SUMMARY.md`, `ACTIVE-TASKS.md:20,129` | Still say ST08/09 + OC15–18 blocked by OC14 (merged #64; #73 completes ST08/09/OC15–18). | Update after #72 + #73 merge. | S | None | #72, #73 | 3 |
| DOC-05 | Docs ADR | Medium | V | `docs/adr/` (0001–0007 only); `docs/architecture.md:94` | Zoom decision Q-Z1 (100 % = source pixel, on-demand original) has no ADR. | Write ADR 0008; link from architecture.md + OPEN-DECISIONS Q-Z1. | M | None | #72 | 3 |
| DOC-10 | Docs structure | Medium | V | 5 status files | Group status duplicated in `task_on_progress`, `ACTIVE-TASKS`, `REFACTOR-STATUS`, `OPTIMIZE-CLEAN-SUMMARY`, `STRUCTURE-OPTIMIZE-STATUS` ⇒ recurring staleness. | `ACTIVE-TASKS.md` = single source; others keep only a link + their own task detail. | M | Low | #72, #73 | 3 |
| TEST-01 | Tests isolation | High → **Medium** | V\* | `tests/PhotoReview.App.Tests/HotPath/RealPhotosManualTests.cs:52-57` | Sets `PHOTOREVIEW_DATA_ROOT` process-wide, never restores it, no `[Collection("GlobalState")]`, temp root never deleted. Only in `Manual` runs, hence Medium. | Collection + save/restore in `Dispose` + delete temp root. | S | None | — | 2a |
| TEST-03 | Tests hygiene | Medium | V | `tests/PhotoReview.App.Tests/HotPath/TestRecycleBinCleanup.cs:53` | Cleanup removes only the current run's exact folder; a hard-killed run leaves `TC06_RecycleBin_*` orphans forever (~2650 today). | Add prefix sweep (own `TC06_RecycleBin_` items only, older than 10 min) at fixture start. | M | Low | — | 2b |
| TEST-04 | Tests determinism | Medium | V | `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.Explorer.cs:37,82,137,148,206` | "Never happens" asserted by waiting a 1 s wall-clock window. | Wait for a settled/generation signal (e.g. Explorer-order task completion) then assert state. | M | Low | — | 2b |
| TEST-06 | Tests coverage | Medium | NC | `SettingsWindow.xaml:110-113` → `AppSettings.JournalDurability` → `OperationJournal` | No integration test from the Settings radio to the journal durability actually used. | Integration test: select Power-loss safe, Apply, execute a Move, assert journal opened durable (fake `IFileSystem` flag). | M | None | — | 2b |
| TEST-09 | Tests isolation | Medium | V\* | 3× `GlobalStateCollection.cs`; `Architecture.Tests/EnvironmentVariableScopeTests.cs` covers `src/` only | Nothing forces test classes that call `Environment.SetEnvironmentVariable` into `[Collection("GlobalState")]`. | Architecture rule over `tests/` (Roslyn/regex like existing rules). | M | None | — | 2a |
| TEST-10 | Tests hygiene (new) | Medium | V | `src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:100`; test `Integration.Tests/BenchmarkWorkloadRunnerTests.cs:55` | Benchmark "delete" calls `FileSystem.DeleteFile(..., SendToRecycleBin)` directly; the `action-interleaved` test (runs in the AGENTS local filter) puts items in the user's real Recycle Bin each run. | Route delete through injected `IRecycleBin` (fake in tests). | S | Low | — | 2a |
| HYG-04 | Repo hygiene | Medium | NC | `work/` (gitignored) | Logs/reports accumulate with no retention. | `tools/clean-work.ps1 -OlderThanDays 14` (dry-run default). | S | None | — | 5a |
| CORE-07 | Core structure | Low | V | `FileActionService.cs:119-161`, `RecoveryRetryService.cs:62-107` | Move/Copy verify-and-journal sequence duplicated. | Extract shared helper after W1a (async retry) lands. | M | Low | W1a | 5c |
| CORE-08 | Core perf | Low | NC | `Core/FileActions/OperationJournal.cs:121-161` | Reverse reader re-parses the whole doubled window each retry (Move-sparse journals). | Parse only the new prefix; add Move-sparse perf fixture. | M | Low | — | 5c |
| CORE-09 | Platform | Low | V | `Platform.Windows/InstanceLock.cs:21` | `catch (ApplicationException) { }` silently. | Log the swallow. | S | None | — | 5c |
| CORE-10 | Core settings | Low | NC | `Core/Settings/SettingsStore.cs:62-68` | Transient I/O at load ⇒ defaults for the session with same log level as corruption. | Distinct log message/level. | S | None | — | 5c |
| CORE-11 | Platform | Low | NC | `Platform.Windows/WindowsMemoryProbe.cs:35-52` | P/Invoke failure = fail-closed, undocumented, pure logic untested. | Comment + unit tests on snapshot logic. | S | None | — | 5c |
| CORE-12 | Platform | Low | NC | `Platform.Windows/WindowsNaturalComparer.cs:22-36` | Narrow catch around `StrCmpLogicalW` (informational). | Comment justifying the narrow catch. | S | None | — | 5c |
| IMG-02 | Imaging RAM | Medium → **Low** | V\* | `PreviewImageService.cs:26,449,624,649` | `_originalDimensions` never trimmed by eviction (~250 B/entry, grows per distinct file seen). | Bounded LRU (e.g. 200 k entries). | S | Low | — | 5b |
| IMG-04 | Imaging diag | Medium → **Low** | V | `Decoding/Wic/WicDirectDecoder.cs:211-217` | ICC-specific catch wraps converter/rotator stages ⇒ wrong diagnostic (render still correct). | Narrow the catch to color-chain build + `CopyPixels`. | S | Low | — | 5b |
| IMG-07 | Imaging | Low | NC | `Imaging.TurboJpeg/TurboJpegDecoder.cs:312-389` | Duplicated JPEG marker walker. | Shared callback-based walker. | S | Low | — | 5b |
| IMG-08 | Imaging | Low | NC | `Decoding/ImageDecoderFactory.cs:59-75` | New decoder chain per `Create` call. | Cache per backend inside the factory. | S | Low | — | 5b |
| IMG-09 | Imaging API | Low | NC | `Caching/SourceBytesCache.cs:22-31` | Public sync-over-async `GetOrRead`. | XML-doc warning or async overload. | S | Low | — | 5b |
| IMG-10 | Imaging disk cache | Low | V | `Caching/DiskCacheStore.cs:165-194` (`:171` orders by `LastAccessTimeUtc`) | Full enumerate+sort per prune; nothing in `src/` ever touches access time, so "LRU" may be creation order. | Incremental size tracking; touch/`LastWriteTime` on hit or document. | M | Low | — | 5b |
| IMG-11 | Imaging RAM (new) | Low | NC | `RamBudgetPolicy` / `SourceBytesCache` | 16 GiB preview + 16 GiB source-bytes budgets not cross-checked against physical RAM. | Startup clamp to a fraction of physical RAM + log. | S | Low | — | 5b |
| APP-03 | App theme | Medium → **Low** | V | 48 hex literals in 9 XAML files (e.g. `ActionProfilesWindow.xaml:1,16,23,26,32`) | Colors duplicate `Themes/DarkControls.xaml:10-21` brushes; `#505050` is an off-palette one-off. | `{DynamicResource Dark.*}`; add a XAML lint architecture rule. | M | Low (visual) | W1c | 4 |
| APP-04 | App UX | Low | NC | `App/MainWindow.xaml.cs:113,259,364` | `_ = ApplyFitViewAsync()` failures only logged. | Status-bar message on failure (keep the T89 multi-pass loop untouched). | S | Low | #73 | 5a |
| TOOL-02 | Benchmarking | Low | NC | `Benchmarking/PerformanceTestHarness.cs:59-71` | Cold measurement includes first-call JIT. | One untimed warm-up decode. | S | Low | — | 5a |
| TOOL-03 | Docs/CI | Low | V | `AGENTS.md:49` vs `ci.yml:83-95` | Documented local filter differs from CI (Integration). | Resolve with Q-R3, then one filter everywhere. | S | None | Q-R3, #72 | 2a / 3 |
| TOOL-04 | Tools (new) | Low | V | `tools/verify-release.ps1:7` | Default `-ReleaseDirectory` points at deleted `outputs\release\…` (Q-AR4). | Default to the CI publish path. | S | None | — | 2a |
| DOC-03 | Docs | High → **Low** | V\* | `docs/refactoring/TEST-CLEANUP-SUMMARY.md:3` (not `:307`) | Says TC06/TC07 live in `Integration.Tests`; they are in `App.Tests/HotPath`. | One-line fix after #72. | S | None | #72 | 3 |
| DOC-04 | Docs ADR | Medium → **Low** | V | `docs/adr/0007-io-durability-contract.md:4` | "Triển khai … làm sau khi nhánh i18n được merge" — implemented (#65, #66) but no addendum. | Implemented-by addendum (Vietnamese, matching the ADR). | S | None | — | 3 |
| DOC-08 | Docs | Low | NC | `docs/refactoring/TC00-BASELINE-REPORT.md` | Stale snapshot still listed as T1. | `git mv` to archive, drop INDEX row. | S | None | #72 | 3 |
| DOC-09 | Docs | Medium → **Low** | NC | `OPTIMIZE-CLEAN-SUMMARY.md` OC14 row | Self-contradictory status (#72 edits this file). | Fix after #72/#73. | S | None | #72, #73 | 3 |
| DOC-11 | Docs | Low | NC | repo root | No RELEASING/CONTRIBUTING. | Short `docs/RELEASING.md` (versioning, CI tag, verify-release). | M | None | — | Backlog |
| DOC-12 | Docs | Low | NC | `docs/refactoring/CQ-WARNINGS-PLAN.md` (8 KB) | Done plan never compressed. | Compress to one line + archive detail. | S | None | #72 | 3 |
| DOC-13 | Docs | Low | V | `docs/refactoring/OPEN-DECISIONS.md:33` | "Most critical blocker: OC14" is false. | Fix after #72 (which rewrites the file). | S | None | #72 | 3 |
| DOC-14 | Docs ADR (new) | Low | NC | `docs/adr/0001-image-decoder.md` | Says embedded-ICC images go to WPF; WicDirect now color-transforms first. | Addendum. | S | None | — | 3 |
| TEST-02 | CI | Medium → **Low** | V\* | `.github/workflows/ci.yml:83-95` | Premise wrong: `Integration.Tests` **does** run in CI; only 6 cheap `Category=Integration` tests are excluded (2 journal in Core.Tests, 2 benchmark-runner, `PlatformPrimitivesTests`). | Per Q-R3: drop the exclusion (after TEST-10). | S | Low | TEST-10, Q-R3 | 2a |
| TEST-05 | Tests determinism | Low | V | `tests/PhotoReview.Imaging.Tests/BurstPreloadTests.cs:284` | `Task.Delay(100)` to prove absence. | Deterministic gate/`NextStartAsync` with no-start assertion after scheduler idle signal. | S | Low | — | 2b |
| TEST-07 | Tests | Low | V | `AGENTS.md:49`, `tools/verify-all.ps1:7,19`, `ci.yml` | `Category=Stress` used by no test. | Remove `Stress` from filters/switches (or re-add a test). | S | None | #72 (AGENTS) | 2a / 3 |
| HYG-01 | Build | Low | V | `tests/PhotoReview.Architecture.Tests/*.csproj:8` | Redundant `NoWarn CA1707`. | Remove. | S | None | — | 5a |
| HYG-02 | Build | Low | V | `tests/PhotoReview.TestSupport(.Windows)/*.csproj` | No `<Platforms>AnyCPU;x64</Platforms>`. | Add. | S | None | — | 5a |
| HYG-03 | Solution | Low | NC | `PhotoReview.slnx` | Inconsistent solution folders. | Regroup `/src/`, `/tests/`, `/tools/`. | S | Low | — | 5a |
| HYG-05 | Repo | Low | V | `.gitignore:97-98` | `!` exception for sample log undocumented. | Add a comment. | S | None | — | 5a |
| HYG-06 | Repo | Low | V | `outputs/` (3 tracked files) | Tracked install scripts live in a folder named like build output. | Per Q-R4. | S | Low | Q-R4 | 5a |
| HYG-07 | Repo | Low | V | `.gitignore:378` | `/Claude outputs/` entry; folder no longer exists. | Remove the line. | S | None | — | 5a |
| HYG-08 | CI | Low | V | `ci.yml:83-95` | Same filter string repeated 5×. | Job-level `env: TEST_FILTER`. | S | None | — | 2a |
| HYG-10 | Build | Low | NC | `Directory.Packages.props:14` | `Microsoft.CodeAnalysis.CSharp` must not exceed the SDK compiler; only a comment guards it. | Doc note or build check. | M | Low | — | 5a |

## Dropped / merged / handled elsewhere (5)

| ID | Outcome | Reason |
|---|---|---|
| CORE-04 | Dropped (withdrawn by reviewer) | Reviewer's own re-read: ordering is correct; the real issue is perf → CORE-08. |
| DOC-06 | Merged into CORE-01 + W2a | Same issue (ADR 0005 rule 2 not enforced in Core/Imaging/Platform). |
| DOC-07 | Handled by PR #72 (DT10) | T0 trimmed to 11,128 B there. |
| TEST-08 | Duplicate of DOC-01 | — |
| HYG-09 | **NR** — not reproduced | `.gitattributes` exists (`* text=auto`). Optional: add explicit `binary` rules for `*.jpg *.png *.dll` in W5a. |

## Counts

| Severity | Reported | After verification |
|---|---|---|
| Critical | 1 | 0 |
| High | 6 | 4 |
| Medium | 24 | 19 |
| Low | 30 | 37 |
| Dropped/merged | — | 5 |

Reported total 61 + 4 new (TEST-10, TOOL-04, IMG-11, DOC-14) = 65 = 60 active + 5 dropped. Verified V/V\*: 40 of 60 (all High, 16 of 19 Medium); NR: 1.
