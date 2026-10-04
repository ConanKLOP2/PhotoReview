# Function review — current-head checkpoint (2026-10-04, incomplete)

Current pinned head: `18aa6eb64ba48666de620937211e0782dd6131c1` (`origin/master`). The audit began at `5d291076` and has been reconciled through PRs merged since then. Review branch: `codex/review-all-20261004`; [PR #284](https://github.com/ConanKLOP2/PhotoReview/pull/284), open and not merged. This is a documentation/review PR; it makes no production changes. App/Integration and Core/Platform/tooling lanes are continuing semantic review; Imaging is complete and Architecture/TestSupport remains unassigned. The inherited model names are unavailable.

## Coverage contract

The current Roslyn inventory covers 16,799 callable bodies in 1,068 tracked C# files: methods/constructors/operators with bodies, explicit accessors, expression-bodied properties/indexers, local functions and lambdas. Declaration-only interfaces/extern members, implicit record members, XAML event declarations and the outer top-level statement body are excluded. Nested bodies have individual rows. This is an inventory, not semantic sign-off.

The merged-PR reconciliation and bounded source/test delta findings through the pinned head are summarized in [current-delta-20261004.md](current-delta-20261004.md). The current complete callable review remains in progress.

[functions.tsv](functions.tsv) records ID, path, current line/end line, kind, signature, status and rationale. `STATIC-ONLY` means a saved semantic source review, including inherited enclosing-function rationale for small helpers/lambdas; it does not mean runtime PASS. `ISSUE` identifies affected source bodies, not a count of independent bugs. `UNREVIEWED` includes partially read functions whose completed rationale was not saved. Broad-suite pass does not change these statuses. The current ledger has 13,144 `STATIC-ONLY`, 86 `ISSUE`, and 3,569 `UNREVIEWED` rows after merging available App and current-head Core batches. It carries forward unchanged-file reviews, applicable wave-two rows and completed current-head batches; active review lanes have not yet contributed all of their remaining coverage.

The original baseline ledger reported 1,203 App and 694 Imaging-family source bodies reviewed. Those figures are historical and are not current-head coverage. App, Core/tooling, and Imaging agents are reviewing the remaining current-head rows in separate ownership lanes. No lane may be marked complete until its exact callable coverage and remaining count are reconciled against this inventory. [App baseline details](app.md).

| Area | Production bodies | Test bodies | Total | Status |
|---|---:|---:|---:|---|
| App + Integration | 1,229 | 5,226 | 6,455 | 4,629 rows reread; 1,826 remaining |
| Core + Platform + tooling | 1,560 | 3,487 | 5,047 | 1,834 reread in current-head scan (1,433 Core-owned + 401 overlapping Integration rows) |
| Imaging + Raw + LibRaw + TurboJpeg | 716 | 4,279 | 4,995 | 716 production + 4,279 Imaging.Tests reread; complete; 189 related Integration/App/Architecture rows also read |
| Architecture.Tests / TestSupport / TestSupport.Windows | — | 298 | 298 | 8 Architecture decoder rows read; TestSupport and remaining Architecture rows unreviewed |

[powershell-functions.tsv](powershell-functions.tsv) additionally inventories 52 named PowerShell functions in 15 scripts; script-level statements and CI YAML are not callable rows. The ancestor-junction finding in `Publish-Guard.ps1` was fixed by #292 and must not be carried as open. Exhaustive tooling/CI/docs semantic coverage is pending. Native ABI, real WPF interaction, RAW corpus, NAS/performance, actual Recycle Bin, and full mutation campaign are not validated by this review.

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

### R20 — P2 candidate — RAW survey can label an ordinary JPEG as preview-only

`tools/PhotoReview.Benchmark.Cli/RawSurvey.cs:358-367`. `WicDecodedPreviewOnly` is set when WIC dimensions equal the largest embedded JPEG, without requiring a RAW format or comparing with sensor dimensions. An ordinary JPEG's sole embedded stream naturally matches its full WIC dimensions, so the report is false. Existing synthetic JPEG coverage does not assert this flag. Static report-accuracy finding; no benchmark was run. Limit the inference to RAW and distinguish preview/sensor dimensions, or report ambiguity.

### R21 — P2 candidate — Localization can throw from a formatting argument

`src/PhotoReview.Core/Localization/LocTemplate.cs:110-144`. `Render` accepts arbitrary `LocArg.Value` and invokes `IFormattable.ToString`/fallback `ToString` without catching formatter exceptions. A custom object that throws can escape into UI localization. `Render_HostileArguments_DoNotThrowForFormatting` claims the case but its helper only returns null. Current source/test inspected; static only. Use a documented safe fallback for non-fatal formatting exceptions and add a throwing formatter regression.

### R23 — P2 candidate — Settings load can misclassify and repeat a throwing Changed callback

`src/PhotoReview.Core/Settings/SettingsStore.cs:120-122,160-170,181-193,195-218`.

`Save` invokes `Changed` after the durable write, but repaired-config `TryPersistRepairs` catches `IOException`/`UnauthorizedAccessException` around the entire call. A subscriber that throws one of those exceptions is therefore logged as a persistence failure, then `Load` invokes `Changed` again after the try. First-run/default creation wraps `Save` the same way and repeats the callback after treating it as a failed write; the second invocation can escape. The file write may already have succeeded. Static call-flow review only; no test/runtime execution. Restrict the catch to persistence operations or separate notification from write, and add throwing-subscriber tests for repaired and first-run paths.

### Test-only and oracle findings retained at current head

- `TurboJpegGuardMutationTests.ReadAllBytes_LengthExactlyTheLimit_PassesTheLimitCheck` attempts to allocate `MaxSourceBytes` (about 2 GiB) in a default-gate test. Do not run it during this review; replace the allocation with a seam or isolate the resource test.
- `SourceBytesCacheTests.GetOrRead_ReadsOnCallingThread` uses an unbounded `Thread.Join()`; a deadlock can outlive its test bound and rely on the outer suite watchdog. Static only; not run.
- `WarmNavigationReadBoundsTests` claims no duplicate presentations but uses a no-op sink and checks only final state; use a recording sink to assert the sequence/count (APP-T19). Static only; not run.
- `MainWindowBehaviorTests.Fit` passes `MouseButton.Right` for a negative double-click case, but its helper always raises `PreviewMouseLeftButtonDownEvent`; the case never exercises the right-button route (APP-T20). Route the event from the requested button and cover right/middle behavior per contract. Static only; not run.
- `FolderLoadCoordinatorTests.PerfMarks.LoadAsync_WithRawSupportOff_TheListingNeverAcceptsSidecarFiles` has a bell control character embedded in the would-be sidecar path, so it is not a child of the scanned folder and the count assertion cannot prove sidecar exclusion (APP-T21). Use a normal child path and assert that the fake contains it. Static only; not run.
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

`src/PhotoReview.Core/FileActions/FileActionService.cs:189,212,283-305,376-381,666-667`; `PartialDestinationCleanup.cs:17-28`.

After a group Copy destination passes `VerifyGroupDestination`, the member is added to `done`. If a later member fails, `RemoveCreatedCopies` deletes each completed destination whenever it still exists and has the expected length. A foreign same-size replacement is therefore treated as operation-owned and can be deleted. On the single-Copy path, `TryCopyNew` leaves a creation-proof bool true after success, but `RemovePartialCopy`/`PartialDestinationCleanup.RemoveIfPartial` later deletes any current destination whose length is smaller than the source. If the completed copy is replaced by a shorter foreign file before verification/journal cleanup, that foreign file can be deleted too. #290 proves the original creation event but does not preserve file identity through cleanup. This is a static race trace; no file was replaced or deleted. Preserve destination identity across cleanup or leave ambiguous destinations for Recovery instead of deciding ownership from length; add deterministic fake replacement-before-rollback regressions for both paths.

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
