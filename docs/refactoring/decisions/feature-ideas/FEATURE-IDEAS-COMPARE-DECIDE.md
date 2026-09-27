---
id: FEATURE-IDEAS-COMPARE-DECIDE
order: 1000
summary: |-
  Four new feature ideas to help users decide faster when comparing similar photos (burst shots, near-duplicates, slightly different exposures/focus).
---

# FEATURE-IDEAS-COMPARE-DECIDE — Faster decision-making in Compare mode

**Brainstorm**: Four feature proposals to help users decide faster when comparing similar photos in the existing Compare side-by-side viewer (burst shots, near-duplicates, different exposures/focus). Each survived performance and conflict critique.

---

## Idea 1: Sharpness Quality Indicator (SURVIVED)

**User scenario:**
User compares two burst photos where focus shifted slightly. Wants to instantly see which one is sharpest to pick the best photo without examining both closely.

**Rough mechanism:**
- Add an optional "Analyze Sharpness" button in the Compare panel.
- Compute Laplacian variance (edge detection metric) on the 50% downscaled preview for each image.
- Display a simple visual: numeric sharpness score (0–100) or a bar indicator beneath each image.
- Result shows "Left: Sharp (92)" vs "Right: Blurry (58)" at a glance.

**Critique survived:**
- ✓ Performance: Only computed on-demand when user clicks "Analyze", not automatic. Computed on preview (50% scale), not full resolution. Laplacian is a single convolution pass, ~10–50 ms per preview.
- ✓ Conflicts: No conflict with Q-Z1 (zoom = source pixel), Q-R35–R40 (zoom/pan/keyboard decisions). Orthogonal UI feature in Compare panel.
- ✓ File safety: No new file format or metadata persisted. Pure compute metric displayed in UI.

**Complexity:** M (requires Laplacian edge-detection library, integration into CompareViewModel, UI indicator)

**Performance cost:** On-demand only (zero cost until user clicks). ~10–50 ms per pair when computed. No automatic scanning.

---

## Idea 2: EXIF Metadata Side-by-Side Comparison Panel (SURVIVED)

**User scenario:**
User compares two photos taken in slightly different conditions (e.g., burst with auto-exposure adjusting between frames). Wants to see ISO, aperture, shutter speed, and exposure bias side-by-side to understand why they differ without switching to a metadata editor.

**Rough mechanism:**
- Extend Compare panel to show a compact EXIF table below or beside the images.
- Display key fields: ISO, Aperture (f-number), Shutter Speed, Focal Length, Exposure Bias, Flash, White Balance.
- Format values clearly: "ISO 400 | f/4.0" left side, "ISO 100 | f/5.6" right side, differences highlighted (e.g., lighter background for changed fields).
- EXIF already cached in the app; no additional I/O.

**Critique survived:**
- ✓ Performance: Zero cost. EXIF metadata is already parsed and cached during preload (evidenced by hash computation in CompareViewModel). Just format and display.
- ✓ Conflicts: No conflict with any zoom/pan decisions. Orthogonal UI enhancement.
- ✓ File safety: No new metadata or file format. Uses existing EXIF data.

**Complexity:** S (EXIF fields already available; need table layout UI and value formatting/comparison highlighting)

**Performance cost:** None. Metadata already in RAM.

---

## Idea 3: Exposure Histogram Comparison (SURVIVED)

**User scenario:**
User compares two photos with different exposures (one slightly underexposed, one bright). Wants to see which one has better tonal distribution (is one clipping highlights or crushing blacks?) without zooming or opening a histogram editor.

**Rough mechanism:**
- Add a "Show Histogram" toggle in Compare panel.
- Compute brightness/luminance histogram (R+G+B average) on the downscaled preview (~256 bins).
- Display side-by-side histograms as a small bar chart beneath each image.
- Optionally flag if either image is clipping (first or last bin > 5% of pixels): "⚠ Left: clipping highlights".
- Hide by default to avoid clutter; user enables when comparing exposures.

