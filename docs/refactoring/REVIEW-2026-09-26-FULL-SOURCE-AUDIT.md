# Review 2026-09-26 — Full source audit

**Base:** `master` `1c84c59` (`v2.0.101`, PR #97 merged after #96) · **Branch:** `codex/review-source-2026-09-26` (pushed to origin; not in master as of 2026-09-26 fetch)

## Scope and method

- Three independent reviewers covered Core/FileActions, App/WPF, and Imaging/Platform/Benchmarking/PerfAnalysis/tools/CI plus related tests. The lead checked the localization generator, composition root, project/build configuration, and consolidated source evidence.
- Reviewed the `src/` and `tools/` inventory (288 files) and tests inventory (351 files) by project ownership. The initial lanes reviewed the then-current `becdce1`; the merged PR #97 delta was re-reviewed against `1c84c59`. Static review only; no build, test suite, GUI, perf run, source edit, or user-data operation was performed.
- Current `origin/master` confirms PR #97 merged at `1c84c59`; PR #96 is its first-parent predecessor. Reviewers checked the source against the recorded Q-R decisions and avoided reporting old performance estimates as measured facts.

## Findings

### F0 · P2 · Stat errors can leave an old comparison pair visible under a new file error

**Locations:** `src/PhotoReview.App/Coordinators/ImagePresenter.cs:196-214`, `src/PhotoReview.App/ViewModels/CompareViewModel.cs:131-161`, `tests/PhotoReview.App.Tests/Coordinators/ImagePresenterTests.StatErrors.cs:117-130`. PR #97 introduced this behavior.

On navigation, `ImagePresenter` only calls `CompareViewModel.Select(null)`, which clears the selected side but leaves comparison visibility, paths, and images intact. When the newly selected file then returns a transient stat error, the error branch clears the main image and changes the status but does not clear the comparison. The old A/B pair therefore remains visible while the status names an unreadable different file. The PR #97 regression test sets and checks only the main image, not a pre-existing comparison. **Confidence: high.**

**Follow-up:** clear the comparison on the stat-error path and cover a visible prior pair.

### F1 · P2 · Compare mode clears both photos on hash failure

**Locations:** `src/PhotoReview.App/Coordinators/ImagePresenter.cs:387-410`, `src/PhotoReview.App/ViewModels/CompareViewModel.cs:217-229`, `src/PhotoReview.App/FileHashService.cs:42-62`.

With hash comparison enabled, `CompareViewModel.LoadAsync` first loads both previews, then awaits both hashes. A hash can fail if the file changes during hashing or becomes unreadable. `ImagePresenter` catches any non-cancellation exception from the whole comparison operation, clears the comparison, restores the active image alone, and reports that the partner image failed. Thus a hash-only failure discards two successfully decoded images and mislabels the error. Existing tests cover successful hash comparisons and missing partners, but not a hash failure after both previews load. **Confidence: high.**

**Follow-up:** separate hash-status failure handling from preview/decode failure so valid previews remain visible; add a focused fault-path regression test.

### F2 · P2 · Forwarding truncates launches with more than 16 paths

**Locations:** `src/PhotoReview.Platform.Windows/InstanceForwardClient.cs:29-33, 61-63`, `src/PhotoReview.Core/Instance/ForwardedPathProtocol.cs:18-26, 43-45`.

When a second launch supplies over `ForwardedPathProtocol.MaxPaths` photos, the client truncates the list to 16 before encoding. The owner accepts those paths and the client can report `Delivered`; every remaining selected photo is silently dropped. The protocol limit is enforced on receive, but the forwarding client converts overflow into a successful partial request. Pipe tests cover 1, 2, and 6 paths, not the 16-path boundary. **Confidence: high.**

**Follow-up:** reject overflow with a visible/fallback outcome or implement bounded batching; test the exact limit and over-limit behavior.

### F3 · P2 · A size-mismatched moved file is reported as failed after the source disappears

**Locations:** `src/PhotoReview.Core/FileActions/JournalTransaction.cs:71-75`, `src/PhotoReview.Core/FileActions/FileActionService.cs:153-157, 232-248`.

If the source changes size between the initial stat and the move, the move can remove the source but produce a destination whose size differs from the prepared journal size. `VerifyMoved` then throws on destination verification before setting `MutationCompleted`; `ExecuteAsync` reports failure. The caller can restore the now-missing source path to the review catalog even though the file remains in the destination. The test `FileAction_SizeChangedAfterMove_JournalsCode_ResultShowsUiLanguage` checks the reported error but not source/destination/catalog consistency. **Confidence: high.**

This behavior was already listed as unresolved in `docs/refactoring/REVIEW-2026-09-25-ROUND3-4.md` (round 6); this pass reconfirmed that the current implementation still has the issue.

**Follow-up:** define post-move integrity semantics when the source is gone, then test journal outcome and catalog recovery together. Avoid silently treating the changed destination as either a clean success or a no-op.

### F4 · P2 · An older preload snapshot can overwrite the cached source-size total

**Locations:** `src/PhotoReview.Core/Catalog/SourceSizeTracker.cs:58-88`, `tests/PhotoReview.Core.Tests/Catalog/SourceSizeTrackerTests.cs:188-206`.

In observed mode, `GetTotal` snapshots `_observed` under the lock, sums outside the lock, then commits the result. If call A starts summing snapshot A; `Observe(B)` runs; call B sums and commits B, setting `_observedDirty = false`; then A commits late, it overwrites `_cachedTotalBytes` with A but leaves the dirty flag false because its snapshot reference no longer matches. Future `GetTotal()` calls for B return A's total as a cache hit. The preload scheduler uses this total in its whole-folder memory estimate, so a folder switch can make the scheduler choose based on the previous snapshot's source size. The existing `Observe_WhileGetTotalIsStatting_DoesNotBlock` test verifies `Observe` is not blocked, but not two overlapping totals or which snapshot's result remains cached. **Confidence: high.**

**Follow-up:** only commit a computed total when its snapshot is still current; add a deterministic interleaving test proving late A cannot replace B's cached total.

## Validation and continuation

- No tests/build/GUI were run, per review-only scope. Findings are based on source control flow and test inspection; native lifecycle and performance behavior remain unverified.
- No product code changed. Review document and handoff status only.
- Recommended order: F3 (catalog/file consistency), F2 (silent dropped user input), F4 (stale preload decision), then F0/F1 (compare resilience). Each fix should be isolated and validated with a targeted regression test plus the required full solution gate before commit/PR.

## Follow-up (Claude, 2026-09-26)

Each finding was re-checked against `master` `1c84c59` and decided with the user:

| Finding | Verdict | Action |
|---|---|---|
| F0 stale comparison under a stat error | real, low (UX) | fix: branch `fix/audit-f4-f1-f0` |
| F1 hash failure discards both previews | real, low (UX) | fix: same branch |
| F2 more than 16 forwarded paths | **not a defect**: the owner opens only the first forwarded path (`ForwardedOpenCoalescer`), the trimmed paths were never used | comment in `InstanceForwardClient` + pipe test pinning the behaviour |
| F3 size-mismatched move reported failed after the source disappeared | real, highest (catalog consistency) | user chose option B: stays Failed/unverified, journal Failed (Recovery), catalog drops the missing source, no rollback: branch `fix/f3-move-size-mismatch-catalog` |
| F4 late snapshot overwrites the cached total | real, low; a regression of the stat-outside-lock change made in the 2026-09-26 review wave | fix: same branch as F0/F1 |
