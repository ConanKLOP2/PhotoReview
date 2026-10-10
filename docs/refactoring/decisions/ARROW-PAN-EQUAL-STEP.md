---
id: ARROW-PAN-EQUAL-STEP
order: 392
summary: |-
  One arrow press now moves a zoomed image the SAME number of pixels horizontally and vertically: ArrowPanStepPercent % of the viewport's SHORTER side (was: the viewport size along the pressed axis). Reverses an earlier "not doing this" decision; applies to the instant and the kinetic arrow pan; no new setting.
---

# ARROW-PAN-EQUAL-STEP — equal arrow-key pan step on both axes (2026-10-10)

## User report

A tester: with the step at 17 % ("Arrow key step (zoomed image)"), a 24 Mpx 3:2 landscape photo needs 7 presses to cross
horizontally and 9 vertically; a 2:3 portrait photo 3 horizontally and 16 vertically. "Moving the same number of pixels
horizontally and vertically would feel better."

## Verified cause

`KeyboardPan.Step` used `ViewportWidth * p` for Left/Right and `ViewportHeight * p` for Up/Down (`p` = `ArrowPanStepPercent`
/ 100). On a landscape window `W > H`, so a horizontal press was always longer than a vertical one, whatever the photo's
orientation. The kinetic path (`KeyboardPan.ImpulseVelocity`) is sized from that same clamped step, so it inherited it.

## Decision (reverses an earlier one)

Earlier this was considered and not done (equal step was left out). The project owner reversed it: the step is now

    step px = ArrowPanStepPercent / 100 * min(ViewportWidth, ViewportHeight)      (both axes)

in ONE pure function, `KeyboardPan.StepPixels(viewportW, viewportH, stepFraction)` (App/Input/KineticPan.cs). The no-WPF
engine (WP-16) must copy exactly this rule. Unchanged: the clamp at the scroll edges, `NotScrollable`/`AtEdge`
results, the kinetic friction/time constant and `ImpulseVelocity` (only the reference quantity changed, so a kinetic glide
still lands within ~1 DIP of the step). The setting's range (1..100), default (10) and storage are unchanged; only its
hint text (en/vi) says "% of the SHORTER side of the view; same pixels horizontally and vertically".

## Consequences

- On a landscape window the horizontal step gets shorter (by H/W, e.g. 1080/1920 = 56 % of the old length); the vertical
  step is unchanged. Users who tuned 10-17 % will see a shorter horizontal step and the same vertical one; raising the
  percentage gets the old horizontal distance back.
- On a portrait window the opposite: the vertical step shortens (to W/H of the old), the horizontal is unchanged.
- At 100 % one press moves one short side, not the whole viewport width.

## Press counts (zoom 100 %, viewport 1920x1080 DIP, step 17 %, scroll range = image size - viewport, ceil)

| image | axis | before | after |
|---|---|---|---|
| 6000x4000 (3:2) | horizontal | 13 | 23 |
| 6000x4000 (3:2) | vertical | 16 | 16 |
| 4000x6000 (2:3) | horizontal | 7 | 12 |
| 4000x6000 (2:3) | vertical | 27 | 27 |

Step: before 326.4 px horizontal / 183.6 px vertical; after 183.6 px both.

## Tests

`KineticPanTests` (`StepPixels_*`, `Step_BothAxes_MoveTheSamePixels`, `ImpulseVelocity_OfEqualSteps_*`),
`PointerInputControllerTests` (`Arrow_Instant_HorizontalAndVerticalStepsAreTheSamePixels_*`,
`Arrow_Kinetic_HorizontalAndVerticalGlidesTravelTheSamePixels`); older arrow tests were updated to the short-side step.
