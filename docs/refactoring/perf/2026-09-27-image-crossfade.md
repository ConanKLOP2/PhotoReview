# PR-D image-crossfade (2026-09-27; F4 = 2058 files, 18.9 GB; `-Profile quick -FixtureAlias F4 -Repeat 3`)

`ImageTransition` has no CLI override in the harness, so `ImageTransitionMs`/`ImageTransition` were set in the real
`%LOCALAPPDATA%\PhotoReview\config.json` between runs (restored after). Same branch build both times (only the setting
differs), so this compares `None` (branch, functionally the same code path as master) against `Fade` 120 ms; both runs
were after the full test-suite/other perf runs on this box had fully exited (an interleaved first attempt showed P95 in
the 1500 ms range with dozens of timeouts -- CPU contention, discarded).

| Scenario | Metric | `None` | `Fade` 120 ms |
|---|---|---:|---:|
| S2 next (40 keys, realistic pace) | key→present P50 (3 runs) | 14.4 / 14.7 ms (run 1 discarded: warm-up P95 outlier) | 10.5 / 10.8 ms |
| S2 next | key→present P95 (2 clean runs) | 24.9 / 97.6 ms | 19.7 / 20.9 ms |
| S3 burst (30/s x200, holds a key) | avg `PresentMilliseconds`/image (3 runs) | 3.15 / 3.55 / 3.60 ms | 7.39 / 8.27 / 8.39 ms |

**Reading this:** at a realistic browsing pace (S2), `Fade` shows no regression -- P50/P95 are the same or lower than
`None`, within this box's run-to-run noise (see the discarded outlier). At a sustained 30 keys/s burst (S3, i.e. key
auto-repeat held down), each `Fade` present costs ~4-5 ms more on average: `StartImageFade` (`MainWindow.xaml.cs`) runs
synchronously inside `PresentAsync`'s call stack (reading `MainImage.ActualWidth`/`ImageScroll` offsets, allocating a
`DoubleAnimation`, one `BeginAnimation` call) every time the photo changes while `Fade` is on. This does not delay
presentation of the new image itself (the swap to the new bitmap is unchanged; only the outgoing layer is animated
afterwards, off the critical path for what the user sees next) and `HandoffBehavior.SnapshotAndReplace` still cancels
a running fade instantly under this exact burst -- but the extra per-present setup cost is real and, being
inline/synchronous, shows up in `PresentMilliseconds`. Acceptable because `Fade` is opt-in (default `None`) and S3's
30/s auto-repeat is an extreme case; a future optimization (reusing one `DoubleAnimation` instance instead of
allocating one per navigation) could reduce this further if it matters in practice.

`None` was not compared against unmodified `master` directly (git checkout of a second build was out of scope for
this measurement pass); `None`'s code path adds one cheap `ImageTransitionDecision.ShouldTransition` bool check per
`UpdateCurrentImage` call and returns before touching any WPF element (`OutgoingImage` stays `Visibility.Collapsed`,
no animation clock), so no measurable difference from `master` is expected and none was observed against noise.
