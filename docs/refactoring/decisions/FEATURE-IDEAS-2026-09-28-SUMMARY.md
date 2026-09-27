---
id: FEATURE-IDEAS-2026-09-28-SUMMARY
order: 31
summary: |-
  Consolidated index of 6 independent brainstorm passes (compare/decide, safety/trust,
  workflow/speed, large-library/NAS, organization/discovery, power-user/customization)
  proposing new features, for user review -- not yet approved or scheduled.
---

# Feature Ideas 2026-09-28 -- Consolidated Brainstorm Index

Six independent brainstorm passes (one per topic area) each proposed new, non-overlapping
feature ideas for PhotoReview, with adversarial self-critique against existing decisions,
invariants, and the app's speed/simplicity/file-system-first priorities. This document is a
**consolidated index** of all six passes, gathered here for user review and prioritization.
**Nothing below is approved, scheduled, or committed for implementation** -- every idea still
needs a decision on whether/when to build it, and would get its own decision doc (and possibly
an ADR) if and when it's picked up. Full detail for each category -- mechanism, critique,
dropped ideas, and complexity rationale -- lives in its own file under
[`decisions/feature-ideas/`](feature-ideas/).

## How to read this

Each category below lists its "survivor" ideas (the ones that passed self-critique) as a short
table: name, one-line scenario, and rough complexity (S = small, M = medium, L = large). Ideas
that were considered and dropped are **not** repeated here -- see each detail doc's "Dropped
Ideas" section for the rejected ideas and why.

---

## 1. Compare / Decide -- faster decisions when comparing similar photos

Detail: [`feature-ideas/FEATURE-IDEAS-COMPARE-DECIDE.md`](feature-ideas/FEATURE-IDEAS-COMPARE-DECIDE.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Sharpness Quality Indicator | On-demand sharpness score per image in Compare, to pick the sharpest of a burst | M |
| EXIF Side-by-Side Panel | Compact ISO/aperture/shutter/etc. table in Compare, using already-cached EXIF | S |
| Exposure Histogram Comparison | Side-by-side brightness histograms + clipping flags in Compare | M |
| Multi-Photo Burst Comparison | Grid of 3-4 images instead of 2, for culling larger bursts at once | L |

## 2. Safety / Trust -- cheaper recovery from delete/move mistakes

Detail: [`feature-ideas/FEATURE-IDEAS-SAFETY-TRUST.md`](feature-ideas/FEATURE-IDEAS-SAFETY-TRUST.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Session-End Summary Card | Non-blocking toast on folder close: counts/sizes of deleted/moved/copied | S |
| Action History Sidebar with Search | Searchable list of session actions with per-action undo | M |
| Undo-Toast with Quick-Restore Menu | Toast after delete with instant Undo, plus a "last 10 deleted" restore menu | S-M |
| Session Action Journal Export | Export the session's action log as JSON/CSV for audit/record-keeping | S |

## 3. Workflow Speed -- reviewing hundreds/thousands of photos faster

Detail: [`feature-ideas/FEATURE-IDEAS-WORKFLOW-SPEED.md`](feature-ideas/FEATURE-IDEAS-WORKFLOW-SPEED.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Session Undo Log Sidebar | List of this-session file actions with one-click restore for any entry | S |
| Live Folder Metrics / Progress Summary | Remaining/deleted/moved counts and pace estimate during a long cull | S |
| Spatial Lookahead Sidebar (contact sheet) | Thumbnail strip of upcoming photos from the preload window | M |
| Hash-Grouped Duplicate Sidebar | Groups exact/near-duplicate bursts across the whole folder at once | M |

## 4. Large Library / Slow-Link -- huge folders and NAS/slow storage

Detail: [`feature-ideas/FEATURE-IDEAS-LARGE-LIBRARY.md`](feature-ideas/FEATURE-IDEAS-LARGE-LIBRARY.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Slow-Link Visual Indicator | Status-bar icon (green/yellow/red) reusing the existing decode-latency signal | S |
| Graceful Degradation ("Fast Mode") | Lower-res previews + reduced preload when the library exceeds the RAM cache | M |
| Persistent Metadata Cache | Cache folder state/thumbnails across sessions to skip re-scanning unchanged NAS folders | M |

## 5. Organization / Re-Findability -- organizing and finding photos without a database

Detail: [`feature-ideas/FEATURE-IDEAS-ORGANIZATION.md`](feature-ideas/FEATURE-IDEAS-ORGANIZATION.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Sidecar Folder Notes (`.photoreview.md`) | Freeform per-folder note file, readable in any text editor | S |
| Folder Bookmarks / Quick Access | App-specific quick-access list for frequently reviewed folders | S |
| Smart Collections / EXIF-Based Grouping | Session-only grouping of files by capture date/camera/etc. within a folder | M |
| Folder Tagging Convention Helper | UI helper for a `Name [#tag]` folder-naming convention, no hidden metadata | S-M |
| Recently Reviewed Folders Quick Jump | Recent-folders list/dropdown to jump back to a folder across sessions | S |

## 6. Power-User Customization -- repeated workflows for photographers

Detail: [`feature-ideas/FEATURE-IDEAS-POWER-USER.md`](feature-ideas/FEATURE-IDEAS-POWER-USER.md)

| Idea | Scenario | Complexity |
|---|---|---|
| Review Session Presets | Save/restore folder+zoom+profile+UI-state snapshots ("Fashion Cull v2") | M |
| Folder-Based Action Profile Selection | Auto-select an action profile by folder-name glob pattern | M |
| Template Destinations & Smart Prompting | `{date-taken}`-style templates in action destinations | M |
| Multi-Step Actions | One shortcut runs a primary action then a chained secondary action | M |
| Keyboard Chording (multi-key shortcuts) | Two-key sequences (e.g. "Z" then "1") for zoom/action shortcuts | M |
| Session Auto-Load on Folder | Auto-load a matching session preset when opening a matching folder | S |
| Review Workflow Templates | Importable bundles of settings + action profiles + presets | M |

---

## Totals

- **27 survivor ideas** across 6 categories: 4 (Compare/Decide) + 4 (Safety/Trust) + 4 (Workflow
  Speed) + 3 (Large Library) + 5 (Organization) + 7 (Power-User).
- Complexity mix: roughly a third S, just over half M, one L (Multi-Photo Burst Comparison).

**Next step:** the user picks which ideas (if any) are worth a real decision doc and
prioritization; none of this is scheduled work.
