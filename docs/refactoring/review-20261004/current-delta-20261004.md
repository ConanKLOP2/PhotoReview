# Current-head delta review — 2026-10-04

Pinned comparison head: `origin/master` `8952067db8fd820afe47e112c202c75c0cae181b`. Static review only. No builds, tests, destructive operations, real Recycle Bin calls, large allocations, RAW corpus, native decoder execution, or GUI checks were run for this delta.

## Changes reconciled

- The App/Integration lane compared `7fe54c35..91a21240`: 42 changed paths (17 App production files, 18 App test files, and 7 Integration test/project paths; +2,744/−143). The worker inspected the changed behavior around queued actions, folder identity, Fit supersession, Undo reopen and same-source fades. Previous R01–R05 and D-02/D-04 are fixed by merged PRs #288/#298/#309. Exact changed-callable coverage is not asserted from the added method declarations alone. One overlapping-open request race remains (R14 in [WORK-REVIEW.md](WORK-REVIEW.md)); no tests were run.
- Core/PerfAnalysis/Benchmark CLI compared `91a21240..8952067d`: 42 changed paths, comprising 27 production files (15 Core, 3 PerfAnalysis, 9 Benchmark CLI) and 15 Core test files. The review covered every changed production file and the changed test additions/oracles in those 15 test files. The existing wave-two Core findings W2-FA-01/02/04/05/06/07/10 and D-06/D-07 were checked against the fixes in #310/#311/#313–#318; the initial R01 pre-creation/in-flight cases are fixed but a same-size replacement residual remains. R11 remains open. Exact body counts for this bounded delta are not claimed.
- The Imaging lane found no direct Imaging/Raw/LibRaw/TurboJpeg source or Imaging test path changed in `91a21240..8952067d`; it reviewed 3 changed PerfAnalysis callables and 4 associated test bodies. This does not mean all existing Imaging functions/tests have been read at current head.
- Current Roslyn inventory: 1,068 C# files, 16,795 callable bodies. See [functions.tsv](functions.tsv) for current lines and the per-body status. The ownership lanes continue beyond these deltas.

## Full-inventory continuation progress

These are reread callables for the current-head continuation, separate from the cumulative ledger statuses above. At the latest checkpoint, App/Integration had read 966/6,455 owned rows (5,489 remaining); Core/Platform/tooling had read 714/6,790 (6,076 remaining); Imaging had reread all 716/716 production rows and 817/4,279 Imaging test rows (3,462 remaining). Each lane is also checking scoped cross-project test callables; no lane is complete. No tests or builds were run.

## Current findings

### R01 residual — group Copy rollback can delete a same-size replacement

`src/PhotoReview.Core/FileActions/FileActionService.cs:189,212,283-305,376-381,666-667`; `PartialDestinationCleanup.cs:17-28`. #290 proves that the operation created the destination, but not that the same file is still present later. After a verified group Copy member moves into `done`, rollback deletes it based only on matching length; a foreign same-size replacement during the next member's await can be deleted. On the single-Copy path, a successful creation-proof bool remains true while `RemovePartialCopy` deletes any destination shorter than the source; a foreign short replacement before verification/journal cleanup can also be deleted. Static race traces only; no files were replaced or deleted. Preserve destination identity through cleanup or leave ambiguous destinations for Recovery, and add fake replacement-before-rollback regressions for both paths.

### R13 — P1 candidate: Benchmark CLI output can follow a symlink into a photo

`RawSurvey.ValidateMarkdownPath` checks parent containment but does not reject an existing final output reparse point (`tools/PhotoReview.Benchmark.Cli/RawSurvey.cs:116-126`). `RawSurvey.RunAsync` later writes that path with `File.WriteAllTextAsync` (`:172`). `DecoderBenchmark.RunAsync` and `RawDecoderBenchmark.RunAsync` guard the output directory but then write fixed report filenames (`DecoderBenchmark.cs:374-380`; `RawDecoderBenchmark.cs:225,232`). An output report symlink to a photo can therefore be followed and truncate the photo. This is a source-traced path, not reproduced: no symlink or target file was created or changed. Reject existing reparse-point report targets and use safe create/replace semantics; add only a temporary self-cleaning regression.

### R11 — P1: unknown drive eligibility may permit permanent deletion

`RecycleEligibility.QueryDriveType` can return null, `CanRecycle` collapses that to false, and `WindowsRecycleBin.DeletePermanently` rejects only true before `File.Delete`. `FileActionService` and `RecoveryRetryService` also use the bool result as if false meant known unsupported. #312 corrected mount-point selection and refuses unsupported volumes for recycling but did not establish a fail-closed tri-state for permanent deletion. This remains conditional on the permanent-delete setting/journal path. No delete or Recycle Bin operation was run.

