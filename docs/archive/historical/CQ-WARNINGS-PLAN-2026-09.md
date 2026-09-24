# CQ — Code Quality: Build Warnings (COMPLETE)

**Status:** ✅ DONE. 634 → 0 warnings. Verified via clean rebuild (`--no-incremental`) on `codex/cq-wave1-warnings`: 0 warnings, 0 errors, 800/800 non-Manual tests passing (1 known-flaky timing test `TS02` excluded, confirmed unrelated — passes in isolation, fails only under parallel-build machine load). **Merged to `master` via PR #18 (2026-09-22).**

**Path:** 634 → 160 (CA1707/CA1051 config, 1 commit) → 94 (Wave 1: CQ01-03) → 48 (Wave 2: CQ04/05/08) → 0 (Wave 3: CQ06/07). 32 commits total across `codex/code-quality-conventions` + `codex/cq-wave1-warnings`.

**Real bugs found and fixed along the way** (not just style):
- `xUnit1031`: `Task.WaitAll()` in a test — real deadlock risk, converted to `await Task.WhenAll()`.
- `PreloadSafetyTests.RecordingTarget`: held a `SemaphoreSlim` with no disposal path — real leak, fixed with `IDisposable`.
- `CS8603` in `MainViewModel.UndoService`: investigated root cause (field is unconditionally non-null post-construction; annotation was just inconsistent with an unrelated optional-DI-field convention) rather than blindly suppressed.



**Baseline:** commit `5e88704` on `codex/code-quality-conventions`. Verify with `dotnet build PhotoReview.slnx -c Release`.

## Priority Order

Production code (`src/`) first — real correctness risk. Then culture/comparison bugs in tools/tests. Then mechanical cleanup.

