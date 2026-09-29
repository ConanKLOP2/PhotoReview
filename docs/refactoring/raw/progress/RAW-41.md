# RAW-41 Pair file actions — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

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
- [ ] Fault-injection crash-point tests; Category=Slow real-filesystem group checks; final visual Recovery-window review.

## Next action
Add failure injection around group journal append, second-member mutation, and Undo restore; run Slow/Native checks on the user's machine. Keep grouped mode opt-in/default Separate until those gates pass.

## Evidence / measurements
- `CaptureGroupActionServiceTests`: 8/8 pass, including partial recovery retry, reversed Undo Move manifest, Recycle restore, target-conflict preflight, and stale snapshot rejection.
- Related Core catalog/journal/undo tests: 54/54 pass. Related App controller/loading/Recovery tests: 97/97 pass.
- `tools/verify-all.ps1 -Hidden`: build succeeded with 0 warnings; default suite passed: Architecture 64, Core 1776, Imaging 642, Integration 635, App 1196. Slow/Native categories were excluded by the documented default gate; Integration&Slow supplementary gate ran 1 test successfully.
- Docs budget/links, generated OPEN-DECISIONS, translation catalog validation, publish guard, and framework-dependent release verification passed. Vietnamese catalog has one existing unused-key warning: `status.noSupportedImagesButSubfolders.one`.

## Open problems
- Crash-point, Slow/Native filesystem checks, and visual Recovery review remain. Group mode stays opt-in; the complete feature is not done.