**Critique survived:**
- ✓ Performance: Histogram is a single linear pass through preview pixels (~0.5–2 ms). Downscaled preview (e.g., 400×300) is already in RAM from preload. Can be computed on-demand or lazily cached.
- ✓ Conflicts: No conflict with zoom/pan decisions. Orthogonal UI feature.
- ✓ File safety: No new metadata. Pure visualization of pixel distribution.

**Complexity:** M (histogram computation, visualization, optional clipping flag, toggle UI)

**Performance cost:** Lazy on-demand, ~0.5–2 ms per pair. Can cache result in Compare state.

---

## Idea 4: Multi-Photo Burst Comparison (SURVIVED, but orthogonal scope)

**User scenario:**
User has a burst of 5 similar photos and wants to compare all of them at once to pick the best, instead of comparing 1 vs 2, marking a winner, then comparing winner vs 3 (tedious). Wants to see multiple photos simultaneously and click one to "mark as keeper".

**Rough mechanism:**
- Extend Compare mode to display 3–4 images in a grid layout instead of just 2 side-by-side.
- Allow user to configure: 2 (current), 3, or 4 images visible.
- Navigation: arrow keys cycle through adjacent sets of photos (e.g., if viewing 1–4, right-arrow shows 2–5).
- Clicking an image marks it as selected (highlight border).
- Context menu: "Keep This, Delete Others" action (or just navigate and use existing delete shortcuts).

**Critique survived:**
- ✓ Performance: No new computation. Just more UI elements rendered. Zoom/pan works the same on each preview.
- ✓ Conflicts: Potential conflict with Q-R32 "Arrow keys on a zoomed image only pan, never navigate" — would need to clarify: arrow navigation only happens at zoom = 100%, or a separate mode toggle. Not a blocking conflict, just needs design.
- ✓ File safety: No new metadata or file format.

**Complexity:** L (significant UI redesign: grid layout instead of side-by-side, state management for grid size, keyboard navigation, image cycling logic, visual feedback for selection)

**Performance cost:** None (renders downscaled previews already in memory).

**Note:** This is orthogonal to "faster decision per pair" (Ideas 1–3). It addresses "faster marking when you have 3+ candidates" but requires more implementation effort (L vs S/M). Trade-off: larger scope for broader burst-handling capability, but more code churn.

---

## Summary

| Idea | Complexity | Auto Cost | Dropped? | Reason |
|---|---|---|---|---|
| Sharpness Indicator | M | On-demand only | ✓ Survived | Directly helps burst-shot decisions; lazy computation |
| EXIF Side-by-Side | S | Zero | ✓ Survived | Instant value; no perf penalty; uses existing data |
| Exposure Histogram | M | Lazy (0.5–2ms) | ✓ Survived | Solves exposure-comparison moment; low cost |
| Multi-Photo Burst | L | None | ✓ Survived | Reduces 3+ burst workflow; orthogonal but valuable |

**Dropped ideas** (not listed above):
- **Image Similarity Percentage** — Less useful than Sharpness; users already know photos are similar if they entered Compare. Similarity would tell them "95% match" but not help them pick a winner (sharpness and exposure do).
- **Focus Map Visualization** — Too expensive (would require per-pixel depth estimation or edge-detection at full resolution, ~500ms–2s per image). Violates "fast review" principle (AGENTS.md: "Prioritize Review Speed").
- **Auto-Candidate Selection** — Would require pre-scanning all burst-group photos during preload, extending first-visual latency. Violates preload performance targets (Q-R17, Q-R26). Opinionated ranking also conflicts with user autonomy.

---

## Next Steps (if approved)

1. **Size and prioritize**: Which idea has the highest ROI? (S complexity = EXIF. M = Sharpness or Histogram. L = deferred unless burst workflow is a top user pain point.)
2. **Prototype**: Build Sharpness + EXIF first (quick win: EXIF costs nothing, Sharpness is contained feature request).
3. **Test with real data**: Measure Laplacian latency on actual photo library previews. Verify histogram computation cost.
4. **Gather user feedback**: Validate that "sharpness score" and "EXIF side-by-side" are actually the bottlenecks in burst-shot decisions (or if histogram matters more for exposure-focused workflows).
