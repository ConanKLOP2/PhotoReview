# RAW-40 JPG+RAW pair detection — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Detect unambiguous same-folder, same-basename JPEG+RAW groups and associate one matching XMP sidecar — `ac52f73` — `CaptureGroupBuilderTests` 3/3.
- [x] 2. Apply `RawPairMode` in `ReviewCatalog`; keep both image members addressable and preserve representative metadata — `PENDING` — targeted tests 6/6.
- [ ] 3. Preserve Explorer order and INV-7 behavior; verify 10k-file grouping remains below 20 ms.

## Next action
Measure grouping on 10k paths, then wire folder loading after group-safe file actions are in place.

## Evidence / measurements
- Group matching is ordinal case-insensitive on folder and basename; ambiguous duplicate JPEG/RAW names remain separate.

## Open problems
- Group file actions and recovery are not implemented; those belong to RAW-41.