### R14 — P2 candidate: a delayed folder open can replace a newer request

`OpenFolderCoreAsync` awaits ownership negotiation before advancing/checking folder-load identity. If request B is delayed in `InstanceScope.BeforeOpenAsync` and request C completes first, B can resume with `Proceed` and replace C. Duplicate-cleanup reload uses the same path. Existing tests cover a single pending open, not overlapping B/C requests. This is static-only. Capture a request identity before ownership negotiation, recheck it after the await, preserve `AfterOpen` when ownership was granted, and add a gated fake-ownership regression.

### R15 — P2 candidate: group undo reconciliation accepts any existing source path

`src/PhotoReview.Core/FileActions/OperationJournal.cs:490-498,614-620`. A prepared group Recycle undo is marked completed when each original source path exists, without comparing the saved size/last-write fingerprint. An unrelated replacement can make reconciliation commit the group while the original remains in the bin. `UndoService` and `RecoveryFileCheck` do compare the fingerprint. Existing reconciliation tests seed matching files only. Static review; add a fake mismatch case. No real Recycle Bin access.

### R16 — P2 candidate: checked WIC output math overflows into WPF fallback

`src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:238-240` throws `OverflowException` before calling `EnsureOutputFits`; `FallbackImageDecoder.IsFallbackable` accepts that exception (`:76-95`). WPF admission uses unchecked `(long)outW * outH * 4` (`WpfBitmapImageDecoder.cs:230-235`), so sufficiently large positive dimensions can wrap negative, pass the `<128 MiB` early return in `EnsureOutputFits`, then reach `EndInit`. No huge image or native decoder test was run. Use overflow-safe preflight before either backend and keep admission failures non-fallbackable.

### R17 — P2 candidate: single recovery retry does not revalidate source identity after preflight

`src/PhotoReview.Core/FileActions/RecoveryRetryService.cs:89-102,309-334`. `RetryCoreAsync` validates the failed source size/last-write fingerprint on the caller thread, then `ExecuteRetry` appends Prepared and moves/copies the path without a second fingerprint check. If that path is replaced after preflight but before mutation, the replacement can be committed as the original failed operation. Group retry has explicit fingerprint checks before and after Prepared, while the single retry path does not. Existing retry tests cover duplicate/stale operation races, but not a source replacement in this interval. Static race trace only; no files were mutated. Re-stat and compare the expected fingerprint immediately before mutation, retain the journal's recoverable failure path on mismatch, and add a deterministic fake interleaving regression.

### R18 — P2 candidate: Undo can move a replacement after its identity preflight

`src/PhotoReview.Core/FileActions/UndoService.cs` single and group Move-undo paths verify the moved-to file fingerprint and original-path absence during preflight, then append Prepared and move without rechecking the moved-to path identity immediately before mutation. If another file replaces it in that interval, the replacement may be moved back and committed; the post-move check is length-only. The group path has the same gap after its all-member pre-scan. Existing tests cover a blocker at the original path before preflight and a fingerprint mismatch at the moved-to path before retry, not replacement after Prepared. Static race trace only; no user files were touched. Revalidate the fingerprint after Prepared before each move and preserve recoverability on mismatch; add a gated fake replacement test for single and group undo.

### APP-P01 — P2 candidate: overlapping presentation can read Token from a disposed CTS

`src/PhotoReview.App/Coordinators/ImagePresenter.cs:247-253,286,377-378`. `PresentCoreAsync` captures `viewerDecodeToken`, but later rereads `viewerDecodeCts.Token`. A second overlapping presentation can exchange and dispose that CTS first, so the later Token getter can throw `ObjectDisposedException` instead of following the canceled decode path. Existing supersession tests cover general stale decode results, not this disposed-getter interleaving. Static only; no runtime reproduction. Use the already captured token at every access and add a gated overlap regression.

## Test-only risks and oracle gaps