| ID | Warnings | Count | Files (scope) | Risk | Priority |
|---|---|---|---|---|---|
| **CQ01** | CS8603, CA1816 | 3 | `src/PhotoReview.App/ViewModels/MainViewModel.cs`, `App.xaml.cs`, `BenchmarkWindow.xaml.cs` | Production code — investigate, don't just silence | **P0** |
| **CQ02** | CA1305 | 30 | `tools/PhotoReview.Benchmark.Cli/*.cs` (28), `App.Tests/PerfTraceTests.cs` (2) | Diagnostic/CSV output — real correctness per AGENTS.md #4 | **P1** |
| **CQ03** | CA1310 | 24 | `tests/PhotoReview.Core.Tests/FileActions/DuplicateFinderTests.cs`, `Services/InterleavedFileActionSequenceTests.cs` | File-path comparisons in tests — per AGENTS.md #3 | **P1** |
| **CQ04** | CA1001 | 12 | `App.Tests` (4): `FolderLoadCoordinatorTests.cs`, `DiagOverrideTests.cs`; `Imaging.Tests` (8): `PreloadSafetyTests.cs`, `PreviewImageServiceTests.cs` | Type owns disposable field but isn't `IDisposable` — verify no leak in test fixtures | **P2** |
| **CQ05** | CS0618 | 10 | `App.Tests/DiagOptionsTests.cs`, `MainViewModelFileActionTests.cs`, `tools/.../IoDecodeSplit.cs` | Obsolete API usage — check replacement, may reveal stale test setup | **P2** |
| **CQ06** | CA1822 | 18 | `App.Tests` (10), `Architecture.Tests` (4), `Imaging.Tests` (4) | Mark static — **must update call sites** (see PR #17 CA1822 lesson) | **P3** |
| **CQ07** | CA1869, CA1826, CA1861, CA1847, CA1865, CA1859, CA1837, CA1829, CA2201 | 28 | Scattered, mostly `tools/`, a few tests | Mechanical API modernization, same pattern as PR #17 | **P3** |

## Task Detail

### CQ01 — Production code investigation (P0)
- **CS8603** `MainViewModel.cs:113`: `public UndoService UndoService => _undoService;` — non-nullable return but analyzer flags possible null. **Investigate first**: check `_undoService` field declaration and constructor initialization order before fixing. Do not add `!` blindly — determine if this is a real initialization-order bug.
- **CA1816** `App.xaml.cs` `Dispose()`, `BenchmarkWindow.xaml.cs` `Dispose()`: missing `GC.SuppressFinalize(this)` call. Add it — standard Dispose pattern fix, no behavior change.
- **Acceptance:** Full gate green; if CS8603 turns out to be a real bug (not just analyzer noise), document what was fixed and why.

### CQ02 — CultureInfo.InvariantCulture (P1)
- Add `CultureInfo.InvariantCulture` to every flagged `ToString`/`Format`/interpolated numeric string in `tools/PhotoReview.Benchmark.Cli/IoDecodeSplit.cs` and `Program.cs` (CSV/console diagnostic output).
- `App.Tests/PerfTraceTests.cs` (2): same fix in test assertions/output.
- **Acceptance:** No behavior change to values, only formatting culture explicit. Run affected tests.

### CQ03 — Explicit StringComparison (P1)
- `DuplicateFinderTests.cs`, `InterleavedFileActionSequenceTests.cs`: add explicit `StringComparison.Ordinal` (file names/paths — not user-facing sort).
- **Acceptance:** Tests still pass with same semantics; confirm Ordinal (not OrdinalIgnoreCase) matches existing production behavior in `DuplicateFinder.cs`/`FileActionService.cs` being tested.

### CQ04 — Disposable field ownership (P2)
- For each flagged test class, determine: does it truly own an `IDisposable` that needs cleanup, or is the field a mock/fake that doesn't need disposal?
- If real: implement `IDisposable` (or `IAsyncLifetime` for xUnit) and dispose properly.
- If mock/fake with no real resource: suppress with a one-line justification comment per AGENTS.md #6.
- **Acceptance:** No resource leaks; full gate green.

### CQ05 — Obsolete API replacement (P2)
- Identify what's marked `[Obsolete]` and being called at each site; check if a newer API exists in this codebase already.
- `IoDecodeSplit.cs` (2 sites): likely an internal API rename from earlier refactor — verify against current signature.
- **Acceptance:** No behavior change; obsolete call sites updated to current API.

### CQ06 — Static members + call-site updates (P3)
- Same pattern as PR #17's CA1822 fix: mark method static, then **must** update every call site (compiler will fail to build otherwise — this bit Agent C last time).
- Batch by project to avoid cross-file conflicts: App.Tests, Architecture.Tests, Imaging.Tests as three separate commits.
- **Acceptance:** Build succeeds, full gate green.

### CQ07 — Mechanical API modernization (P3)
- CA1869 (cache JsonSerializerOptions), CA1826/CA1829 (use `.Length`/`.Count` property not LINQ), CA1861 (static readonly array), CA1847 (char overload for StartsWith/Contains), CA1865 (char overload), CA1859 (concrete type for perf), CA1837 (Environment.ProcessId), CA2201 (don't throw reserved exception types — check what's thrown at `AppLogTests.cs:55` and use a more specific exception type).
- Same low-risk pattern as PR #17. Batch by file to keep commits reviewable.
- **Acceptance:** Build succeeds, no behavior change, full gate green.

### CQ08 — xUnit analyzer warnings (P2, discovered during Wave 1)
- **xUnit1031** (1, `Core.Tests/Catalog/SourceSizeTrackerTests.cs:158`): blocking task operation (`.Wait()`/`.Result`/`GetAwaiter().GetResult()`) inside a test — can deadlock on sync context. Investigate: convert to `async Task` test method with `await`, unless there's a documented reason it must stay sync.
- **xUnit2009** (22, `Core.Tests/Services/InterleavedFileActionSequenceTests.cs` — same file as CQ03): `Assert.True(x.Contains(y))` style assertions should be `Assert.Contains(y, x)` (or `Assert.EndsWith`/`Assert.StartsWith` as appropriate) for better failure messages. Mechanical rewrite, same semantics.
- **Acceptance:** Build succeeds, `dotnet test tests/PhotoReview.Core.Tests -c Release --filter "Category!=Manual" --no-build` — 325/325 passing, no regression.

## Execution Plan

Multi-agent, same pattern as PR #17 (4 agents completed 32 warnings there):

1. **Wave 1 (P0/P1):** CQ01 + CQ02 + CQ03 — 3 agents in parallel, disjoint files.
2. **Wave 2 (P2):** CQ04 + CQ05 — 2 agents in parallel, disjoint files.
3. **Wave 3 (P3):** CQ06 + CQ07 — 2 agents in parallel, disjoint files.

Each agent: fix scope, run `dotnet build -c Release` + affected test project, commit per warning-code (matching PR #17 convention), verify before reporting done.

## Verification

- `dotnet build PhotoReview.slnx -c Release` — warning count should reach 0 (or document any remaining with justification per AGENTS.md #6).
- `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` — no regression from baseline (800 tests passing per PR #17).
- Update this file's status line and `task_on_progress.md` when done.
