# Findings: delta review of App + Imaging* + Core changes since baseline 5d291076 (wave 2)

136 rows: OK 127, ISSUE 6, NEEDS-EVIDENCE 3, REMOVED 0 (ledger rows; they map to 9 findings D-01..D-09, all P3; no P1 or P2). Reproduced deterministically: 0. D-01 has a partial probe (executed), D-05 is pinned by an existing test, all others were only traced by reading. Ledger: [ledger-delta.tsv](ledger-delta.tsv). Evidence (never compiled by the build): `evidence/D01-copyfile-failure-cleanup.cs.txt`. Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files.

Scope facts: origin/master at review time was `3db54da1` with no new merges while the agent worked (open PRs then: #300, #299, #296, #294, #284). The diff against `5d291076` was 35 files and about 490 added lines. Platform.Windows and Imaging.TurboJpeg have no change on master (TurboJpeg only through open #299). The agent also judged the diffs of open PRs #296, #299, #300 and `tools/Publish-Guard.ps1` (#292). Read-only on src/tests; no builds or test suites were run; nothing destructive. Test names cited in the ledger were located by grep, not executed.

## Fix verification (does each fix do what it claims?)
- **R01 #290 (CopyCreationProof):** correct for the foreign-destination case on the single, group and recovery paths (`CopyForeignDestinationTests`). Residuals: D-01, D-06.
- **R07 #290 (AtomicCacheFile):** correct and complete; other temp writers use GUID names, no further instance.
- **R02 #288 (folder-load epoch):** correct; epoch captured at submission, rechecked before and after the only await, selection read in the same synchronous run; dropped commands are silent (acceptable).
- **R08 #288:** all four awaits in `ExecuteFileActionCoreAsync` now recheck folder identity. Residuals: D-02, D-04; sibling gap D-03.
- **R09 #288:** correct (`OpenFolderCoreAsync` returns the load epoch; the ownership `AfterOpen` contract is kept when a granted open is abandoned).
- **R05 #287:** correct (the normalizer resets a non-finite font size to the default and clamps the keyboard zoom step to [5,100]; the UI rejects NaN, Infinity and 1e999). No other double setting is unprotected.
- **WIC #289:** correct; the dead second guard term is D-05.
- **R03 #292 (Publish-Guard):** correct and complete for ancestor junctions. An own-fixture probe showed Windows PowerShell 5.1 `Remove-Item -Recurse` does not follow a junction nested inside the wiped directory.
- **R04/R10 #298:** correct. R04 relies on the awaiting continuation running inline rather than carrying the version (no interleaving found). R10: the only null-path caller with an image is the same-source swap.
- **#296 (open):** fix correct; lock-leak gap D-07.
- **#299 (open, R06 and the persist-worker claim):** correct as scoped. The new `DecoderMemoryAdmissionException : IOException` was checked against every `catch (IOException)` site and nothing reclassifies it as missing or corrupt. R06 is partial by design (D-08). The persist claim/release/delete logic is correct.
- **#300 (open, GCHandle):** correct. A grep for other `readonly` mutable-struct fields (GCHandle, SpinLock, CancellationTokenRegistration, MemoryHandle, enumerators/builders) found none (only `readonly Lock`, a reference type).

## D-01 (P3, NEEDS-EVIDENCE) The real file system claims copy-creation proof only after File.Copy returns
`PhysicalFileSystem.TryCopyNew` (3-arg), `src/PhotoReview.Core/IO/PhysicalFileSystem.cs:52-69`, raises `CopyCreationProof` only after `File.Copy` returns. So for the real file system the proof-guarded cleanup branches (`FileActionService.cs:187/539/646`, the group catch near 245, `RecoveryRetryService.cs:266-279`) run only when a later verify step fails. A copy that throws after creating the destination (disk full) is never claimed, so RV-C03 ("no partial file left") holds only in fakes: `InMemoryFileSystem.cs:224-239` marks the proof at the partial write.
- Scenario: Copy to a nearly full volume leaves a truncated file; Recovery shows Conflict and retry refuses "destination exists"; the source is intact.
- Partial probe (the evidence file, run as PowerShell on own temp files): a read-lock failure makes Win32 `CopyFile` remove its own destination (destination absent, HResult 0x80070021), which bounds the impact; the disk-full variant was not provoked.
- Remedy: copy through a `FileMode.CreateNew` stream and mark creation after the open, or treat post-creation HRESULTs (0x70/0x27) as proof; make the fake raise the proof only where the real one can. No open PR covers it. (This is the behaviour change the R01 fix agent already named for #290.)

## D-02 (P3, ISSUE) A successful action returns true for a folder that is no longer current
`FileActionController.cs:441` (`return true; // R08` in the `actionSucceeded` catch branch) contradicts the `<returns>` contract at line 164. `MoveOrCopyToFolderAsync` (lines 714-734) reads `folderAfterAction` at line 721 after the awaits, so its `IsFolderCurrent` guard is vacuous and writes "Moved to X" into folder B.
- Scenario: Move-to succeeds, bookkeeping throws, the user opens B during the present await. Traced only (an existing test covers the controller, not the wrapper).
- Remedy: return `IsFolderCurrent(folderGen)` there and capture `folderAfterAction` before the core call; add a `MainViewModel`-level test.

## D-03 (P3, ISSUE) DuplicateCleanupController writes BatchDone without a folder recheck
`DuplicateCleanupController.cs:261-269`: after `await _sink.OpenFolderAsync(folder)` the controller writes the BatchDone status (and the failure dialog) with no folder recheck. The reload bumps the folder generation itself, so the earlier guard cannot protect it. If the user opens B during A's reload, A's "Batch done" lands in B. Traced.
- Remedy: have the sink return the load epoch (as `OpenFolderCoreAsync` now does) or restructure the status write.

## D-04 (P3, ISSUE, pre-existing, F00218) No late status for an unverified single Move after a folder switch
`FileActionController.cs:293-334`: the stale-folder branch says "On failure nothing changed on disk", but a single Move with `Succeeded=false` and `SourceRemoved=true` (unverified destination) gets no late status after a folder switch (only success and part-way group Recycle do). The journal has a Failed entry, so no data loss, just unannounced. Traced.
- Remedy: late status for `!Succeeded && SourceRemoved`.

## D-05 (P3, ISSUE) Dead guard term in WicDirectDecoder.ThrowIfFactoryFailed
`WicDirectDecoder.cs:310-317`: the second term `|| !factoryCreated` is dead (`GetExceptionForHR(0)` is null), so S_OK with a null factory yields a NullReferenceException later. Pinned by the existing test `ThrowIfFactoryFailed_SuccessHresultButNoFactory_DoesNotThrow`. Unreachable with a healthy WIC proxy.
- Remedy: throw a typed error when `!factoryCreated`.

## D-06 (P3, NEEDS-EVIDENCE) Group Move compensation still infers ownership from a preflight stat
`FileActionService.cs:197` and `362-369`: group Move compensation infers ownership of the in-flight destination from a preflight stat (`GetFileStat(dest) is null`) plus `destination.Length < member.Size` (the same class as R01). A foreign shorter file appearing between the stat and the Move, with a Move failure other than "destination exists" and an unchanged source, is deleted. Needs two instances or an external writer; the error ordering could not be proven.
- Remedy: drop the in-flight partial deletion (Recovery already judges full-size destinations) or require a creation time newer than the Move start.

## D-07 (P3, ISSUE, open PR #296) SessionWriter.WriteBatch can leak the writer lock
`SessionWriter.WriteBatch` (PR branch lines about 214-223): `_writeLock.Wait` succeeds, then `Task.Factory.StartNew` can throw before the worker's `finally` exists, leaking the lock forever (every later bounded flush requeues and timer writes block). Traced.
- Remedy: a try/catch around `StartNew` that releases the lock and requeues.

## D-08 (P3, NEEDS-EVIDENCE, R06 residual) WPF fallback decoder has no memory admission
`WpfBitmapImageDecoder.Decode` (`WpfBitmapImageDecoder.cs:22`, unchanged) still has no memory admission. #299 only stops the fallback for admission refusals, so a primary failure for another fallbackable reason (COMException or InvalidData) on a huge image still allocates unguarded in the WPF fallback. No multi-GB fixture to prove it.
- Remedy: the same `MemoryHeadroom` check in the WPF decoder.

## D-09 (P3, ISSUE, fixed by open #300, verified correct) CancellationState GCHandle double free
Master `LibRawDecoder.cs:446-460`: `CancellationState` has `readonly GCHandle _handle`, `Free()` acts on a copy, and a second `Dispose` double-frees the slot (the cited StreamAsIStream crash). Production disposes once per `using var` (lines 39, 214), so it is latent there and was triggered by the idempotency test.

## Non-findings worth knowing
`IFileSystem.TryCopyNew` 3-arg default is non-atomic, but `FileSystemWrapperForwardingTests` discovers every src decorator by reflection and requires both overloads to forward, so it was recorded as OK. The R04 fix's reliance on inline continuation and the silent drop of stale queued commands (R02) were judged acceptable. The seam-only changes (dialog host, mouse, ambient dispatcher, LibRaw seams, validators extracted from `LibRawDecoder`, Cr3/PreviewSelector simplifications, `PerfCsvListener.Enqueue`) were checked for behavioural equivalence and are OK, with specific rationales in the ledger.

Test gaps noted: no `MainViewModel`-level test for D-02, no test for D-03, D-04 or D-07; the production-FS partial-copy path is untested (D-01).
