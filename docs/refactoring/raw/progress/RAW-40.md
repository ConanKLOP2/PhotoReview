# RAW-40 JPG+RAW pair detection — progress
Branch: feat/raw22-cache-preload-ram · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] 1. Detect unambiguous same-folder, same-basename JPEG+RAW groups and associate one matching XMP sidecar — `ac52f73` — `CaptureGroupBuilderTests` 3/3.
- [x] 2. Apply `RawPairMode` in `ReviewCatalog`; keep both image members addressable and preserve representative metadata — `b2f2b5e` — targeted tests 6/6.
- [ ] 3. Finish folder/Explorer integration and verify INV-7 behavior; 10k-path grouping measured below 20 ms median.

## Next action
Complete RAW-41's journaled group actions before enabling `RawPairMode` from folder settings; then verify late Explorer ordering preserves group membership and selection.

## Evidence / measurements
- Group matching is ordinal case-insensitive on folder and basename; ambiguous duplicate JPEG/RAW names remain separate.
- 10k paths (5k pairs), five samples: median 8.01 ms, range 5.73–37.12 ms; median meets the 20 ms target.
- Mutation check disabled pair collapsing; `GroupEntries_UsesSelectedRepresentativeAndKeepsBothMembersAddressable` failed on catalog count (expected 2, actual 3); production source restored.

## Open problems
- Group file actions and recovery are not implemented; those belong to RAW-41.
