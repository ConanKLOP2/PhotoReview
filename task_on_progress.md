# PhotoReview — Current Status

**Updated:** 2026-09-22

**Master task list (live):** [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md) — consolidated status, blockers, and critical path for all groups (TS/TC/T89/OC/WD/IO/DT/D).

## Done since last full review

- **ST01-ST07, ST10-ST12** — structure optimize, merged. ST08/ST09 remain, blocked on OC14. See [`docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md`](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md).
- **TS00-TS04** — default test gate no longer hangs, runs ~23s. TS05-TS10 remain.
- **DF00-DF07** — double-click-to-Fit gesture, PR #14 merged.

## Currently open (see ACTIVE-TASKS.md for detail)

- **T89** GUI/STA acceptance for Fit layout — implemented on `feature/Fit-Layout-Status` only, not on `master`.
- **TC01-TC11** test cleanup — not started; TS10 must re-audit prior "done" claims first.
- **OC14** (Undo unification) is the key blocker for ST08/09, all of WD, and OC15-18.
- **IO01/IO02** contract + baseline gate the rest of I/O durability work.
- **DT00-DT10** docs token diet — planned, not implemented; 4 decisions pending.
- **D06-D12** perf diagnosis — needs a Procmon/ETW session before D01/D02/D08/D09 can proceed.

## Process rules (carried forward)

- Do not commit directly to `master`: feature branch → push → PR, per AGENTS.
- Do not reduce `ApplyFitViewAsync` to a single pass without STA/layout/GUI evidence (T89).
- Do not add `Dispatcher.Invoke` broadly, or convert `GetService`→`GetRequiredService`, before WD01 proves the contract.
- Do not reduce journal durability, enable `IgnoreInaccessible`, or introduce a wide `IAsyncFileSystem` before IO01/IO02 evidence.
- No OS SendInput/SendKeys/SetForegroundWindow in test harnesses; use dedicated fixtures.

Detailed history of the 2026-09-20/21 review round (commit hashes, baseline numbers, probe results) is archived at [`docs/archive/historical/task-log-2026-09-21.md`](docs/archive/historical/task-log-2026-09-21.md).
