# Test speed and gate reliability plan (TS00-TS10)

- Date: 2026-09-20. Measured on the working tree at `10ff31f` (= `master` `4afee69` + the T89.1-T89.2 files; nothing else differs), Windows 11, 12 logical cores, Release build.
- Status: investigation `DONE` with measurements (section 1). **This plan changes no test or source.** Every number below was measured in this session unless marked "target".
- Relation: prerequisite for [`TEST-CLEANUP-PLAN-2026-09-20.md`](TEST-CLEANUP-PLAN-2026-09-20.md) (TC00-TC11). Several TC commits reported "done" are defective (F5-F8), so TC03-TC11 must not be trusted until TS10 re-audits them.
- Goal: the default gate finishes in about a minute, can never hang silently, leaves no garbage behind, and is green at `HEAD`.

## 1. Measured facts

| ID | Fact | Evidence |
|---|---|---|
| F1 | **The default gate never finishes.** `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Stress"` exceeded 120 s three times. The App.Tests `testhost` stayed alive for more than 5 minutes with only 41 CPU-seconds, so it was blocked, not computing. | `--blame-hang --blame-hang-timeout 60s` named the test: `App.Tests.HotPath.WarmNavigationReadBoundsTests.WarmNext_InCachedRange_ReadsZeroSources`. |
| F2 | With that one class excluded, the whole solution runs in **41 s wall**. | Per project (own wall time): Core 325 tests 4 s (18 fail), Imaging 205 tests 4 s, App 188 tests 8 s, Architecture 13 tests 15 s (2 s when run alone, so contention from parallel hosts, not a defect), Integration 57 tests 32 s (2 fail at 10-11 s each, i.e. 20 of the 32 s). Every other App test is under 1 s. |
| F3 | Fixture generation costs **16.5-22.9 s per test process** for 20 photos and writes **389 MB**. Random-byte noise images are the worst case for JPEG. | `PhotoFolderBuilder.GenerateJpeg` fills pixels with `Random.NextBytes` at 4000x3000, 6000x4000 and 8000x6000. On disk: 7 files ~9 MB, 7 files ~17 MB, 6 files ~35 MB. Timestamps of folder creation vs last photo. |
| F4 | **Test garbage is never cleaned.** 10 `PhotoReview-TC01-*` folders (3.9 GB) and 17 `PhotoReview_TC05_*`/`TC05_*` directories sat in `%TEMP%` after today's runs. | `PhotoFolderBuilder.Cleanup()` has no caller; `WarmNavigationReadBoundsTests.DisposeAsync` is empty; the TC05 helpers create temp dirs with no delete. |
| F5 | **Regression origin.** 17 Core failures are mojibake in three test classes plus 1 load-sensitive journal test; Integration has 2 timeouts. All 30 tests of those three Core classes and all 3 Explorer tests **pass at `0fa4bcd`** (parent of `fe00f36`): 30/30 in 0.3 s, 3/3 in 6 s. | Worktree at `0fa4bcd`. |
| F6 | The 2 Integration timeouts come from one 9-line change. Copying only `StaTestHost.cs` from `HEAD` into the `0fa4bcd` worktree makes the same 2 tests fail at 10-11 s. | Bisect. `fe00f36` rewrote `StaTestHost.WaitForAsync` from polling to a tight `Dispatcher.InvokeAsync(Background)` loop. Why that starves the folder load is **not yet understood** (hypothesis only). |
| F7 | **Mojibake scope.** Vietnamese strings were double-encoded (UTF-8 read as cp1252 and re-saved), e.g. `Nguá»“n khÃ´ng cÃ²n tá»“n táº¡i.` for `Nguồn không còn tồn tại.`. Markers at `HEAD`: `AppPathsTests` 1, `FileActionServiceTests` 4, `RecoveryRetryServiceTests` 4, `FileSystemContractTests` 2, `SettingsValidatorTests` 6. Also 5 **production** files in `src/PhotoReview.PerfAnalysis/` (doc comments such as `má»¥c`); they had 0 markers before ST05 commit `937f642`. | `fe00f36` changed 89 files (1164 insertions, 100 deletions). Regex `Ã[-¿]\|á»\|â€\|Ä[-¿]`. |
| F8 | **Passing tests that prove nothing** in the same class. `MoveDelete_InFolder_DoesNotReadUnaffected` contains only `TODO` comments and `await Task.CompletedTask`, yet reports Passed in <1 ms. `RapidNextRepeat_...` asserts only `CurrentIndex` arithmetic although it claims "presented in order". `MoveDeleteQueue_...` **fails** (`Assert.DoesNotContain`, line 364) because it asserts the Q-T1 queue behavior, while production still drops the action when busy (`FileActionController.cs:99`: `if (_fileActionService.IsBusy) return;`). | Test source and production source. |
| F9 | **The gate cannot detect a hang and runs projects one after another.** `tools/verify-all.ps1:84` and `.github/workflows/ci.yml:44-56` call `dotnet test` with no hang timeout. | Source. |
| F10 | `LargeJournal_ReadCommittedMoves_IsBoundedAndFast` takes 3.3 s, almost all of it writing 100 000 lines with `JsonSerializer.Serialize` in setup, then asserts `< 100 ms` wall time on the read. It fails when other hosts run in parallel (known). | `OperationJournalTests.cs:337`. |

