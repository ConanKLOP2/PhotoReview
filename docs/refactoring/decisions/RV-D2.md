---
id: RV-D2
order: 102
summary: |-
  An unknown or garbage enum value in config.json (LoadingMode, ImageSortMode, ScalingQuality, DecoderBackend, KeyboardZoomAnchor, KineticGlideSmoothing) now loads as the AppSettings default and is listed in LastLoadRepairs (startup dialog), instead of silently becoming the enum's zero member (option A, 2026-10-01).
---

# RV-D2 — Unparsable enum in config.json (RV-S01)

**Decided 2026-10-01: option A** (A reset to the `AppSettings` default and report it in `LastLoadRepairs`; B same reset, silent; C keep the zero member and document it).

## Problem

`LenientEnumConverter<T>` returned `default(T)` (the zero member) for unknown text, `""`, `null`, undefined numbers and
objects. For six `AppSettings` properties the zero member is not the default (`LoadingMode` Fast vs Preview, `ImageSortMode`
Name vs Default, `ScalingQuality` Linear vs HighQuality, `DecoderBackend` Wpf vs WicDirect, `KeyboardZoomAnchor` Pointer vs
ViewportCentre, `KineticGlideSmoothing` Off vs Predict). The `Enum.IsDefined` branches in `SettingsNormalizer` never fired
because 0 is defined, so a typo silently changed behaviour and nothing was reported.

## Implementation

- `Model/SettingsEnumConverter<T>` wraps `LenientEnumConverter<T>` with an undefined sentinel (`int.MinValue`) as its fallback.
- It is attached with `[JsonConverter]` on the six `AppSettings` properties only (property level beats the type-level attribute).
  Journal lines and metrics dictionaries keep the plain lenient converter (zero member), unchanged.
- The existing `SettingsNormalizer` `Enum.IsDefined` branches then reset the value to the `AppSettings` default and add the property
  name to `LastLoadRepairs`; the startup dialog (`SettingsLoadRepairText`, key `settings.load.repaired`) already lists those names,
  so no new text keys. The repaired value is persisted by the existing write-back as its PascalCase name.
- Side effect: anything deserializing `AppSettings` without normalizing sees the sentinel (settings import and `AppSettings.Clone`
  already normalize or round-trip through the same converter).
- Tests: `SettingsStoreFailureTests` (6 properties x 5 literals + write-back), `MouseSettingsTests.Load_GlideSmoothingText_IsReadLeniently`
  updated (unknown text now gives `Predict`, not `Off`).
