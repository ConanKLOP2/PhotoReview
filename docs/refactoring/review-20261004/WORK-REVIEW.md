# Function review — current-head checkpoint (2026-10-04, incomplete)

Current pinned head: `18aa6eb64ba48666de620937211e0782dd6131c1` (`origin/master`). The audit began at `5d291076` and has been reconciled through PRs merged since then. Review branch: `codex/review-all-20261004`; [PR #284](https://github.com/ConanKLOP2/PhotoReview/pull/284), open and not merged. This is a documentation/review PR; it makes no production changes. Core/tooling is continuing semantic review; App/Integration, Imaging and Architecture/TestSupport are complete. Platform.Windows was reviewed, with no ABI defect claimed. The inherited model names are unavailable.

## Coverage contract

The current Roslyn inventory covers 16,799 callable bodies in 1,068 tracked C# files: methods/constructors/operators with bodies, explicit accessors, expression-bodied properties/indexers, local functions and lambdas. Declaration-only interfaces/extern members, implicit record members, XAML event declarations and the outer top-level statement body are excluded. Nested bodies have individual rows. This is an inventory, not semantic sign-off.

The merged-PR reconciliation and bounded source/test delta findings through the pinned head are summarized in [current-delta-20261004.md](current-delta-20261004.md). The current complete callable review remains in progress. The Core continuation overlay was discovered to come from a stale source checkout; 1,650 changed-path rows were invalidated. A current-head reread is underway.

[functions.tsv](functions.tsv) records ID, path, current line/end line, kind, signature, status and rationale. `STATIC-ONLY` means a saved semantic source review, including inherited enclosing-function rationale for small helpers/lambdas; it does not mean runtime PASS. `ISSUE` identifies affected source bodies, not a count of independent bugs. `UNREVIEWED` includes partially read functions whose completed rationale was not saved. Broad-suite pass does not change these statuses. The current ledger has 15,584 `STATIC-ONLY`, 185 `ISSUE`, and 1,030 `UNREVIEWED` rows. A freshness audit found the Core overlay agent checkout remained at `5d291076` while its inventory was at `18aa6eb6`; 1,650 callable rows across 100 affected Core/Platform/tooling/test paths were invalidated. App/Integration separately completed all 1,743 Integration rows. The Core-owned scope excludes Integration: 4,568 rows across 307 paths, with 2,181 current inventory rows reviewed (2,387 remain) across 114 matched paths. The refreshed Core overlay through worker batch 41 maps 1,374 current inventory rows (40 ISSUE, 1,334 STATIC-ONLY). Lead batch 42 reviewed 51 JournalCompaction callables; batches 43–68 reviewed JournalLineParser, benchmark, DuplicateFinder, compaction boundaries, JournalModel, error catalog, commit-clock, older-build downgrade, durability, manual latency, group reconcile and small-gap tests. Current Core coverage is 2,181 rows (44 ISSUE, 2,137 STATIC-ONLY) across 137 paths; 2,387 rows remain. Lead batches 47–50 reviewed JournalModel, JournalErrorCatalog, commit-clock failure, and older-build fixtures; batch 51 found R41 in a downgrade test’s malformed fixture paths. Lead batches 43–45 reviewed JournalLineParser, its manual benchmark, and DuplicateFinder with related tests; batch 46 reviewed compaction boundary tests and added R40. Batch 52 reviewed JournalGroupSchema; batch 53 found R42 in concurrency tests; batch 54 reviewed append robustness; batch 55 reviewed group evidence; batch 56 reviewed durability tests; batch 57 reviewed the manual latency harness; batch 58 reviewed group reconciliation and small-gap tests; batch 59 reviewed Core helpers and enum/localizer callables; batch 60 reviewed PerfAnalysis statistics, navigation and report lambdas; batch 61 reviewed filesystem/compaction IO tests; batch 62 reviewed version/update-checker tests; batch 63 reviewed Explorer snapshot validator cases; batch 64 reviewed natural-sort property and benchmark tests, documenting the Unicode-digit oracle limit; batch 65 reviewed catalog state/mutation tests; batch 66 reviewed catalog robustness/reference-model tests; batch 67 reviewed sorting/catalog service robustness; batch 68 reviewed catalog mutation, Explorer equivalence/reason, and drag-drop tests without a new issue. 552 historical batch rows without a canonical current identity are excluded. Mapping preserves duplicate structural-key occurrences. Exact changed-set coverage is still being recalculated after correcting the historical-ID mismatch. Stale batch totals are not used. App/Integration is complete at 6,455/6,455. Core remains active at the pinned head; Platform.Windows review is complete at 442/442 callable keys (440 STATIC-ONLY, 2 ISSUE); do not read ledger status counts as full-project completion.

The original baseline ledger reported 1,203 App and 694 Imaging-family source bodies reviewed. Those figures are historical and are not current-head coverage. Core/tooling remains active; App/Integration, Imaging and Architecture/TestSupport are complete. Every status is reconciled against this current inventory. [App baseline details](app.md).

| Area | Production bodies | Test bodies | Total | Status |
|---|---:|---:|---:|---|
| App + Integration | 1,229 | 5,226 | 6,455 | 6,455/6,455 rows reread against current source; 6,416 semantic-static, 1 issue, 18 test gaps, 20 test risks; duplicate identity multiplicity preserved |
| Core + tooling | — | — | 4,568 | Current semantic-review filter excludes Platform.Windows and the App-owned 1,743 Integration rows; 1,745 current rows reviewed (2,823 remain), preserving repeated structural identities. Exact changed-set coverage is being recalculated after correcting the historical-ID mismatch |
| Imaging + Raw + LibRaw + TurboJpeg | 716 | 4,279 | 4,995 | 716 production + 4,279 Imaging.Tests reread; complete; 189 related Integration/App/Architecture rows also read |
| Architecture.Tests / TestSupport / TestSupport.Windows | — | 298 | 298 | 298/298 reread; two test-fixture cleanup safety candidates (R24–R25) |

[powershell-functions.tsv](powershell-functions.tsv) additionally inventories 52 named PowerShell functions in 15 scripts; all 52 functions were read statically. All 15 scripts and 3 GitHub workflow YAML files were also read; script-level R27 and workflow-level R29 are outside callable rows. R28 identifies a fixture-integrity oracle gap. The ancestor-junction finding in `Publish-Guard.ps1` was fixed by #292 and must not be carried as open. Native ABI, real WPF interaction, RAW corpus, NAS/performance, actual Recycle Bin, and full mutation campaign are not validated by this review.

## Original baseline findings (R02–R10 fixed; R01 has a residual; R11 remains open)

### Current-head triage through `18aa6eb6`

The R01–R10 descriptions below record defects on the original `5d291076` baseline. #290 fixes the pre-creation/in-flight destination ownership cases in R01, but a separate same-size replacement during completed group-Copy rollback remains under R01 below. R02 #288; R03 #292; R04 #298; R05 #287/#315; R06 #299; R07 #290; R08 #288/#309; R09 #288; and R10 #298 are fixed and should not be reported as open. Their old repros remain regression context only. R11 remains an open conditional permanent-delete safety issue after #312. R12 remains a static reporting concern pending exact current-line recheck. Wave-two findings are carried forward only for unchanged source files; see the linked Core, Platform, and tooling reports under `wave2/`.

### R13 — P1 candidate — Benchmark CLI can follow an output symlink into a photo

`tools/PhotoReview.Benchmark.Cli/RawSurvey.cs:116-126,131-186`; `DecoderBenchmark.cs:134-399`; `RawDecoderBenchmark.cs:63-79`.

The CLI guards output-directory containment but does not reject an existing output file that is a symlink/reparse point. `RawSurvey.ValidateMarkdownPath` validates the parent, then `File.WriteAllTextAsync` follows the report-file link. The fixed summary/report paths in decoder benchmarks have the same shape. A report path linked to a photo can truncate that photo. This is source-traced only; no link was created and no file was changed. Reject final reparse-point targets and use safe create/replace semantics for the report outputs. Current delta review is documented in the Core supplement; add a regression using only a self-cleaning temporary target.

### R14 — P2 candidate — An older folder-open request can replace a newer one

`src/PhotoReview.App/ViewModels/MainViewModel.cs:366-415,1223-1228`; `DuplicateCleanupController.cs:259-269`; `Platform.Windows/InstanceScope.cs:88-120`.

All normal `OpenFolderAsync` calls and duplicate-cleanup reloads reach `OpenFolderCoreAsync` with no request epoch. It awaits ownership negotiation before incrementing the folder epoch. If open B is delayed in `BeforeOpenAsync` and later open C completes first, B can resume with `Proceed` and replace C; the duplicate-cleanup A/B race is the same root path. Existing tests cover a single pending open and immediate outcomes, but not two overlapping open requests. Static trace only. Capture a request token at invocation, recheck after ownership negotiation, preserve the `AfterOpen` contract when ownership was granted, and add a gated B/C regression. No Recycle Bin operation is needed.

### R15 — P2 candidate — Journal reconcile accepts any existing path as a restored group member

`src/PhotoReview.Core/FileActions/OperationJournal.cs:490-498,614-620`.

For a prepared group Recycle undo, `IsGroupMemberCompleted` treats `FileExists(member.Source)` as proof of restoration. If an unrelated/replacement file appears at an original path while another member remains in the Recycle Bin, startup reconciliation can mark the group Committed and remove it from pending Recovery even though the original photo remains unrestored. The active Undo and Recovery checks compare both size and last-write time (`UndoService.cs:561-572`; `RecoveryFileCheck.cs:114-134`), so this reconcile path is weaker. Existing tests seed matching fingerprints and do not cover a mismatched but present target. Static-only recoverability risk; add a fake-filesystem reconciliation case that proves the mismatched target does not settle the group. No real Recycle Bin use.

### R16 — P2 candidate — Checked WIC buffer overflow falls through to unchecked WPF admission math

`src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:238-240`; `FallbackImageDecoder.cs:76-95`; `WpfBitmapImageDecoder.cs:230-235,294`.

WIC accepts each dimension up to `int.MaxValue`, then checked stride/size arithmetic can throw `OverflowException` before `EnsureOutputFits`. The fallback classifier treats `OverflowException` as fallbackable. WPF recomputes bytes as `(long)outW * outH * 4`; sufficiently large positive dimensions can wrap that `long` negative, and `EnsureOutputFits` treats values below its minimum threshold as fitting before `EndInit` attempts full-resolution decode. No cross-backend huge-dimension regression covers this chain. Static arithmetic/control-flow review only; no native decoder or large image ran. Use checked/overflow-safe size admission before either backend allocates or decodes, and ensure admission failures cannot fall back.

### R17 — P2 candidate — Single recovery retry can act on a replacement source

`src/PhotoReview.Core/FileActions/RecoveryRetryService.cs:73-116,300-334`. Single retry validates the failed source size/last-write fingerprint before appending Prepared, then `ExecuteRetry` moves/copies without rechecking identity immediately before mutation. A replacement arriving in that interval can be committed under the old failed operation. Group retry has explicit fingerprint checks around Prepared; the single path does not. Static race trace only; no file was changed. Re-stat and compare the expected fingerprint immediately before mutation and add a gated fake replacement test.

### R18 — P2 candidate — Move Undo can restore a replacement after identity preflight

`src/PhotoReview.Core/FileActions/UndoService.cs:153-280,348-437`. Single and group Move Undo validate the moved-to fingerprint and original-path absence, append Prepared, then move without rechecking the moved-to file identity before mutation. A replacement can be restored and committed; the post-move verification is length-only. Static race trace only; no file was changed. Recheck each fingerprint after Prepared and before each move; add gated fake replacements for single and group Undo.

The same R18 identity weakness affects Recycle Undo: `WindowsRecycleBin.RestoreViaShell` waits for any original-path file with the expected last-write timestamp but discards the expected size. Single Undo trusts a successful restore result; group Undo accepts mere path existence. A competing file with the same timestamp but different size can be mistaken for the restored photo if shell restoration does not complete. Existing restore tests cover a blocker already present before invocation, not a post-invocation replacement. Static only; no shell or Recycle Bin call ran.

### APP-P01 — P2 candidate — Overlapping presentation reads Token from disposed CTS

`src/PhotoReview.App/Coordinators/ImagePresenter.cs:247-253,286,377-378`. `PresentCoreAsync` captures `viewerDecodeToken` but later rereads `viewerDecodeCts.Token`. A second overlapping presentation can exchange and dispose that CTS first, making the later getter throw `ObjectDisposedException`. Existing supersession tests do not cover this exact interleave. Static race trace only; no runtime reproduction. Use the captured token consistently and add a gated overlap regression.

### APP-P02 — P2 candidate — Loss of display timing can double-count glide elapsed time

`src/PhotoReview.App/Input/KineticPan.cs:326-341. After Predict anchors the glide to a refresh grid, a null timing callback advances using rendering time but keeps the old anchor. When timing returns at the same period, Advance recomputes from that old anchor and can apply the missing interval again, causing a pan jump. Display timing can be null during initialization, query failure, or monitor change. Static trace only. Re-anchor on timing loss/recovery or use one consistent fallback epoch; add an anchored → null frames → same-period recovery regression. No runtime reproduction.

### R20 — P2 candidate — RAW survey can infer preview-only from matching dimensions alone

`tools/PhotoReview.Benchmark.Cli/RawSurvey.cs:358-367`. `WicDecodedPreviewOnly` is set when WIC dimensions equal the largest embedded JPEG, without requiring a RAW format or comparing with sensor dimensions. A RAW whose sensor dimensions, largest embedded JPEG, and WIC result are equal is marked preview-only even though equality does not show that WIC used a reduced preview. This can distort the WIC/LibRaw coverage conclusion. Existing synthetic JPEG coverage does not assert the classifier path. Static report-accuracy finding; no benchmark was run. Limit the inference to RAW and distinguish preview/sensor dimensions, or report ambiguity.

### R21 — P2 candidate — Localization can throw from a formatting argument

`src/PhotoReview.Core/Localization/LocTemplate.cs:110-144`. `Render` accepts arbitrary `LocArg.Value` and invokes `IFormattable.ToString`/fallback `ToString` without catching formatter exceptions. A custom object that throws can escape into UI localization. `Render_HostileArguments_DoNotThrowForFormatting` claims the case but its helper only returns null. Current source/test inspected; static only. Use a documented safe fallback for non-fatal formatting exceptions and add a throwing formatter regression.

### R23 — P2 candidate — Settings load can misclassify and repeat a throwing Changed callback

`src/PhotoReview.Core/Settings/SettingsStore.cs:120-122,160-170,181-193,195-218`.

`Save` invokes `Changed` after the durable write, but repaired-config `TryPersistRepairs` catches `IOException`/`UnauthorizedAccessException` around the entire call. A subscriber that throws one of those exceptions is therefore logged as a persistence failure, then `Load` invokes `Changed` again after the try. First-run/default creation wraps `Save` the same way and repeats the callback after treating it as a failed write; the second invocation can escape. The file write may already have succeeded. Static call-flow review only; no test/runtime execution. Restrict the catch to persistence operations or separate notification from write, and add throwing-subscriber tests for repaired and first-run paths.

### R24 — P2 candidate — Test fixture cleanup can recursively delete a caller-owned directory

`tests/PhotoReview.TestSupport.Windows/Fixtures/PhotoFolderBuilder.cs:46,60-62,251-273`. `BuildFolder` accepts `tempDir` and stores that path as `_cachedFolder`; process-exit cleanup and `Dispose` later recursively delete the whole path. Passing an existing directory can therefore remove files that the helper did not create. Static-only review; no directory was created or deleted. Always create a unique owned child or track created files, and delete only paths proven to belong to this helper.

### R25 — P2 candidate — Shared temp sweep deletes matching directories by age alone

`tests/PhotoReview.TestSupport.Windows/Fixtures/PhotoFolderBuilder.cs:210-248`. `PurgeStaleTemporaryFolders` enumerates shared-temp `PhotoReview-TC01-*` directories and recursively removes any older than six hours without an ownership marker or lock. A long-running test or another process using a matching path can lose its files when this helper starts. Static-only review; no directory was swept. Remove cross-run recursive sweeping or require an owned marker/lock and a conservative stale-owner check.
### R26 — P1 candidate — A file can outgrow Recycle Bin capacity after preflight and be deleted by the shell

`src/PhotoReview.Core/FileActions/FileActionService.cs:155-160,204-224,560-594`; `RecoveryRetryService.cs:163-190,222`; `src/PhotoReview.Platform.Windows/WindowsRecycleBin.cs:45-54`; `RecycleBinCapacity.cs:102`.

Single Recycle checks the current file size before journaling, then calls `SendToRecycleBin`; group and recovery paths check the volume aggregate before later sending members. The Windows backstop re-evaluates with `fileSize: null`; `RecycleBinCapacityGuard.EvaluateCore` returns `Fits` before the size-capacity arithmetic. If a file grows beyond the configured bin capacity between the size-aware check and the shell call, `FileSystem.DeleteFile(...SendToRecycleBin)` runs without confirmation and may delete it permanently while the journal records Recycle. Static race trace only; no shell or Recycle Bin call ran. Revalidate the captured size/identity immediately before mutation or otherwise prevent the unchecked shell path; add a deterministic fake mutation between capacity preflight and send. `CaptureGroupActionRollbackTests` cover only the sized preflight; they do not mutate file size after preflight or exercise the null/unknown-size backstop.

### R27 — P2 candidate — RAW sample fetch removes a caller file before download succeeds

`tools/fetch-raw-samples.ps1:397-407,430`. Arbitrary `-TargetDir` accepts an existing same-name file whose hash differs from the manifest, removes it, then downloads the replacement. A network/download failure after removal loses the caller's file. Restrict the target to a dedicated owned corpus directory or stage and verify the new file before replacing. Static script review only; no target files were touched.

### R28 — P2 candidate — fixture-unchanged guard misses same-size replacements

`Test-FixtureUnchanged` in `tests/PhotoReview.TestSupport.Windows` compares file count and aggregate byte size only. Renaming or replacing fixture files while preserving count and total bytes passes as unchanged. Compare normalized paths and per-file identity/hash metadata. Static review only; no fixture was modified.

### R29 — P3 candidate — Release notes may call an older release the first

`.github/workflows/release.yml` lists only 100 releases and does not fail closed when `gh release list` fails. An older predecessor outside the first page, or a transient API failure, leaves the previous-release value empty and generates first-release text. Paginate/check the command result and distinguish an empty successful result from lookup failure. Workflow read only; no release workflow ran.

### R30 — P2 candidate — Concurrent benchmark CLI runs can overwrite the same report

### R32 — P2 candidate — Relative file actions from a drive-root folder are rejected

`src/PhotoReview.Core/FileActions/ActionDestinationPolicy.cs:73-87`; `ActionDestinationRootTests.cs:28-35,51-63`. With a physical filesystem, `ResolveRealPath` can return a canonical root such as `C:\`. Trimming the ending separator preserves the root, then the containment prefix appends another separator (`C:\\`), which does not match a child path such as `C:\selected\photo.jpg`. A relative Move/Copy is rejected as outside the source folder. Existing tests use an in-memory resolver returning `C:` and miss this Windows-root case. Normalize root prefixes or compare a relative path; add a deterministic policy test with a canonical root result. Static review at `18aa6eb6`; no file operation ran.


`tools/PhotoReview.Benchmark.Cli/BenchmarkCliArguments.cs:107-114`; `Program.cs:70-72`. The default report directory uses second-resolution timestamp naming; invocations in the same second share the directory and both write `summary.json`, so one result overwrites the other. Add a unique invocation suffix or atomic directory creation; cover same-second/concurrent calls. Static trace only; no report was written.
### Test-only and oracle findings retained at current head

- `TurboJpegGuardMutationTests.ReadAllBytes_LengthExactlyTheLimit_PassesTheLimitCheck` attempts to allocate `MaxSourceBytes` (about 2 GiB) in a default-gate test. Do not run it during this review; replace the allocation with a seam or isolate the resource test.
- `SourceBytesCacheTests.GetOrRead_ReadsOnCallingThread` uses an unbounded `Thread.Join()`; a deadlock can outlive its test bound and rely on the outer suite watchdog. Static only; not run.
- `WarmNavigationReadBoundsTests` claims no duplicate presentations but uses a no-op sink and checks only final state; use a recording sink to assert the sequence/count (APP-T19). Static only; not run.
- `MainWindowBehaviorTests.Fit` passes `MouseButton.Right` for a negative double-click case, but its helper always raises `PreviewMouseLeftButtonDownEvent`; the case never exercises the right-button route (APP-T20). Route the event from the requested button and cover right/middle behavior per contract. Static only; not run.
- `FolderLoadCoordinatorTests.PerfMarks.LoadAsync_WithRawSupportOff_TheListingNeverAcceptsSidecarFiles` has a bell control character embedded in the would-be sidecar path, so it is not a child of the scanned folder and the count assertion cannot prove sidecar exclusion (APP-T21). Use a normal child path and assert that the fake contains it. Static only; not run.
- `InstanceForwardPipeTests.Client_OwnerAnswersTooLate_ReportsUnknown` has no latch proving the server handler began before the client deadline; `Unknown` can pass without a late owner answer (APP-T22). Signal handler entry, hold the response until after timeout, then release it and assert the outcome. Static only; not run.
- `ImagePresenterTests.Adversarial.PerPathGatedDecoder.Decode` ignores the boolean result of `Task.Wait(20s)`, so a never-opened gate becomes a successful fake image instead of failing the test (APP-T23). Assert the wait result or throw a timeout exception. Static only; not run.
- `FolderLoadCoordinatorTests.Races` uses unbounded `ManualResetEventSlim.Wait` gates released only along the happy assertion path; an earlier failure can leave a worker blocked (APP-T24). Release gates in `finally` and bound waits. Static only; not run.
- `FolderLoadSkippedFilesTests` catches ACL setup failures and returns from `[Fact]` methods, so xUnit reports pass although no behavior assertion ran (APP-T25). Use explicit skip reporting with a reason. Static only; not run.
- `FolderLoadCoordinatorTests.Probe_DoneButNotYetApplied_ThenDisposed_IsDropped` can leave its foreground worker alive after the bounded `Join` times out; releasing the probe does not necessarily unblock that worker (APP-T10 risk). Add deterministic worker shutdown or a process-level hard bound. Static only; not run.
- `PowerShellSafetyGuardTests.RunPowerShell` uses unbounded `Process.WaitForExit()`; a hung child process can stall the test suite (APP-T26 risk). Add a timeout, kill the process tree, drain output, and fail with diagnostics. Static only; not run.
- `PlatformGapTests` uses `.First()` over supposedly unused D:–Z: drive letters; if all are mounted, the test throws before its Unknown assertion and does not report an explicit skip (APP-T27). Use an injectable no-volume provider or an explicit skip. Static only; not run.
- `BenchmarkCliHardeningTests` supplies `OperationCanceledException` for both cancellation and OOM negative classification cases; use `OutOfMemoryException` for the latter (APP-T18). Static only; not run.
- `DecoderRegistrationTests.ImageDecoderFactory_HasNoReflectionLoading` scans source text, contrary to the no-source-text-test rule; use behavioral or IL inspection instead. Static only; not run.
- `PerfCsvListenerMappingTests.Writer_FlushesAfterOneSecond` and Core timing tests use fixed pauses before assertions. This conflicts with the repository's no-fixed-delay-assert rule and can flake under load; static review only.
- `UpdateCheckerBoundaryTests.DeclaredLengthOverMax_IsRejected` checks only the returned failure with `ByteArrayContent`; it does not prove the body was rejected before consumption. Add a read-counting or throw-on-read body.
- Two App presentation test helpers create a timeout source but do not pass its token to the barrier await; the requested timeout does not bound a missing presentation.
- Existing baseline test-oracle gaps APP-T04/APP-T05 and imaging wrapper resource leaks are described in the lane notes; their current status must be reconciled by the active agents before this report is final.
- `DecodeOriginal_RunsAtMostOneFullResolutionDecodeAtATime_AndDropsCancelledWaiters` uses a 300 ms negative wait as an absence proof; slow scheduling can hide an illegal second decode. Replace it with an explicit signal that the waiter reached the gate. Static only; not run.
- `FallbackExceptionCoverageTests` routes an injected OverflowException using a 32x24 JPEG; it does not cover hostile dimensions or WPF fallback memory admission for R16.
- `ImagePresenterTests` cover ordinary cancellation/supersession but do not exercise APP-P01's disposed-CTS Token getter interleave.
- Core recovery tests do not gate a foreign file appearing at a pending Undo-Recycle source between initial check and retry execution (R15 race extension); an equal-size/different-timestamp fingerprint case is also absent.

### R01 — P1 — Copy cleanup can delete a foreign destination

`src/PhotoReview.Core/FileActions/FileActionService.cs:186-187,543-544,654`; `RecoveryRetryService.cs:271-275`; `PartialDestinationCleanup.cs`.

Ownership is set before `TryCopyNew` has created anything. If another process creates the destination and the source vanishes before the copy opens its source, `TryCopyNew` throws FileNotFoundException instead of returning collision=false. Single/recovery cleanup infers ownership from shorter length; group compensation deletes the in-flight destination outright. A foreign file can therefore be deleted. Safe fake-file-system repro covers all three paths, 3/3 observed-bug assertions passed on the pinned baseline. It never uses real photos or the real bin. Existing destination-collision tests keep the source present and miss this pre-creation exception. A separate own-temp Windows File.Copy probe confirmed that missing-source plus existing-destination throws FileNotFoundException (HResult -2147024894), leaving the foreign file intact before application cleanup.

Remedy: propagate proof of successful creation from the copy implementation; only clean up a destination owned by this operation. Length or preflight absence cannot establish ownership. Add regressions that assert the foreign file survives.

#### R01 residual — Copy rollback can delete a foreign replacement

Fresh current-head Core review also confirmed the same proof gap in `RecoveryRetryService.cs:274-291`: after `CopyCreationProof` succeeds, catch cleanup delegates to the size-only `PartialDestinationCleanup.RemoveIfPartial`, so a shorter foreign replacement arriving before cleanup can be deleted. Static trace at `18aa6eb6`; no file was changed.

`src/PhotoReview.Core/FileActions/FileActionService.cs:189,212,283-305,376-381,666-667`; `PartialDestinationCleanup.cs:17-28`.

After a group Copy destination passes `VerifyGroupDestination`, the member is added to `done`. If a later member fails, `RemoveCreatedCopies` deletes each completed destination whenever it still exists and has the expected length. A foreign same-size replacement is therefore treated as operation-owned and can be deleted. On the single-Copy path, `TryCopyNew` leaves a creation-proof bool true after success, but `RemovePartialCopy`/`PartialDestinationCleanup.RemoveIfPartial` later deletes any current destination whose length is smaller than the source. If the completed copy is replaced by a shorter foreign file before verification/journal cleanup, that foreign file can be deleted too. #290 proves the original creation event but does not preserve file identity through cleanup. This is a static race trace; no file was replaced or deleted. Preserve destination identity across cleanup or leave ambiguous destinations for Recovery instead of deciding ownership from length; add deterministic fake replacement-before-rollback regressions for both paths. The current `FileActionServiceGroupMutationGapTests` replacement case only uses a longer foreign file; it does not cover the equal-length replacement that exercises the group rollback length check.

Startup reconciliation has the same size-only weakness for a prepared single Move/Copy: `OperationJournal.ReconcileOne` sets `destinationMatches` by comparing only `destinationStat.Length == pending.Size`. An equal-length replacement at the destination can be marked Committed even when its timestamp differs from the prepared fingerprint. Existing tests cover a different-length Copy mismatch but not an equal-size replacement. Static current-source trace; no file was changed.

### R02 — P1 — Queued file commands act on a newly opened folder

`src/PhotoReview.App/ViewModels/MainViewModel.cs:534-547,1193-1197` (`RunActionAsync`, `RecycleAsync`, `MoveOrCopyToFolderAsync`). A closure queued in folder A reads the current catalog only when dequeued. Block Move #1, enqueue Move #2, open B, release #1: #2 moves B's photo. Desired-behavior repro failed with "An action queued in folder A moved folder B's photo." Same design exposes queued recycle commands, although the runtime repro used Move. Existing tests cover same-folder FIFO or a single action during folder switch.

Remedy: capture folder-load identity at submission and reject stale queued commands; preserve live selection for repeated commands within the same folder. The action generation changes for each action and cannot serve as folder-load identity.

### R03 — P1 — Publish guard misses an ancestor junction

`tools/Publish-Guard.ps1:38-61,77-85`. `Resolve-FinalTarget` follows a link only at the leaf. An approved-root child with an ancestor junction can resolve to an unmarked external `publish` directory and still pass the lexical ownership check. `verify-all.ps1` then has authorization to recursively remove that external output.

Safe own-fixture probe returned `Refused=false`, `MarkerPresent=false`; no destructive publish action was executed. Its junction and temp fixtures were cleaned up. Current self-test covers a junction at the publish leaf, not an ancestor. Remedy: resolve every existing path component, then check actual destination/root containment and marker ownership before deletion.

### R04 — P2 — A superseded Fit overrides a newer zoom

`src/PhotoReview.App/Input/PointerInputController.cs:223-224,234-235,257-258,280-285`. The first zoom pass recognizes a newer operation and returns, but callers still run scrollbar correction, which creates a fresh version and reapplies the obsolete Fit. Deterministic held-render-yield repro expected zoom 2.0 and observed 0.4. Initial probe had a fake collection-modification error; after disabling held yields before releasing them, the assertion reached the actual bug. Remedy: carry the original operation success/version through correction and reject stale correction.

### R05 — P2 — Non-finite font configuration fails startup repair

`src/PhotoReview.Core/Settings/SettingsNormalizer.cs:195-199`; `SettingsStore.cs:121,181-190`. JSON string `"NaN"` is accepted, `Math.Clamp` retains NaN while recording a repair, and repair persistence throws ArgumentException during serialization. `Load` does not catch that exception. Worker fake-store repro passed the observed-bug assertion; lead independently reran the two settings probes against the worker-built baseline DLL (2/2 passed, 256 ms). App font-size input also lacks an explicit finite check. Remedy: normalize non-finite doubles to a documented default before clamping/serialization and reject NaN/Infinity at UI input.

### R06 — P2 — Output memory refusal enters unguarded decoder fallback

`src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:297`; `TurboJpegDecoder.cs:517`; `FallbackImageDecoder.cs:91`; `ImageDecoderFactory.cs:88`. Output headroom refusal throws InvalidDataException, which fallback classifies as a codec error. Default WIC composition then calls WPF, whose decoder has no equivalent headroom admission. Source-slice probe with real guard/classifier and stub decoders reported fallback calls=1; it does not prove an actual OOM or native allocation. Remedy: distinguish admission refusal from corrupt-image failure and preserve it across fallback, or enforce the same admission in every backend.

### R07 — P2 — Cache temporary cleanup deletes a file it did not create

`src/PhotoReview.Imaging/Caching/AtomicCacheFile.cs:43-60`. When `CreateNew` throws because a temporary path already exists, `finally` still deletes that path. Source-slice own-fixture probe: CreateNew threw=True, original temp survives=False, target exists=False. Default GUID collision is rare; caller-supplied temporary names/test seams make the ownership contract visible. Existing pre-existing-temp oracle checks target absence but misses temp survival. Remedy: track successful stream creation and clean up only the operation-owned temporary file.

### R08–R10 — P2 — Source-traced App defects; runtime regressions pending

- R08: `FileActionController.cs:401-410,423-432,446-454` checks folder once before awaiting next-image presentation, then writes old action failure/status or unverified-Move session state into a newly opened folder. Recheck folder identity after each await. Success warning path already does so.
- R09: `MainViewModel.cs:581-599` reopens Undo's source folder after a concurrent user folder open, even when controller correctly drops stale catalog changes. Recheck caller's folder-load identity before reopen and subsequent status update. Existing mid-Undo test uses same-folder Move and misses Recycle/prior-folder Move.
- R10: `ImagePresenter.cs:130-151,777-791` clears `_presentedFilePath` on same-file full-resolution bitmap swap. The next navigation skips enabled Fade because previous identity is null. Preserve file identity during same-source swaps. No visual acceptance was performed.

### R11 — P1 — Unknown drive type can pass the permanent-delete guard

`src/PhotoReview.Platform.Windows/WindowsRecycleBin.cs:77-85,235-255`; callers: `src/PhotoReview.Core/FileActions/FileActionService.cs:140-142,598-624` and `RecoveryRetryService.cs:137-160,204-213`. `QueryDriveType` returns null when `DriveInfo` throws `ArgumentException`, `IOException` or `UnauthorizedAccessException`; `CanRecycle` reduces null and known non-fixed volumes to the same `false`. `DeletePermanently` rejects only `true`, so a fixed-drive photo can reach `File.Delete` when the drive query transiently fails, provided permanent deletion is enabled/confirmed. Retry repeats the same ambiguous bool check; the journal's prior `Permanent` bit and current allow-permanent setting do not establish that the current drive is known unsupported. This is source-traced conditional data loss, not runtime-reproduced; no real delete, Recycle Bin operation, or native API was run. Remedy: use a tri-state eligibility result and allow permanent deletion only for positively identified unsupported drive types; fail closed on unknown, including retry preflight and execution. Add fake drive-query tests for unknown, fixed, and known unsupported types, asserting unknown never invokes permanent deletion.

### R12 — P3 — Test report labels cumulative test duration as wall time

`tools/verify-all.ps1:170-186`. `Generate-TestReport` sums each TRX test's `DurationSeconds` and prints the result as “Per-Project Wall Time” with a 60-second warning threshold. xUnit may execute collections concurrently, so summed test time can exceed elapsed project time and produce a misleading wall-time warning. This is a source-traced tooling/reporting issue, not runtime-verified. Use TRX run-level elapsed time or label the metric as cumulative test duration.

## Test harness and oracle findings

- APP-T03 — `tests/PhotoReview.App.Tests/MainViewModelFileActionTests.cs:991-1003`: `WaitForPresentationCountAsync` creates a timeout source but awaits a barrier task without applying that token or a timed wait. Missing presentation can therefore wait until the outer test-run hang guard, not the helper's requested timeout. Static review only; no deliberate hang was run.
- APP-T04 — `tests/PhotoReview.Integration.Tests/BenchmarkImageExecutorTeardownTests.cs`: the test seeds a cache entry but does not start or hold a prune pass, so its elapsed-time assertion does not exercise the behavior claimed by its name. Add a deterministic prune barrier and assert the prune starts before checking disposal latency.
- APP-T05 — `tests/PhotoReview.Integration.Tests/RecoveryPathPanelTests.cs`: the clipboard test only asserts no exception while another thread owns the clipboard; a no-op handler satisfies that assertion. It does not prove the expected clipboard call or COM failure handling. The real clipboard test was not run.
- IMG-R04 — `tests/PhotoReview.Imaging.Tests/Decoding/WpfDecoderGapTests.cs` and `WicDirectFaultInjectionTests.cs`: two fault-stream wrappers do not dispose the owned inner stream; TempRoot suppresses directory cleanup errors, so fixture files can remain until GC. This is test-fixture resource leakage, not production image-data loss; source-slice probe confirmed wrapper disposal leaves the inner stream readable.
- TEST-R05 — `tests/PhotoReview.Imaging.Tests/Decoding/ErrorHandlingReviewDecodingTests.cs::HugeSourceFile_IsRefusedBeforeReading`: the fixture is 128 MiB + 1 KiB and categorized HotPath. If its guard regresses, the decoder may read that large file before the expected refusal, adding avoidable disk/memory pressure. Static warning only; this test was not run or mutated.

Further per-test oracle gaps (assertions that do not reach the named code path, prove only a subset of behavior, or rely on permissive predicates) are listed next to their callable rows in [functions.tsv](functions.tsv) and the lane reports. They are not counted as production defects.

### Candidates that need controlled evidence

- `PreviewImageService.cs:262-283`: two persistence workers across cache epochs can write the same cache path; stale worker cleanup may delete a newer worker's file. Cache performance/completeness impact, not source-photo deletion; needs controlled two-worker interleaving.
- `SessionWriter.cs:76-85,94-105,202-238`: bounded wait covers lock acquisition/in-flight writes, but an acquired lock performs synchronous `_store.Save`; a newly stalled save may exceed the shutdown budget. Needs a blocking fake with event-driven release and proof of UI call path; not claimed runtime-confirmed.
- `KeyboardZoomStepPercent` is not normalized (worker observed int.MaxValue survive parsing); negative values reverse keyboard zoom direction at the App call site. Bounds/desired default and a regression should be defined before changing behavior.

## Validation and continuation

[Validation record](validation.md) separates baseline gate runs, added repro tests and source-slice probes. Evidence scripts/sources are in [evidence](evidence/README.md); .cs.txt files are deliberately outside compilation. Workers did not alter production source. Lead removed all temporary compiled repro tests before the clean baseline gate.

Next: finish the Core ledger, semantically review all test bodies and lead-owned source/tooling/CI/docs, reproduce static candidates, then implement findings in separately authorized fix work. Keep the pinned baseline and stable IDs; after master changes, create an explicit delta inventory/review rather than treating stale line numbers as current. Preserve worker worktrees for continuation; do not report this audit as DONE or move it into HISTORY.

### R34 — P2 candidate — Bandwidth-only slow-link setting is reported but not applied

`tools/PhotoReview.Benchmark.Cli/PerfSession.cs:152-155,289-315,535-538,930-980`. The CLI accepts and reports `--slow-link-bandwidth-mbps` without latency, but `RunIterationAsync` enables both slow-link wrappers only when latency is non-null. A bandwidth-only run therefore claims a cap in metadata without applying it. Reject bandwidth without latency or activate the wrapper and align validation. Static only; no benchmark ran.

### R35 — P2 candidate — Synthetic action scenario can pass without invoking its action

`tools/PhotoReview.Benchmark.Cli/PerfSession.cs:346-353`; `src/PhotoReview.App/MainWindow.xaml.cs:78-81`; `src/PhotoReview.App/Input/ShortcutRouter.cs:78-87`; `src/PhotoReview.App/Coordinators/FileActionController.cs:62-68`. `MainWindow` builds its router before the CLI replaces settings actions; the router retains stale shortcut indices and is not rebuilt because the CLI assigns the list directly. For a default action key such as F3, the router can return index 1 while the new list has one item, so `RunActionAsync` returns without applying an action and the benchmark can report success. Install synthetic actions before window construction or rebuild routing, and assert resulting action progress/output. Static only; no runtime/test ran.

### PW-01 — P2 candidate — Windows 10 can fail the default titlebar test

`tests/PhotoReview.Integration.Tests/PlatformPrimitivesTests.cs:82-86`; `src/PhotoReview.App/WindowsDarkTitleBar.cs:19-20`.

The test unconditionally asserts caption-color acceptance for Windows 10 20H1+ and Windows 11, but `DWMWA_CAPTION_COLOR` is supported only on Windows 11 and `TrySetCaptionColor` returns false where unsupported. On supported Windows 10, product behavior is correct but the default Integration test can fail. Make the assertion OS-capability-aware or assert no throw on unsupported systems. Static oracle review, not executed.

### R33 — P2 candidate — Localization generator accepts a catalog the runtime rejects

`src/PhotoReview.Localization.Generator/FlatJsonReader.cs:127-131,219-240`; `TrGenerator.Execute`; `src/PhotoReview.Core/Localization/LanguageCatalog.cs:88-109,136-139`; `tests/PhotoReview.Core.Tests/Localization/FlatJsonReaderDifferentialTests.cs`; `LanguageCatalogRobustnessTests.cs`.

For a catalog whose ignored `_meta` object contains an unpaired surrogate string, `FlatJsonReader.SkipValue` decodes the string but skips the unpaired-surrogate check applied to catalog keys and values. `TrGenerator.Execute` can therefore generate members, while `LanguageCatalog.TryParse` materializes metadata strings with `JsonElement.GetString()` and catches the invalid surrogate, rejecting the catalog. This breaks the generator/runtime validity parity contract. Existing differential cases do not pin this nested metadata case; current tests separately cover malformed entry values and normal metadata. Add a deterministic differential case and reject invalid surrogate strings in runtime-consumed metadata, or align both parsers' contract. Static trace only; no build/test ran, and no source was changed.


### R38 — P2 test-oracle gap — journal stress test can miss lost acknowledged entries

`tests/PhotoReview.Core.Tests/FileActions/JournalCompactionTests.cs:394-475`. `TryCompact_StressWithConcurrentWriters_NoTornOrLostRecords` deserializes all remaining lines but discards their IDs. For acknowledged entries it checks only absence from pending/failed; a compactor that drops a committed append can satisfy that assertion. It also adds each acknowledged ID to a set without asserting the insertion succeeds or that the ID exists in the journal. Assert that each acknowledged ID is persisted exactly once. Static review only; no tests ran.

### R42 — P2 test-liveness gap — journal concurrency tests can block before their timeout

`tests/PhotoReview.Core.Tests/FileActions/JournalConcurrencyTests.cs:36-194`. `ManyWritersOneFile_NoTornOrLostAcknowledgedRecords` and `ReconcileWhileOtherProcessCommits_LatestStateStaysCommitted` use unbounded `Barrier.SignalAndWait`; if a participant exits before reaching the barrier, other foreground threads can remain blocked. `ManySimultaneousActions_ExactlyOneRuns` awaits `entered.Task` without a deadline before its bounded wait, so a failure to enter the Move hangs the test; if the later wait fails, `release` is not set in `finally`. Use bounded barrier/task waits and cleanup that always releases and joins workers. Static review only; no tests ran.

### R41 — P2 test-oracle gap — older-build downgrade test uses BEL instead of intended fixture paths

`tests/PhotoReview.Core.Tests/FileActions/JournalGroupDowngradeTests.cs:96-97`. `Reconcile_StaleReconcileFailedFollowsOlderBuildCommitted_StillRepairsTheGroupLine` seeds `C:\selected` + U+0007 + `.jpg` and `C:\photos` + U+0007 + `.cr2`, while `GroupMembers` refer to `a.jpg` and `a.cr2`. The test therefore does not create the claimed state (first member moved, second source present) and may pass without exercising the stale-reconcile path against that state. Replace the control characters with literal `a`. Static review only; no tests ran.

### R40 — P2 test-oracle gap — torn journal delta can be dropped without failing the test

`tests/PhotoReview.Core.Tests/FileActions/OperationJournalCompactionBoundaryTests.cs:193-214`. `TryCompact_TornLineAppendedMeanwhile_GetsANewlineAndBytesAfterCountsIt` verifies the Compacted outcome, a final LF, and `BytesAfter == actual length`, but never asserts the torn delta bytes remain. An implementation that discards `{"Id":"torn","Ty` and writes only a newline can pass. Assert the exact torn bytes are present before the line terminator. Static review only; no tests ran.

### R39 — P2 test-liveness risk — a failed writer join can leave the compactor running

`tests/PhotoReview.Core.Tests/FileActions/JournalCompactionTests.cs:394-475`. If a writer `Join` assertion fails, `stop` is never set and the foreground compactor can loop indefinitely. Put the stop signal and bounded joins in `finally`, and ensure a failed test cannot leave foreground workers keeping the test process alive. Static review only; no tests ran.

### R37 — P2 test-liveness gap — mutation worker can hang before the bounded join

`tests/PhotoReview.Core.Tests/FileActions/RecoveryRetryServiceTests.cs:277`. `RetryMoveOrCopyAsync_RunsMutationOffCallerThread` awaits `done.Task` without a deadline before reaching its 10-second `Join`; if the worker stalls, the bounded join never runs and the test relies on the outer test-run watchdog. Bound the task wait (for example, `WaitAsync`) and release/clean up/join the worker in `finally`. Static test review only; no test ran.

### R36 — P3 test-oracle defect — unequal values are asserted to have unequal hashes

`tests/PhotoReview.Core.Tests/FileActions/OperationJournalMutationTests.cs:136-141`. `GetHashCode_EntriesDifferingOnlyInTheMembers_DifferInTheirHash` asserts unequal `JournalEntry` instances must produce different hash codes. The equality/hash contract only requires equal objects to share a hash; unequal objects may collide. A valid hash implementation change could therefore break this test despite correct behavior. Remove the unequal-hash assertion and retain the equal-values/same-hash assertion. Static test review only; no test ran.
