---
id: Q-R40
order: 27
summary: |-
  Mouse-release kinetic glide velocity is scaled by a new `KineticScroller.PointerReleaseSpeedFactor` (0.65) to feel calmer, without touching the already-measured `KineticGlideSmoothing.Predict` frame-timing default or the keyboard-panning impulse path.
---

# Q-R40 — mouse-release kinetic glide damping

## Report

User reported the mouse-drag-release inertia glide felt "chóng mặt" (dizzying) and asked for it to be
"chậm và mượt hơn" (slower and smoother). Repro is specifically drag-pan with the mouse then release and
watch the glide -- not keyboard arrow-key panning, which shares the same `KineticScroller` but a different,
deliberately-sized velocity input (see below).

The user separately noticed that switching `KineticGlideSmoothing` from `Predict` to `Off` in Settings felt
smoother on their machine. That toggle is left exactly as-is (default `Predict`, still switchable in
Settings) -- **not** because the report was dismissed, but because `docs/refactoring/perf/2026-09-26-kinetic-pan-glide.md`
already measured this exact tradeoff on this category of hardware (hybrid-GPU laptop, mixed 240/60 Hz):
`Predict`'s vblank-aligned stepping cuts judder from 43 to 12 frames/s and speed-error RMS from 0.69 to 0.11
on a 59.94 Hz monitor. Flipping the default away from `Predict` would silently regress that already-verified
fix for everyone else. The frame-timing algorithm (`GlideFrameClock`, `WindowsDisplayClock`, `VBlankEstimator`)
is untouched by this change.

## Decision

Add `KineticScroller.PointerReleaseSpeedFactor = 0.65`, applied only inside `KineticScroller.Start` (the
mouse-release path, called from `PointerInputController.StartKinetic`), multiplying the raw pointer velocity
before it is clamped/stored:

```csharp
var vx = Math.Clamp(-Finite(pointerVelocityX) * PointerReleaseSpeedFactor, -MaxVelocity, MaxVelocity);
var vy = Math.Clamp(-Finite(pointerVelocityY) * PointerReleaseSpeedFactor, -MaxVelocity, MaxVelocity);
```

This reduces both the perceived speed right after release and the total glide distance (still
`v0 * KineticScroller.TimeConstantMs` in the limit) proportionally, by about a third, without changing the
deceleration curve's *shape* or *duration* (the friction time constant `TimeConstantMs` is unchanged, so a
damped glide still decays exponentially to a stop over the same relative timeline, just covering less ground
starting from a lower speed).

**0.65** was chosen as a middle value in the requested 0.5-0.7 range: low enough to meaningfully calm a fast
flick (about 35% off both initial speed and total travel) while staying well clear of `StartVelocity`
(0.1 DIP/ms) for realistic flick speeds, so an ordinary release still glides instead of stopping dead.

`KineticScroller.AddImpulse` (the keyboard arrow-key path, via `PointerInputController.StartKineticImpulse`)
is **not** damped. `KeyboardPan.ImpulseVelocity`'s doc comment explains it already pads the impulse velocity
by `StopVelocity` specifically so a keyboard-triggered glide lands within about 1 DIP of the exact intended
step distance; damping that input here would reintroduce a systematic undershoot. This is mutation-tested:
`KineticPanTests.AddImpulse_IsNotDampedByPointerReleaseSpeedFactor` fails if `AddImpulse` is damped the same
way `Start` is (verified by temporarily applying the same factor to `AddImpulse` and confirming the test
catches it, then reverting).

## Example numbers

For a raw pointer release velocity of 1.2 DIP/ms (a moderate flick):

| | Before | After (x0.65) |
|---|---|---|
| Stored `VelocityX` (DIP/ms) | 1.2 | 0.78 |
| Total glide distance (`v0 * 325 ms`) | 390 DIP | 253.5 DIP |

Keyboard arrow-key panning is unaffected in either case (same `AddImpulse` velocity as before this change).

## Verification

Unit tests added/updated in `tests/PhotoReview.App.Tests/Input/KineticPanTests.cs`:
- `Start_ScrollVelocityIsOppositeThePointerAndCapped` updated for the new damped values.
- `Start_DampsTheRawPointerVelocityByPointerReleaseSpeedFactor` -- a release now stores exactly
  `rawVelocity * PointerReleaseSpeedFactor`.
- `Start_RealisticFlick_StillStartsAGlideAfterDamping` -- a moderate flick still starts gliding after damping.
- `AddImpulse_IsNotDampedByPointerReleaseSpeedFactor` -- regression guard, mutation-checked as described above.
- `Step_MovesWithVelocityAndDecaysExponentially` / `Step_GlideDecelerates_ThenStopsNearTheTotalDistance` updated
  to use the effective (damped) velocity in their expected-value math.

This is a subjective "feel" change. The user should re-test the built app themselves to confirm the glide now
feels slower/smoother on their machine -- it cannot be verified by unit tests alone.