- `TurboJpegGuardMutationTests.ReadAllBytes_LengthExactlyTheLimit_PassesTheLimitCheck` attempts `ReadAllBytes(MaxSourceBytes)`, approximately 2 GiB, in an untagged default-gate test. It was not run. Replace the allocation with a seam or isolate a resource test.
- `PerfCsvListenerMappingTests.Writer_FlushesAfterOneSecond` and related FileLog timing tests use fixed pauses before assertions. They can be scheduling-sensitive and conflict with the repository's no-fixed-delay-assert rule. They were not run.
- `UpdateCheckerBoundaryTests.DeclaredLengthOverMax_IsRejected` asserts only `BadResponse` for `ByteArrayContent`; it does not establish that the response body was rejected before consumption. Use read-counting or throw-on-read content.
- App `WaitForPresentationCountAsync` helpers create but do not apply their cancellation token to the barrier task, so their nominal timeout cannot bound a missing presentation.
- `AccessibilityNamesTests.EveryInputHasAnAccessibleName` does not include `Slider`/`RangeBase` in the controls it checks. The two SettingsWindow sliders currently have automation names, but removing them would not fail the test. No WPF test was run.
- `ActionProfilesDestinationTests` checks the pure validation helper, but does not invoke the dialog's Apply handler; removing Apply-side validation would leave the test green.
- `StaTestHost` uses a dispatcher timer for the local timeout and an unbounded `ready.Wait()` setup wait. A synchronous STA blockage prevents its own dispatcher timer from firing; only the external test-session hang guard can bound it.
- `BenchmarkImageExecutorTeardownTests` seeds a cache file but never starts or gates a prune; its elapsed-disposal assertion does not exercise disposal during active pruning.
- `RecoveryPathPanelTests.Copy_Click_ClipboardHeldByAnotherThread_DoesNotThrow` asserts only absence of an exception, so a no-op handler passes. It exercises the real clipboard and was not run.
- `StatusTextEnglishTests` asserts formatted numeric text without pinning `CurrentCulture`; `LanguageOptionsTests` reads ambient `Localizer.Current` outside the `GlobalState` collection, creating culture and global-state race gaps.
- `AppRound3GapTests` ignores the return from `SpinWait.SpinUntil` before using the dispatcher, so failed initialization lacks an explicit guard. Its STA composition test bounds `Join` but leaves a foreground thread alive if window resolution hangs.
- `RecoveryRetryServiceMutationTests.RetryMoveOrCopyAsync_RunsMutationOffCallerThread` awaits `done.Task` without a timeout before reaching its bounded thread join, so a stalled worker can still hold the test until the outer test-run guard.
- `RecoveryFileCheckMutationTests.Check_UndoRecycleGroupMemberWithDifferentContentAtItsPath_MemberIsNotAlreadyDone` changes file size but has no equal-size/different-timestamp case; removing the timestamp half of the fingerprint check would not fail the current test. Static oracle review only.
- Core `SettingsRobustnessTests.AssertUsable` claims all enum values are valid but omits `FitWidthAnchor`; add an `Enum.IsDefined` assertion and malformed numeric case. Source normalization handles invalid values; this is a test-oracle gap, not a production finding. Static only.
- App `FolderLoadCoordinatorTests` cover gated order/supersession/cancel behavior but do not dispose while a direct-open `PendingOrder` gate is published. Add a liveness assertion that the captured gate completes after Dispose (test-only gap APP-T16, static review).
- Grouped Undo-Recycle tests do not gate a foreign file appearing at a pending source path between the initial `RecoveryFileCheck` and retry execution. This is the race extension of R15, not a separate source finding.
- `LibRawDecoderTests.PrivateMemorySampler.Dispose` disposes its timer and process without waiting for an in-flight callback that reads process memory. A callback may race disposal and throw on a timer thread. This test-harness concern is unverified; native tests were not run.
- `RawContractsAndHeaderSourceTests.SourceRawHeaderSource_ReadsOnlyRequestedBlocks_AndCaches` checks read counts/lengths but not returned byte contents across cached or cross-block reads; the cache could return wrong bytes and still satisfy the current oracle. Missing DNG corpus fixtures can also cause early returns, so those methods do not validate camera samples when fixtures are absent.
- `FallbackExceptionCoverageTests` routes an injected `OverflowException` using an ordinary 32x24 JPEG; it does not cover hostile dimensions, arithmetic overflow, or the WPF fallback output-memory bound relevant to R16.
- `ImagePresenterTests` exercise general cancellation and supersession but do not gate the precise APP-P01 interleave where a later call retrieves `.Token` after the prior CTS has been disposed.
- `DecodeOriginal_RunsAtMostOneFullResolutionDecodeAtATime_AndDropsCancelledWaiters` uses a 300 ms negative wait on `SecondEntered` as an absence proof. Slow scheduling can hide an illegal second decode; replace the fixed-delay assertion with a signal proving the second request reached the gate before checking the decoder-entry count. Static only; test not run.
- Other baseline test-oracle findings remain tagged in the ledger, including a duplicate-cleanup/reload test that fakes the sink result and does not exercise ownership negotiation.

## Limits

All current delta findings are based on source/test inspection unless explicitly stated otherwise. This review does not demonstrate that the CLI symlink scenario was executed, that permanent deletion happened, that WPF/native/RAW behavior is correct at runtime, or that any current-head test gate passes. The full-project multi-agent callable review is still in progress.
