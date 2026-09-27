---
id: CI-CROSSFADE-ANIMATION-FLAKE
order: 20
summary: |-
  Confirmed via real CI diagnostics the compositor ticks fine on `windows-latest`; the fixed-poll assertion could miss a fast 40ms fade under load -- replaced it with a value-changed watcher (no source change).
---

# CI-CROSSFADE-ANIMATION-FLAKE — `ImageCrossfadeIntegrationTests` fade-completion flake on GitHub Actions

## Symptom

`ImageCrossfadeIntegrationTests.NavigatingToADifferentFile_WithFadeEnabled_AnimatesTheOutgoingLayerThenReleasesIt`
(added by #207) failed deterministically on GitHub Actions' `windows-latest` runner on two separate real runs
right after #207 merged:

- Run [36326070302](https://github.com/ConanKLOP2/PhotoReview/actions/runs/36326070302) (master, right after #207 merged)
- Run [36326944042](https://github.com/ConanKLOP2/PhotoReview/actions/runs/36326944042) (PR #209)

Both failed at the same assertion (`ImageCrossfadeIntegrationTests.cs:83` at the time):
`Assert.True(await StaTestHost.WaitForAsync(() => window.OutgoingImage.Opacity < 1.0, FadeTimeout), "The fade
animation never started.")`, each taking exactly the full 3s `FadeTimeout`. The test passes reliably on the
local dev machine. This blocked CI for every PR merged after #207.

## Hypothesis and how it was checked

Hypothesis going in: GitHub-hosted `windows-latest` runners might not tick WPF's composition/render thread
(`CompositionTarget.Rendering`) reliably without a real desktop/compositor session, so `BeginAnimation`'s clock
would never advance at all.

This was **not** assumed — it was checked with real GitHub Actions evidence. A throwaway diagnostic branch
(`diag/crossfade-ci-flake`, PR #210, closed after evidence was gathered) added temporary instrumentation to the
test (`RenderCapability.Tier`, a `CompositionTarget.Rendering` tick counter, `OutgoingImage.Opacity` /
`HasAnimatedProperties` logged via `ITestOutputHelper` at each poll stage) and to the CI workflow (detailed
console logger, an 8x repeat of the fade test). Three real CI runs on that branch:

- Run 36328276611: the fade test **passed** — proving the failure is a flake, not hard-deterministic.
- Run 36328606813: failed a different, pre-existing gate (R2-A-12, the diagnostic step's `dotnet test` command
  didn't carry the shared `TEST_FILTER`) — fixed and re-pushed.
- Run [36328880295](https://github.com/ConanKLOP2/PhotoReview/actions/runs/36328880295): the fade test failed
  again, this time with full diagnostics captured:

  ```
  RenderCapability.Tier=0x00020000 (hi word = tier)                                  # Tier 2: full HW acceleration
  Immediately after fade start: Opacity=1, HasAnimatedProperties=True, RenderingTicksSoFar=3
  After fade-start wait (00:00:03): fadeStarted=False, Opacity=1, HasAnimatedProperties=False,
    RenderingTicksSoFar=204, Visibility=Collapsed
  ```

## Root cause (confirmed, not guessed)

The hypothesis was **refuted**: `RenderCapability.Tier` reports full hardware acceleration and
`CompositionTarget.Rendering` ticked ~201 times over the 3s wait (~67 Hz) — the WPF compositor was never idle
and animation clocks do tick on this runner.

The real cause is a race between the test's own poll and an unusually short animation:

- The shared test helper (`WithTwoImageWindowAsync`) sets `Settings.ImageTransitionMs =
  AppSettings.MinImageTransitionMs` (40ms) "to keep the test quick".
- `StaTestHost.WaitForAsync` polls at `DispatcherPriority.Background` with a 10ms `Task.Delay` between checks.
- `StartImageFade` (`MainWindow.xaml.cs`) starts the fade with `HasAnimatedProperties=True`; when it completes,
  `OnImageFadeCompleted` removes the animation clock (`BeginAnimation(OpacityProperty, null)`) and sets
  `Visibility = Collapsed`. Removing the clock reverts `Opacity` to its pre-fade **base value**, which was set
  to `1.0` at the start of the same call — so once the fade is done, `Opacity` reads back `1.0`, not `0.0` (this
  is expected WPF behavior, not a bug: the layer is `Collapsed` by then, so it's never visibly wrong, and the
  next fade explicitly resets `Opacity = 1.0` anyway).
- On a loaded/contended GitHub Actions runner, the entire 40ms animation (start → `Completed`) can run to
  completion inside a single gap of the test's `Background`-priority poll loop. Because completion reverts
  `Opacity` to `1.0`, the poll for "`Opacity < 1.0`" never observes a true value at any sampled instant — even
  though the animation genuinely ran (confirmed by `HasAnimatedProperties` flipping to `True` then back to
  `False`, and `Visibility` already `Collapsed`, between two consecutive polls).

This is a **test-timing flake**, not a CI-environment rendering limitation and not an application bug.

## Fix

**First attempt (insufficient, disproved by real CI, kept here for the record):** widening
`Settings.ImageTransitionMs` to 200ms for just this test. This was pushed to `fix/ci-crossfade-animation-flake`
and **still failed on real CI** (run
[36329663970](https://github.com/ConanKLOP2/PhotoReview/actions/runs/36329663970), same "The fade animation
never started." after the full 3s `FadeTimeout`). This proved duration alone doesn't fix it: the fade only has
one ~200ms window near the start of the whole 3s wait, and if the test's own `Background`-priority poll
(`StaTestHost.WaitForAsync`) is starved for a stretch longer than that on a loaded, resource-shared CI runner,
it can still miss the entire window — a longer timeout doesn't help because the opportunity to observe
`Opacity < 1.0` doesn't get longer, only the dead time around it does.

**Actual fix:** replace the point-in-time poll for `Opacity < 1.0` with a value-changed watcher (`OpacityWatch`,
mirroring the existing `VisibilityWatch` used by the other two tests in this file), registered on
`window.OutgoingImage` **before** the navigation is triggered. `DependencyPropertyDescriptor.AddValueChanged`
fires from the same per-frame property-invalidation path that updates the animated value for rendering, not
from this test's poll loop, so it cannot miss a transient dip below `1.0` regardless of how the CI runner
schedules the test's own polling. The assertion changed from "poll and see `Opacity < 1.0` at some sampled
instant" to "was `Opacity` ever observed below `1.0`, however briefly, by the time the fade fully completed" —
strictly stronger, not weaker: it still requires a real WPF animation to have actually run. No change to
`Settings.ImageTransitionMs` was needed; the shared helper's fast `AppSettings.MinImageTransitionMs` (40ms) is
kept for all three crossfade tests.

- `NavigatingToADifferentFile_WithTransitionNone_NeverShowsTheOutgoingLayer` and
  `ZoomingTheSameImage_WithFadeEnabled_NeverStartsAFade` already used this same `VisibilityWatch` pattern and
  needed no change.

Mutation-check: temporarily removing the `OutgoingImage.BeginAnimation(OpacityProperty, animation,
HandoffBehavior.SnapshotAndReplace)` call in `StartImageFade` still makes the fixed test fail (now on "The fade
never completed (OnImageFadeCompleted)." since neither the animation nor its completion ever fires) — confirmed
locally before and after the fix.

Verified on real GitHub Actions CI (not just locally) on `fix/ci-crossfade-animation-flake`: see the PR for run
links; at least two consecutive green `build-test-publish` runs are required before merge.

## Alternatives considered

- **Tag the completion assertion `[Trait("Category","Manual")]`**: rejected — the root cause is a fixable test
  timing issue, not a genuine CI-environment inability to run WPF animations (rendering tier and tick rate are
  both healthy), so downgrading real CI coverage would be throwing away a working regression test for no reason.
- **Assert only the wiring via a code seam** (that `BeginAnimation` was called with the right values), dropping
  the completion-observation assertions: rejected for the same reason — CI can observe the real animation
  end-to-end once the assertion is decoupled from this test's own poll cadence, so there is no need to weaken it.
- **Raise `FadeTimeout`**: would not help — the failure was never about running out of time within the 3s
  window, it was about the entire animation completing between two polls of a fixed-cadence loop; a longer
  timeout does not create more opportunities to sample the one short animation window.
- **Widen `Settings.ImageTransitionMs` alone (200ms)**: tried first, disproved by real CI (run 36329663970,
  above) — the poll can still starve past a 200ms window under load, so this only reduces the failure
  probability, it doesn't eliminate the race.