## 2. Root causes

| RC | Cause | Explains |
|---|---|---|
| RC1 | The hanging test builds its view model with `MainWindowHelpers.CreateTestViewModel`, which binds `WpfPresentationSink` to `Dispatcher.CurrentDispatcher` (`MainWindowHelpers.cs:144`). On an xUnit thread nobody pumps that dispatcher, so any marshalled call waits forever. The test has no timeout. `StaTestHost` in Integration.Tests exists to solve exactly this; App.Tests has no equivalent. | F1 |
| RC2 | Fixture is expensive by construction (noise, up to 48 MP, encoded per process) and has no lifetime management. | F3, F4 |
| RC3 | A wait helper was rewritten from polling to a spin loop without running the Integration suite. | F6 |
| RC4 | A mass edit across 89 test files (and earlier ST05's move) re-encoded text. Nothing checks encoding. | F7 |
| RC5 | Process gap: after `fe00f36` no complete default-gate run finished, and the gate has no hang guard; earlier "DONE"/"PASS" claims were not backed by a full run (same lesson as the ST09 build break). | F1, F5, F8, F9 |

## 3. Targets (validated at the end of each task, not assumed)

1. Default gate (`dotnet test PhotoReview.slnx ...`) wall time <= 60 s on this 12-core machine; <= 3 min on CI (target).
2. Any hang fails within <= 120 s and names the test.
3. No default-gate test takes >= 5 s; a test that legitimately does gets `Category=Slow` and leaves the default gate.
4. Zero new directories matching `PhotoReview-TC01-*`, `PhotoReview_TC05_*`, `TC05_*` in `%TEMP%` after a full run.
5. Zero failing tests at `HEAD`; zero passing test bodies without an assertion.
6. Fixture: 20 photos <= 3 s and <= 60 MB (target; measure and record the real number).

## 4. Tasks (priority order; L1 mechanical, L2 medium; coordinator reads every diff)

### TS00 - Guard rails so nothing can hang the gate - TODO (L1, P0)
- **Files:** `tools/verify-all.ps1` (line 84), `.github/workflows/ci.yml` (lines 44-56), `tools/test-verify-gates.ps1`, new helper in `tests/PhotoReview.TestSupport/` (for example `AwaitExtensions.WithTimeout`).
- **Do:** add `--blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none` to every `dotnet test` call, keeping the existing filter. Add `WithTimeout(this Task, TimeSpan, string what)` built on `Task.WaitAsync` that throws a `TimeoutException` naming `what`. Use one shared filter/timeout definition for the local gate and CI (they differ today).
- **Do not:** lower any assertion threshold; add `Task.Delay`.
- **Done when:** a temporary test that awaits a never-completing task (not committed) makes `./tools/verify-all.ps1` fail within about the timeout and print that test's name; `test-verify-gates.ps1` covers it.

### TS01 - Fix the hanging App test and its two siblings - TODO (L2, P0)
- **Files:** `tests/PhotoReview.App.Tests/HotPath/WarmNavigationReadBoundsTests.cs` only.
- **Do:**
  1. `WarmNext_InCachedRange_ReadsZeroSources`: stop using `CreateTestViewModel`. Use the file's own `CreateViewModelWithActions` (stub sink and preload, real `PreviewImageService` + `PhysicalFileSystem`; the same stack `RapidNextRepeat_...` already passes with in <1 s) so the test measures disk reads, not WPF. It needs the `ReviewMetrics` instance that stack creates, so expose it. Wrap awaits in `WithTimeout(20 s)`.
  2. **Check the premise before asserting it.** The stack uses a 64 MB RAM cache and 1920 px previews (about 11 MB each), so navigating to index 8 may already evict early images and make "warm Next reads 0 sources" false for the wrong reason. Measure decoded bytes, size the warm range to fit, and assert the cache hit explicitly.
  3. `MoveDelete_InFolder_DoesNotReadUnaffected`: implement TC03 case 4 or mark `[Fact(Skip = "TC03.4 not implemented")]` so it reports Skipped, never Passed (xunit 2.9.3 has no `Assert.Skip`).
  4. `RapidNextRepeat_...`: assert on what the sink actually received (order, no duplicate, last equals `Catalog.Current`), not on `CurrentIndex` alone.
  5. `MoveDeleteQueue_...`: see Q-S2.
- **Done when:** the class finishes in seconds, 30 consecutive runs are stable, and a planted stall (removing a `TaskCompletionSource` completion) fails within the timeout instead of hanging.
- **Risk:** the warm-cache test may prove production really re-reads sources in that range. That is a finding to report, not something to hide by widening the assertion.

### TS02 - Cheap, clean fixture - TODO (L2, P0; after TS01)
- **Files:** `tests/PhotoReview.TestSupport.Windows/Fixtures/PhotoFolderBuilder.cs`, the classes that use it (App.Tests HotPath now; Integration.Tests manual/native later).
- **Do:**
  1. Replace random noise with deterministic smooth content (gradient plus seeded blocks) so a q85 JPEG is about 1-3 MB, not 9-35 MB. Measure the sizes and record them.
  2. Encode **once per distinct size** (masters) and create the other files by copying bytes and inserting a unique JPEG `COM` segment after `SOI`, so every file has different bytes and fingerprint at almost no cost. Decode one replica per size to prove it is valid (TC01 already specified this).
  3. Keep 4000x3000 as the norm, one or two 6000x4000, at most one 8000x6000, and only where memory pressure is under test; read-count invariants do not need 48 MP.
  4. Lifetime: build once per test process (`Lazy`), delete on process exit, and purge stale `PhotoReview-TC01-*` older than 6 hours on first use. Remove the per-call `Guid` path that is computed before the cache check.
  5. One-time cleanup of today's leaks (Q-S4).
- **Done when:** `BuildFolder(20)` <= 3 s and <= 60 MB (recorded), all fixture users pass, and `Get-ChildItem $env:TEMP -Directory -Filter 'PhotoReview-TC01-*'` is unchanged after a full gate run.

### TS03 - Restore the Explorer waiting behavior - TODO (L2, P0)
- **Files:** `tests/PhotoReview.Integration.Tests/Infrastructure/StaTestHost.cs` (`WaitForAsync` only).
- **Do:** restore the `0fa4bcd` implementation (`git show 0fa4bcd:tests/PhotoReview.Integration.Tests/Infrastructure/StaTestHost.cs`), which is verified 3/3 in 6 s. Do **not** redesign by guesswork. Afterwards, if event-driven waiting is still wanted (TC09), do it as a separate step, first finding out why the spin loop starves the folder load, and keep a generous timeout as the backstop.
- **Done when:** the 3 `MainWindowExplorerOrderTests` pass in <= 10 s total (baseline 6 s), the Integration project takes <= 15 s, 10 consecutive runs are stable.

### TS04 - Restore the Vietnamese text and guard against it - TODO (L1, P0)
- **Files:** the 5 test files and 5 `PerfAnalysis` files in F7; `tests/PhotoReview.Architecture.Tests/` (new rule).
- **Do:** for each file, take the diff against the last good version (`git diff 0fa4bcd HEAD -- <file>` for tests, `git diff 937f642^ HEAD -- <path>` for the moved PerfAnalyze files), restore only the corrupted literals and comments from the good version, and keep every legitimate edit. Preserve each file's existing encoding and line endings (UTF-8 with BOM, CRLF). Then run `git diff --name-only 0fa4bcd fe00f36` through the same marker regex to confirm nothing else is hit. Add an Architecture rule that scans `src/**/*.cs`, `tests/**/*.cs` and `docs/**/*.md` (excluding `docs/archive`) for the mojibake signatures and reports `file:line`.
- **Done when:** the 17 tests pass; the rule passes on the clean tree and fails when a corrupted literal is planted (mutation check, not committed).

### TS05 - Deterministic, faster journal test - TODO (L2, P1, needs Q-S3)
- **Files:** `tests/PhotoReview.Core.Tests/OperationJournalTests.cs` (test at line 337).
- **Do:** cut the 3.3 s setup (write the 100 000 lines without a per-entry `JsonSerializer.Serialize`, for example by formatting the JSON string directly). Assert bounded work with the existing `CountingFileSystem` (bytes read and lines parsed stay within a small multiple of the 200-entry tail) and keep a generous wall-clock cap (for example 2 s) only as a backstop with a clear message.
- **Do not:** silently raise the 100 ms threshold; the intent (startup reads only the recent tail) must remain enforced.
- **Done when:** the test takes <= 0.5 s, fails if `ReadCommittedMoves` is changed to read the whole file (mutation check), and passes 30 times in parallel with the full suite.

### TS06 - Honest hot-path tests - TODO (L2, P1; after TS01, TS02)
- **Do:** implement or skip-with-reason every hot-path test per `TEST-CLEANUP-PLAN` TC03-TC05 acceptance; no test may pass without an assertion. Add a check (source scan in Architecture.Tests) that a `[Fact]` body containing `TODO` must carry `Skip`.
- **Done when:** the check passes; skipped tests are visible as Skipped in the report.

### TS07 - Temp hygiene - TODO (L1, P1)
- **Files:** `WarmNavigationReadBoundsTests.cs` and any test creating `PhotoReview_TC05_*`/`TC05_*` directories.
- **Do:** use the existing `TempRoot` (`IDisposable`, `tests/PhotoReview.TestSupport/TempRoot.cs`) and delete in `DisposeAsync`.
- **Done when:** TS00's leak check shows zero new directories after a full run.

### TS08 - Timing report - TODO (L1, P2)
- **Do:** add an opt-in `-TestReport` switch to `tools/verify-all.ps1` that prints per-project wall time and the 10 slowest tests from the trx files, and warns (does not fail) when target 1 or 3 is exceeded. Timing is reported, never a PASS/FAIL criterion of production speed.
- **Done when:** the report reproduces the section 1 table from a real run.

### TS09 - CI/local alignment - TODO (L1, P2)
- **Do:** verify what `Category!=Integration` (applied to every project in `ci.yml`) actually excludes and whether the `HotPath` category runs in CI as TC11 claims; fix the filter in one shared place.
- **Done when:** the CI step list and `verify-all.ps1` run the same set, confirmed by comparing the executed test counts.

### TS10 - Re-audit the commits that claimed TC01-TC11 - TODO (L2, P1; after TS04)
- **Scope:** `d5fc9c8`, `fe00f36`, `1c1f728`, `34886ec`, `7cab725`.
- **Do:** (1) list every non-`[Trait]` hunk in `git diff 0fa4bcd fe00f36 -- tests` (89 files) and justify or revert each; (2) for each TC06-TC11 claim, show the test exists, asserts something, and is correctly categorized (`Native`/`Manual`/`HotPath`); (3) update `docs/ACTIVE-TASKS-2026-09-20.md` and `TEST-CLEANUP-PLAN` statuses to what the evidence supports.
- **Done when:** a table of claim vs evidence is committed and no "DONE" remains without a real run.

## 5. Order and conflicts

`TS00` first. Then `TS01`, `TS03`, `TS04` in parallel (disjoint files). `TS02` after `TS01` (same users). Then `TS06`, `TS05`, `TS07`. `TS08`, `TS09` next; `TS10` after `TS04`.

- `MainViewModel*Tests` and the Undo tests belong to other tasks; TS tasks touch only the files listed.
- T89 files on `feature/Fit-Layout-Status` are untouched by this plan.

## 6. Verification protocol

- Before and after each task: `dotnet build PhotoReview.slnx -c Release` (0 errors), then the full gate with hang protection:
  `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Stress" --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none --logger trx`
- Record wall time per project; compare with the section 1 table and the section 3 targets.
- Hot-path and STA tests: repeat 30 times.
- Leak check: count `%TEMP%\PhotoReview-TC01-*` and `TC05*` before and after a full run.
- Mutation checks (planted hang, planted mojibake, journal read of the whole file) are done locally and never committed.
- Never report "N tests pass" from a run against stale binaries; rebuild first.

## 7. Decisions needed

| Q | Question | Recommendation |
|---|---|---|
| Q-S1 | Revert `fe00f36` entirely or fix forward? | Fix forward (TS01, TS03, TS04, TS10). The commit is 89 files; TS10 audits it precisely instead of discarding it. |
| Q-S2 | `MoveDeleteQueue_...` asserts the Q-T1 queue behavior that production does not implement. Implement the queue (new behavior task touching INV-4) or skip the test with a reason until then? | Skip with a reason now; open a separate behavior task for the queue. |
| Q-S3 | Convert the journal wall-time assertion into a bounded-work assertion plus a generous backstop (TS05)? | Yes; it strengthens determinism and keeps the intent. |
| Q-S4 | Delete today's leaked `%TEMP%` folders (10 x 389 MB and 17 small TC05 dirs, all created by test runs)? | Yes, in TS02 with a listed command. |

## 8. Not covered

No production behavior change (including the `IsBusy` drop); no lowering of any timeout or threshold to obtain a pass; T89 GUI acceptance; the remaining OC/WD/IO work.
