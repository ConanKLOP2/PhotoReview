# Validation — review checkpoint `8952067d` (2026-10-04)

The exhaustive local gate recorded below was run on the earlier pinned baseline `5d291076`; it is historical evidence only and does not validate PRs #290–#318 or current master `8952067d`. No build or test suite was run against `8952067d` as part of this continuation. The current work is a static source/test-oracle review, with the coverage boundaries recorded in [WORK-REVIEW.md](WORK-REVIEW.md).

At `8952067d`, the Roslyn inventory completed successfully: 1,068 C# files and 16,795 callable bodies. Current-head delta review facts: App/Integration reviewed 42 changed paths (`7fe54c35..91a21240`) with one surviving static D-03 race; Core/PerfAnalysis/Benchmark CLI reviewed 27 production and 15 Core test files changed in `91a21240..8952067d`, finding output-symlink write-through and retaining R11; the Imaging lane found no direct Imaging source/test changes in that delta and checked three changed PerfAnalysis callables plus four tests. These are bounded delta findings, not whole-project semantic sign-off. No real Recycle Bin, symlink target, large allocation, RAW corpus, native or GUI scenario was exercised.

PR #284's latest remote CI result at the time of this checkpoint belongs to its old remote head `c41043c3`, not the rebased/current review contents: build-test-publish, repo-checks and tag-version passed there. Those results must be refreshed by CI after pushing the updated branch.

All runs on this Windows machine, SDK 10.0.401. WPF builds and tests were serialized for final validation. UI tests ran through the hidden desktop helper; Manual/Native/Slow were excluded by the default gate, with gate-selected integration slow checks following the script. No real Recycle Bin mutation, real NAS/perf run, RAW corpus acceptance or human GUI acceptance was performed.

## Earlier baseline run

`tools/verify-all.ps1 -Hidden -TestReport`: Architecture 65 passed; Core 2,552 passed; Imaging 2,786 passed; Integration 739 passed and 1 failed (`OpenForwarded_ClosedWindow_IsDroppedWithoutThrowing`, STA 30-second timeout). Gate stopped before App/docs/publish. The failed Integration test passed alone on bounded hidden retry (1/1, about 4 seconds); this does not make the original full gate green.

Native bootstrap initially failed because MSBuild's Windows PowerShell inherited a PSModulePath lacking its standard modules (`Get-FileHash` unavailable). Direct fetch succeeded; a later solution Release build with Windows PowerShell's module directories restored finished with 0 warnings/0 errors. One mistakenly overlapping build hit locked test DLLs; it was discarded and a serialized build completed. Native license line-ending changes introduced by fetch were restored.

## Finding-specific evidence

| Check | Outcome | Limit |
|---|---|---|
| Single/group/recovery foreign destination Copy | 3 observed-bug assertions passed, lead rerun | Fake filesystem; regression must assert survival |
| Queued Move across folder load | Desired-behavior assertion failed | Fake services + own temp fixtures; no recycle execution |
| Superseded Fit correction | Desired-behavior assertion failed: 2.0 expected, 0.4 actual | Fake viewport; no visual GUI check |
| NaN font / keyboard setting parsing | Lead rerun: 2 observed-bug assertions passed | Worker-built baseline DLL; fake store; exact source preserved |
| Memory fallback admission | Fallback calls=1 after refusal | Source slices/stub decoders; no native allocation/OOM claim |
| Cache temp ownership | Pre-existing own temp deleted after CreateNew failure | Source slice; default random-name collision rare |
| Ancestor junction publish guard | Refused=false, no marker | Own fixture; destructive action never invoked |

Added compiled repro test files were removed before the clean baseline gate. Repro sources are retained as `.cs.txt` evidence.

## Clean baseline rerun

Release build: 0 warnings, 0 errors. Architecture 65 passed; Core 2,552 passed; Imaging 2,786 passed; Integration 739 passed / 1 failed. The same `OpenForwarded_ClosedWindow_IsDroppedWithoutThrowing` timed out at 30 seconds again in the full Integration suite. Gate exit=1; no full-gate PASS. Source trace shows the test awaiting an ApplicationIdle dispatcher callback after closing its window; suite interaction/root cause remains unproven. Previous isolated retry passed; repeating the whole suite a third time without a diagnostic change is not justified.

Separately completed after the gate stopped: App default suite 1,757 passed; Integration+Slow 10 passed (other projects had no matching tests); doc links 0 broken/0 stale; T0 budget 16.7/18 KB, no oversized T1 file; generated decision table, i18n self-test/catalogs and publish-guard self-test passed. Framework-dependent publish into a new, previously absent worktree output directory succeeded; verify-release passed required files/version/native payload checks. No publish cleanup was invoked. These checks do not replace the failed Integration gate. Baseline Native/Slow exclusions also mean the real pipe round-trip tests were not exercised; source review is recorded separately.
