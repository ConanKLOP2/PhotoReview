---
id: SETTINGS-SAVE-MULTI-INSTANCE
order: 105
summary: |-
  Decided 2026-10-06 (option A): `SettingsStore.Save` across several windows in `InstanceMode.PerFolder` stays last-writer-wins (each process writes its whole in-memory settings, no reload, no merge); the trade-off is accepted and documented.
---

# SETTINGS-SAVE-MULTI-INSTANCE - settings written by several processes

## Current state

`SettingsStore.Save` writes the whole in-memory settings of the process (atomic temp file + rename). It does not reload or merge,
so with several processes the last writer overwrites another window's change. Saves come from the Settings window and also from
MainWindow toggles (for example `KeepZoomAcrossImages`, the click zoom level). The default mode `SingleWindow` is one process, so
this only affects `InstanceMode.PerFolder`. The write itself is atomic, so the file is never torn; only a change can be lost.

## Options

| | Change | Pros | Cons |
|---|---|---|---|
| A | Accept and document | No code, no risk | A setting changed in one window can be reverted by another window's later save |
| B | Three-way merge on Save | Keeps both windows' changes | Exact only with cross-process locking; the other window keeps stale values until restart; more code and tests |
| C | Watch the file and reload | Windows converge | Live changes may surprise (several settings apply only after restart) |
| D | Warn when the file changed externally | User is told | Extra dialog in a hot path (toggles); still no merge |

## Decision

Option A, chosen by the user on 2026-10-06. No production change. [ADR 0007](../../adr/0007-io-durability-contract.md) section 2
carries a one-line pointer.

## When to revisit

If the user starts using `PerFolder` routinely or loses a setting this way: then B (with locking) first.