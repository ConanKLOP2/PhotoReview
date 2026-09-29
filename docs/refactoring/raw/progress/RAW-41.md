# RAW-41 Pair file actions — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Approved design (lead)
- Keep one append-only journal operation per capture group. Add optional, omitted-when-null group metadata at the end of `JournalEntry`; old single-file JSON keeps its existing shape and remains readable.
- The group manifest stores every source path, destination path when applicable, size, last-write time, and recycle permanence. Preflight every member before the first mutation; append the complete Prepared manifest before acting on any member.
- Execute and verify members in deterministic order. Stop on the first failure and append one Failed group outcome. The Recovery row identifies the group and reports member-by-member filesystem state; retry skips members already completed and retries only unchanged, conflict-free members.
- Startup reconciliation evaluates the entire manifest and records one group verdict. It never guesses or overwrites: mixed/ambiguous states remain Failed and visible in Recovery.
- Undo registers one group action. Group Move undo is a journaled reverse manifest; group Recycle restore preflights all original paths and records a recoverable group undo before restoring members. Permanent recycle remains explicitly non-undoable, matching current single-file behavior.
- Catalog membership changes once per capture group; on partial failure restore the still-present member(s) as separate entries so no remaining photo disappears from review.
- Keep `RawPairMode.Separate` as the app behavior until group action, retry, undo, and crash-point tests pass.

## Steps
- [ ] 1. Journal manifest schema, all-member preflight, group execution and group-aware reconciliation/retry.
- [ ] 2. Group undo/Recycle restore and catalog recovery after success, failure, folder switch, and retry.
- [ ] 3. Fault-injection crash-point tests; Category=Slow real-filesystem group checks.

## Next action
Implement the backwards-compatible journal manifest model and tests first. Do not enable grouped mode in `FolderLoadCoordinator` until every mutation and undo path is group-safe.

## Evidence / measurements
- Existing single-file flow: `FileActionService.ExecuteAsync` writes one Prepared entry before one mutation; startup reconciles entries independently; Recovery retries one Move/Copy; `UndoService` tracks one Move or Recycle at a time.

## Open problems
- None; design reviewed and approved by lead before implementation.
