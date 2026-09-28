---
id: Q-R41..Q-R52
order: 33
summary: |-
  v2.0.203 user feedback triage: configurable zoom step, Open Folder/Custom Zoom shortcuts, confirm-before-delete, zoom-% HUD, empty-folder notice and settings export/import approved (default off/unconfigured where applicable); Refresh, in-viewer rename, trackpad gesture tuning and new format/RAW support declined.
---

# Q-R41..Q-R52 — v2.0.203 user feedback triage (2026-09-28)

Source: user feedback on release 203 (Vietnamese, pasted in chat). Q-R39 (Fit Width anchor bug)
was already fixed in another session before this triage.

| ID | Item | Outcome | Default |
|---|---|---|---|
| Q-R41 | Keyboard zoom step too coarse (25% jump) | DO — make `ZoomStep` a setting instead of a hardcoded `0.25` constant | user-configurable, no default value mandated by this decision |
| Q-R42 | No keyboard shortcut for Open Folder | DO — add to `ShortcutMappings` | — |
| Q-R43 | No keyboard shortcut for Custom Zoom dialog | DO — add to `ShortcutMappings` | — |
| Q-R44 | No general "confirm before delete" | DO — new setting | default OFF (unchecked) |
| Q-R45 | No on-screen current-zoom indicator | DO — HUD text, reuse info-overlay infra | default OFF (unchecked, not shown) |
| Q-R46 | F5/Refresh re-render | DECLINED — not doing | — |
| Q-R47 | Open Folder gives no signal when the chosen folder has no images directly (only subfolders) | DO — surface a notice | — |
| Q-R48 | External editor integration (right-click → open in declared app) | DO | new setting, default OFF / not configured (menu entry hidden until an editor is declared) |
| Q-R49 | Rename while viewing | DECLINED — not doing (index/cache-corruption risk, needs its own design pass first) | — |
| Q-R50 | Export/Import settings to/from a file | DO | Export defaults to the app's own current directory (`AppContext.BaseDirectory`) as one path option; if the user does not accept that default, a Save/Open dialog lets them pick any location |
| Q-R51 | Trackpad two-finger gesture speed/kinetic + pinch-vs-pan distinction | DECLINED — user expects the OS/trackpad driver to already handle this correctly; no code change | — |
| Q-R52 | New format support (WebP/HEIC/JXL/PSD/AVIF) and RAW (CR2/CR3/ARW/...) | DECLINED for now — needs a separate architecture decision (library choice, licensing, per-format test plan) before any implementation | — |

Implementation of the DO items tracked on branch `claude/outstanding-issues-959307`.
