# RAW-40 JPG+RAW pair detection — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Detect unambiguous same-folder, same-basename JPEG+RAW groups and associate one matching XMP sidecar — `ac52f73` — `CaptureGroupBuilderTests` 3/3.
- [ ] 2. Apply `RawPairMode` in catalog; keep both members addressable while catalog count counts groups.
- [ ] 3. Preserve Explorer order and INV-7 behavior; verify 10k-file grouping remains below 20 ms.

## Next action
Connect the builder to `ReviewCatalog`, test hidden-member lookup and Explorer-order collapse, then wire folder loading after group-safe file actions are in place.

## Evidence / measurements
- Group matching is ordinal case-insensitive on folder and basename; ambiguous duplicate JPEG/RAW names remain separate.

## Open problems
- Group file actions and recovery are not implemented; those belong to RAW-41.
