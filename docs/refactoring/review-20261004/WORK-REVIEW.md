# Function review — 2026-10-04 (incomplete)

Baseline: `5d2910761f14bd99745b9c4bdcd636b36263e716` (`origin/master`, PR #280). Review branch: `codex/review-all-20261004`; [draft PR #284](https://github.com/ConanKLOP2/PhotoReview/pull/284), open/not merged at handoff. No production fixes in this PR. Three isolated workers reviewed App, Core and Imaging; lead covered integration evidence and tooling. Workers inherited the parent model; its actual model name was unavailable. All three workers stopped at the account usage limit before finishing the test/source review.

## Coverage contract

The Roslyn inventory covers 15,690 callable bodies in 1,018 tracked C# files: methods/constructors/operators with bodies, explicit accessors, expression-bodied properties/indexers, local functions and lambdas. Declaration-only interfaces/extern members, implicit record members, XAML event declarations and the outer top-level statement body are excluded. Nested bodies have individual rows. This is an inventory, not semantic sign-off.

[functions.tsv](functions.tsv) records ID, path, baseline line/end line, kind, signature, status and rationale. `STATIC-ONLY` means a saved worker semantic source review, including inherited enclosing-function rationale for small helpers/lambdas; it does not mean runtime PASS. `ISSUE` identifies affected source bodies, not a count of independent bugs. `UNREVIEWED` includes partially read functions whose completed rationale was not saved. Broad-suite pass does not change these statuses.

Saved coverage: App 1,203/1,203 source bodies; Imaging family 694/694 source bodies. Total saved rows: 1,927 STATIC-ONLY, 20 ISSUE, 13,743 UNREVIEWED. Core worker reported production reading progress, but did not save a complete source ledger before interruption; its ledger remains UNREVIEWED except for 14 SessionWriter bodies independently reviewed by the lead. Lead also saved 36 Platform instance-forwarding/identity/ownership bodies; remaining Platform bodies are pending. Selective relevant tests were read, but no exhaustive test-body ledger was completed. [App details](app.md).

| Area | Source bodies | Test bodies | Saved semantic ledger |
|---|---:|---:|---|
| App + Integration | 1,203 | 4,820 | All source; selective test oracles only |
| Core | 748 | 3,207 | Lead SessionWriter 14; worker full ledger pending |
| Imaging + Raw + LibRaw + TurboJpeg | 694 | 3,968 | All source; selected test oracles only |
| Platform.Windows | 139 | included in Integration | 36 instance bodies; remainder pending |
| Benchmarking | 136 | included in Integration | Pending |
| PerfAnalysis | 167 | included in Integration | Pending |
| Localization.Generator | 54 | included in Integration | Pending |
| Benchmark.Cli | 256 | included in Integration | Pending |
| Architecture.Tests / TestSupport / TestSupport.Windows | — | 198 / 67 / 33 | Pending |

[powershell-functions.tsv](powershell-functions.tsv) additionally inventories 52 named PowerShell functions in 15 scripts; script-level statements and CI YAML are not callable rows. Lead traced publish guard and reviewed selected tooling/docs, but 5 publish-guard functions have saved source rationale (2 ISSUE); exhaustive tooling/CI/docs semantic coverage is pending. Native ABI, real WPF interaction, RAW corpus, NAS/performance, actual Recycle Bin, and full mutation campaign are not validated by this review.

## Findings requiring fixes

### R01 — P1 — Copy cleanup can delete a foreign destination

`src/PhotoReview.Core/FileActions/FileActionService.cs:186-187,543-544,654`; `RecoveryRetryService.cs:271-275`; `PartialDestinationCleanup.cs`.

Ownership is set before `TryCopyNew` has created anything. If another process creates the destination and the source vanishes before the copy opens its source, `TryCopyNew` throws FileNotFoundException instead of returning collision=false. Single/recovery cleanup infers ownership from shorter length; group compensation deletes the in-flight destination outright. A foreign file can therefore be deleted. Safe fake-file-system repro covers all three paths, 3/3 observed-bug assertions passed on the pinned baseline. It never uses real photos or the real bin. Existing destination-collision tests keep the source present and miss this pre-creation exception. A separate own-temp Windows File.Copy probe confirmed that missing-source plus existing-destination throws FileNotFoundException (HResult -2147024894), leaving the foreign file intact before application cleanup.

Remedy: propagate proof of successful creation from the copy implementation; only clean up a destination owned by this operation. Length or preflight absence cannot establish ownership. Add regressions that assert the foreign file survives.

### R02 — P1 — Queued file commands act on a newly opened folder

`src/PhotoReview.App/ViewModels/MainViewModel.cs:534-547,1193-1197` (`RunActionAsync`, `RecycleAsync`, `MoveOrCopyToFolderAsync`). A closure queued in folder A reads the current catalog only when dequeued. Block Move #1, enqueue Move #2, open B, release #1: #2 moves B's photo. Desired-behavior repro failed with "An action queued in folder A moved folder B's photo." Same design exposes queued recycle commands, although the runtime repro used Move. Existing tests cover same-folder FIFO or a single action during folder switch.

Remedy: capture folder-load identity at submission and reject stale queued commands; preserve live selection for repeated commands within the same folder. The action generation changes for each action and cannot serve as folder-load identity.

### R03 — P1 — Publish guard misses an ancestor junction

`tools/Publish-Guard.ps1:38-61,77-85`. `Resolve-FinalTarget` follows a link only at the leaf. An approved-root child with an ancestor junction can resolve to an unmarked external `publish` directory and still pass the lexical ownership check. `verify-all.ps1` then has authorization to recursively remove that external output.

Safe own-fixture probe returned `Refused=false`, `MarkerPresent=false`; no destructive publish action was executed. Its junction and temp fixtures were cleaned up. Current self-test covers a junction at the publish leaf, not an ancestor. Remedy: resolve every existing path component, then check actual destination/root containment and marker ownership before deletion.

### R04 — P2 — A superseded Fit overrides a newer zoom

`src/PhotoReview.App/Input/PointerInputController.cs:223-224,234-235,257-258,280-285`. The first zoom pass recognizes a newer operation and returns, but callers still run scrollbar correction, which creates a fresh version and reapplies the obsolete Fit. Deterministic held-render-yield repro expected zoom 2.0 and observed 0.4. Initial probe had a fake collection-modification error; after disabling held yields before releasing them, the assertion reached the actual bug. Remedy: carry the original operation success/version through correction and reject stale correction.

### R05 — P2 — Non-finite font configuration fails startup repair

`src/PhotoReview.Core/Settings/SettingsNormalizer.cs:195-199`; `SettingsStore.cs:121,181-190`. JSON string `"NaN"` is accepted, `Math.Clamp` retains NaN while recording a repair, and repair persistence throws ArgumentException during serialization. `Load` does not catch that exception. Worker fake-store repro passed the observed-bug assertion; lead independently reran the two settings probes against the worker-built baseline DLL (2/2 passed, 256 ms). App font-size input also lacks an explicit finite check. Remedy: normalize non-finite doubles to a documented default before clamping/serialization and reject NaN/Infinity at UI input.

### R06 — P2 — Output memory refusal enters unguarded decoder fallback

`src/PhotoReview.Imaging/Decoding/Wic/WicDirectDecoder.cs:297`; `TurboJpegDecoder.cs:517`; `FallbackImageDecoder.cs:91`; `ImageDecoderFactory.cs:88`. Output headroom refusal throws InvalidDataException, which fallback classifies as a codec error. Default WIC composition then calls WPF, whose decoder has no equivalent headroom admission. Source-slice probe with real guard/classifier and stub decoders reported fallback calls=1; it does not prove an actual OOM or native allocation. Remedy: distinguish admission refusal from corrupt-image failure and preserve it across fallback, or enforce the same admission in every backend.

### R07 — P2 — Cache temporary cleanup deletes a file it did not create

`src/PhotoReview.Imaging/Caching/AtomicCacheFile.cs:43-60`. When `CreateNew` throws because a temporary path already exists, `finally` still deletes that path. Source-slice own-fixture probe: CreateNew threw=True, original temp survives=False, target exists=False. Default GUID collision is rare; caller-supplied temporary names/test seams make the ownership contract visible. Existing pre-existing-temp oracle checks target absence but misses temp survival. Remedy: track successful stream creation and clean up only the operation-owned temporary file.

### R08–R10 — P2 — Source-traced App defects; runtime regressions pending

- R08: `FileActionController.cs:401-410,423-432,446-454` checks folder once before awaiting next-image presentation, then writes old action failure/status or unverified-Move session state into a newly opened folder. Recheck folder identity after each await. Success warning path already does so.
- R09: `MainViewModel.cs:581-599` reopens Undo's source folder after a concurrent user folder open, even when controller correctly drops stale catalog changes. Recheck caller's folder-load identity before reopen and subsequent status update. Existing mid-Undo test uses same-folder Move and misses Recycle/prior-folder Move.
- R10: `ImagePresenter.cs:130-151,777-791` clears `_presentedFilePath` on same-file full-resolution bitmap swap. The next navigation skips enabled Fade because previous identity is null. Preserve file identity during same-source swaps. No visual acceptance was performed.

### Candidates that need controlled evidence

- `PreviewImageService.cs:262-283`: two persistence workers across cache epochs can write the same cache path; stale worker cleanup may delete a newer worker's file. Cache performance/completeness impact, not source-photo deletion; needs controlled two-worker interleaving.
- `SessionWriter.cs:76-85,94-105,202-238`: bounded wait covers lock acquisition/in-flight writes, but an acquired lock performs synchronous `_store.Save`; a newly stalled save may exceed the shutdown budget. Needs a blocking fake with event-driven release and proof of UI call path; not claimed runtime-confirmed.
- `KeyboardZoomStepPercent` is not normalized (worker observed int.MaxValue survive parsing); negative values reverse keyboard zoom direction at the App call site. Bounds/desired default and a regression should be defined before changing behavior.

## Validation and continuation

[Validation record](validation.md) separates baseline gate runs, added repro tests and source-slice probes. Evidence scripts/sources are in [evidence](evidence/README.md); .cs.txt files are deliberately outside compilation. Workers did not alter production source. Lead removed all temporary compiled repro tests before the clean baseline gate.

Next: finish the Core ledger, semantically review all test bodies and lead-owned source/tooling/CI/docs, reproduce static candidates, then implement findings in separately authorized fix work. Keep the pinned baseline and stable IDs; after master changes, create an explicit delta inventory/review rather than treating stale line numbers as current. Preserve worker worktrees for continuation; do not report this audit as DONE or move it into HISTORY.
