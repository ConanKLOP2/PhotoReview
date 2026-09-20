# Structure Optimization Decisions — Q-ST1 through Q-ST4

**Date:** 2026-09-20 (Finalized)  
**Reference:** [`STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md`](STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md) section 2

All four critical decisions have been made and are reflected in completed ST tasks.

---

## Q-ST1: Separate PerfAnalysis Project?

### Question
Should `PerfAnalyze*` (6 files: analysis, grouping, report, utilities) be moved to:
- A new project `PhotoReview.PerfAnalysis` (`net10.0`, no WPF)
- Or remain in `Core`, minimizing new projects?

### Decision: ✓ **Separate Project**

Create new `PhotoReview.PerfAnalysis` project (`net10.0`, no `-windows`, no WPF dependency).

### Rationale

1. **Core Clarity:** Core is a domain library; performance analysis is tooling/optional feature.
2. **CLI Independence:** Allows `Benchmark.Cli` and test harnesses to reference `PerfAnalysis` directly without pulling `Benchmarking` (WPF, slow builds).
3. **Test Speed:** Analysis tests no longer require WPF/STA infrastructure or slow Benchmarking assembly load.
4. **Reuse:** Other projects (Analytics, Reporting) can consume analysis without WPF.
5. **Minimal Downside:** 6 files, straightforward extraction, no WPF entanglement.

### Implementation

- **Task:** ST05 (DONE)
- **Changes:**
  - New project: `src/PhotoReview.PerfAnalysis/`
  - Files moved: `PerfAnalyze*.cs` (6 files)
  - Updates: `PhotoReview.slnx`, `Benchmark.Cli` csproj, test project references
  - EventSource contract preserved: `PhotoReview-Perf` event IDs unchanged
- **Architecture Rule:** PerfAnalysis does not reference WPF, App, or Imaging
- **Test Location:** Integration tests run in `Core.Tests` (no STA required)

### Evidence

- `PerfAnalyze*` contains zero `using PhotoReview.Benchmarking` and zero `using System.Windows`
- 33 unit tests pass without WPF host
- No upward dependency on `Benchmarking` project

---

## Q-ST2: Move PerfCsvListener to Core?

### Question
Should `PerfCsvListener` and `DiagOptions` (diagnostics configuration) be moved to:
- `Core/Diagnostics`, allowing CLI and tests to reference without App dependency
- Or remain in App, keeping diagnostics as App-layer feature?

### Decision: ✓ **Move to Core/Diagnostics**

Relocate `PerfCsvListener` and `DiagOptions` to `Core/Diagnostics/`.

### Rationale

1. **Layering:** These are infrastructure concerns, not UI/App concerns. Core provides EventSource diagnostics; App is consumer.
2. **CLI Reuse:** `Benchmark.Cli` and other tools need diagnostic output without App reference.
3. **Contract Preservation:** EventSource name `PhotoReview-Perf` and event IDs unchanged; no breaking change to wire format.
4. **Minimal Scope:** Two small files, straightforward move. No behavioral change to EventSource or CSV format.
5. **Testability:** Core diagnostic tests do not require App or WPF initialization.

### Implementation

- **Task:** ST04 (DONE)
- **Changes:**
  - Files moved: `App/Diagnostics/PerfCsvListener.cs` → `Core/Diagnostics/`
  - Files moved: `App/Diagnostics/DiagOptions.cs` → `Core/Diagnostics/`
  - Namespace updated in consumers: `PhotoReview.App.Diagnostics` → `PhotoReview.Core.Diagnostics`
  - DI container: wiring updated, lifetime preserved
  - EventSource: no changes to `EventSource` contract or event IDs
- **Affected Consumers:** CLI, Integration.Tests, Benchmarking.Tests
- **Architecture Rule (updated):** Core does not reference WPF / App types (still valid; only receiving PerfCsvListener)

### Evidence

- `PerfCsvListener` references only `EventSource`, `TextWriter`, and `IDisposable`
- `DiagOptions` is POD (plain old data): file paths, booleans, strings
- No `using PhotoReview.App` in these files; no UI or WPF dependency
- 100% compatible with Core dependencies

---

## Q-ST3: Keep Cli→App Dependency? Permit Reflection?

### Question
Should we:
- A) Cut `Benchmark.Cli` → `App` dependency entirely (requires ST12 Presentation project split)
- B) Keep dependency but ban reflection; instead make necessary members public/accessible
- C) Keep dependency and allow reflection into private members (current state, fragile)

### Decision: ✓ **Option B: Accept dependency, make members public**

Keep `Benchmark.Cli` → `App` reference. Remove reflection into private members by:
- Making necessary `MainWindow` members public (via properties/methods)
- Adding public `PerfDispatcherHooks` property
- Adding public method `SuppressWindowPlacement()`

