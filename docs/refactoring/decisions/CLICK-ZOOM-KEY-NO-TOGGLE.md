---
id: CLICK-ZOOM-KEY
order: 141
summary: |-
  The Click-zoom shortcut no longer toggles back to Fit when the image is already at the click zoom level (default: does nothing; use the Fit key); new setting `ClickZoomKeyReturnsToFit` (default off) restores the old toggle. The mouse click-to-zoom still toggles.
---

# CLICK-ZOOM-KEY — Click-zoom shortcut does not toggle back to Fit (2026-10-09)

## User report

"After Fullsize (100 %) / Fit / Fit width, the Click-to-zoom key often does not work the first time; the second press does."

## Verified cause

The user's config: `ClickZoomPercent = 100`, `ZoomActualSize = D1`, `ClickZoom = D2`, `ClickToZoomEnabled = false`
(the mouse click is off, so this is the key). `PointerGestures.DecideClickZoom` was a toggle: at the click zoom level the
key returned to Fit. After the 100 % key the image IS at the 100 % click level, so the first `2` went to Fit and the second
`2` zoomed again -- exactly "works on the second press".

A real-window probe (MainWindow on a hidden desktop, keys raised as `PreviewKeyDown`, gaps of 0/10/30 ms and 1.3 s) showed:
`F` then `2` and `W` then `2` zoom to 100 % on the first press every time; only `1` then `2` went to Fit. A race between
the zoom operations (Fit's convergence loop, Fit width's scrollbar correction, the click zoom) did not reproduce: the
shared `ViewportOperationVersion` supersedes the older operation correctly. So no ordering change was made.

## Decision

- New setting `AppSettings.ClickZoomKeyReturnsToFit` (Settings > Mouse & zoom, default **off**).
- Off: the key always means "go to the click zoom level"; already there, it does nothing (no zoom, no re-anchoring
  scroll). Least surprising for a key named after a zoom level: pressing it never moves AWAY from that level, and Fit has
  its own key (`F`). Re-applying the same zoom was rejected: it would re-run the anchored scroll and could shift the view.
- On: the previous toggle (at the level -> Fit).
- The mouse click-to-zoom keeps toggling: the mouse has no Fit key, and click / click-again is the established gesture.

No migration: an absent value is `false`. Users who relied on `2` to return to Fit turn the setting on.
