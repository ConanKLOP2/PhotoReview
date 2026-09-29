# RAW-41 Pair file actions — progress
Branch: feat/raw-support-integration · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: READY FOR REVIEW

## Approved design (lead)
- Keep one append-only journal operation per capture group. Add optional, omitted-when-null group metadata at the end of `JournalEntry`; old single-file JSON keeps its existing shape and remains readable.
- The group manifest stores every source path, destination path when applicable, size, last-write time, and recycle permanence. Preflight every member before the first mutation; append the complete Prepared manifest before acting on any member.
- Execute and verify members in deterministic order. Stop on the first failure and append one Failed group outcome. Recovery identifies the group and reports member-by-member filesystem state; retry skips members already completed and retries unchanged, conflict-free members.
- Startup reconciliation evaluates the entire manifest and records one group verdict. It never guesses or overwrites: mixed/ambiguous states remain Failed and visible in Recovery.
- Undo registers one group action. Group Move undo is a journaled reverse manifest; group Recycle restore preflights all original paths and records a recoverable group undo before restoring members. Permanent recycle remains explicitly non-undoable, matching current single-file behavior.
- Catalog membership changes once per capture group; on partial failure restore still-present members as separate entries so no remaining photo disappears from review.
- Keep `RawPairMode.Separate` as the default until grouped actions and their failure/recovery checks are complete.

## Steps
- [x] Journal manifest schema, all-member preflight, group execution, group-aware reconciliation/retry, and per-member Recovery status.
- [x] Group undo/Recycle restore and catalog recovery after success, failure, folder switch, and retry; group Recycle confirmation is consolidated.
- [x] Add fault-injection coverage for Prepared journal append failure, second-member Move failure, and partial group Recycle restore during Undo.
- [x] Add Category=Slow real-filesystem group Move + journaled Undo check.
- [x] Category=Native RAW corpus group Move/Undo check using a copied corpus RAW file in an isolated temporary directory.
- [x] Recovery-window visual review waived by the user (2026-09-29); automated behavior/recovery coverage remains the validation basis.

## Next action
No further RAW-41 implementation work remains; review this task's changes in PR #239. Keep grouped mode opt-in/default Separate as decided in Q-RAW-04 until RAW-62/RAW-70 handles real-machine validation and default selection.

## Evidence / measurements
- Bounded focused Release run: 31/31 passed across `CaptureGroupActionServiceTests`, `UndoServiceTests`, and `CaptureGroupActionSlowTests`; includes a real temporary-filesystem group Move + journaled Undo.
- Fault injection covers Prepared journal append failure (no member mutation), failure on the second member's real `IFileSystem.Move` hook, and second-member failure while Undo restores a recycled group (first restore remains represented in a Failed group manifest).
- Mutation checks: omitting the Prepared append made `ExecuteGroupAsync_PreparedJournalAppendFails_DoesNotMutateAnyMember` fail; bypassing the in-memory Move fault hook made `ExecuteGroupAsync_PreparesManifestBeforeMovingAndReportsPartialFailure` fail; omitting the Undo Failed outcome made `UndoLastAsync_GroupRecyclePartialRestore_RemainsRecoverableAndUndoable` fail. All mutations were restored, followed by a green 31/31 focused run.
- Bounded imaging `Category=Native` RAW corpus/runtime gate: 35/35 passed in 3m33s.
- Bounded `CaptureGroupActionNativeTests`: 1/1 passed on the copied Canon EOS 350D CR2 corpus sample; Move + journaled Undo restored the copied pair and left the original corpus file intact. Replacing the group Move with a no-op made this test fail; the source mutation was reverted and the test passed again.
- Full Release build: 0 warnings, 0 errors. `tools/verify-all.ps1 -Hidden` passed: Architecture 64, Core 1778, Imaging 642, Integration 635, App 1196; supplementary Integration+Slow 1/1; docs/link/localization/publish checks passed (the known unused vi key warning remains).
- GitHub CI for branch head `d114eafd`: `repo-checks`, `build-test-publish`, and `tag-version` all passed; PR #239 remains open and draft.
- Separate Core `Category=Slow` gate: 7/8 passed, including the new real-filesystem action test; the 10k-path grouping performance assertion measured 31.12 ms median against its 20 ms threshold under suite load. The same test passed on immediate isolated rerun; treat the suite timing miss as environment-sensitive and retain both results.
- Related Core catalog/journal/undo tests: 54/54 pass. Related App controller/loading/Recovery tests: 97/97 pass.
- `tools/verify-all.ps1 -Hidden`: build succeeded with 0 warnings; default suite passed: Architecture 64, Core 1776, Imaging 642, Integration 635, App 1196. Slow/Native categories were excluded by the documented default gate; Integration&Slow supplementary gate ran 1 test successfully.
- Docs budget/links, generated OPEN-DECISIONS, translation catalog validation, publish guard, and framework-dependent release verification passed. Vietnamese catalog has one existing unused-key warning: `status.noSupportedImagesButSubfolders.one`.

## Open problems
- None for RAW-41. The user explicitly waived the optional visual Recovery-window review; no GUI behavior is claimed from that waiver. RAW-70 still owns product documentation/default decisions after RAW-62.