Do not implement ST12 (Presentation split) unless measurement shows > 5% build-time improvement.

### Rationale

1. **Pragmatism:** Cutting the dependency requires new project (ST12), adding complexity for unmeasured benefit.
2. **Reflection Risk:** Reflection into private members is fragile (rename breaks silently at runtime). Public contract is explicit and verified at build time.
3. **Minimal API Surface:** Only 3–4 members needed; manageable public API.
4. **Reversibility:** If future refactoring requires the split, removing these public members is straightforward.
5. **Build Impact:** No measurable build-time savings from removing App reference alone (measured in ST00 investigation).

### Implementation

- **Task:** ST06 (DONE)
- **Changes:**
  - `MainWindow.SuppressWindowPlacement()` — public method to skip restoring window position
  - `PerfDispatcherHooks` — public static property in App or extracted to public service
  - Removal of reflection: 26 private-member lookups eliminated
  - Architecture rule: Cli does not use reflection into App members (only direct public API)
- **Consequence:** Cli→App dependency remains; acceptable trade-off
- **Future Option:** ST12 (Presentation split) available if metrics warrant it

### Evidence

- ST12 investigation found: Presentation types are public, no cycles, ~2.3k lines
- But: 0 projects benefit from dropping App reference (all use App types directly)
- Estimated build-time benefit: < 1% (unproven with measurement)
- Reflection workaround: currently 26 unsafe lookups; all replaceable with public API

---

## Q-ST4: Ctrl+Z Semantics — Move-Only or Move+Recycle?

### Question
When Ctrl+Z (Undo) is invoked after a Move or Delete (Recycle), should the undo:
- A) Undo Move only; Recycle is separate operation (modal)
- B) Undo both Move and Recycle (queue/integr semantics)
- C) Undo only the most recent action, whichever it was

### Decision: ✓ **Move+Recycle: Undo both**

Ctrl+Z will undo the most recent file action, whether Move or Recycle. Undo history is a single queue; pressing Ctrl+Z multiple times walks backward through all actions.

### Rationale

1. **User Expectation:** "I moved the file, then deleted it, now I want both undone" is common workflow.
2. **Consistency:** Single undo stack is simpler than modal (Move vs. Recycle) undo semantics.
3. **Implementation:** OC14 will implement unified Undo entry point and queue semantics.
4. **Test Coverage:** TC05 verifies undo order and final state after mixed Move/Recycle/Undo sequence.

### Implementation

- **Task:** OC14 (blocks ST08–ST09)
  - Unify Undo entry point
  - Implement undo queue instead of modal state machine
  - Add `IUndoableAction` interface for Move, Delete (Recycle), and future actions
  - Update UI to show undo stack breadcrumb or history list
- **Tests:** TC05 — queue order, state after undo, redo semantics

### Evidence

- Current code: `DuplicateCleanupController` and `FileActionController` do not coordinate undo
- Proposal: single `UndoStack<IUndoableAction>`, not separate Move/Delete handlers
- Risk: regression in undo behavior if not tested; mitigated by TC05

---

## Summary: All Decisions Locked

| Question | Decision | Task(s) | Status |
|---|---|---|---|
| Q-ST1 | Separate project `PhotoReview.PerfAnalysis` | ST05 | ✓ DONE |
| Q-ST2 | Move PerfCsvListener to `Core/Diagnostics` | ST04 | ✓ DONE |
| Q-ST3 | Accept Cli→App; make MainWindow members public | ST06 | ✓ DONE |
| Q-ST4 | Move+Recycle undo semantics (queue, not modal) | OC14 | ⏸ BLOCKED (OC14 pending) |

All architectural decisions are finalized. No open questions remain for ST01–ST12 scope.

**Blocked tasks:**
- **ST08–ST09** await OC14 implementation (Q-ST4 undo semantics)

---

## Related Decisions (Q-T1..Q-T4 — Test Cleanup)

Test cleanup has separate decision gates (Q-T1 through Q-T4), documented in [`TEST-CLEANUP-PLAN-2026-09-20.md`](TEST-CLEANUP-PLAN-2026-09-20.md):

- **Q-T1:** Queue keypresses when action running? — **✓ YES**
- **Q-T2:** Add seam to detect source-read blind spots? — **✓ YES**
- **Q-T3:** Use real-photo directory via env var? — **✓ YES** (if available)
- **Q-T4:** Use real Recycle Bin in tests? — **✓ YES**

These are orthogonal to ST decisions and do not block any ST tasks.

---

**Last reviewed:** 2026-09-20 · **Status:** Final, all decisions locked
