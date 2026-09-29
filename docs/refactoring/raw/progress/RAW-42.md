# RAW-42 Pair UI — progress
Branch: feat/raw-support-integration · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] Add optional, unassigned-by-default shortcut for switching the displayed pair member; include settings UI, validation, normalization, and routing.
- [x] Present either capture-group member without splitting the catalog entry; add the `JPG+RAW` overlay badge.
- [x] Keep normal navigation single-image; explicit Compare opens both capture-group members and preserves selection.

## Next action
Run the full bounded Release gate and localization/docs checks on this branch. Fix any regressions, rerun focused RAW-42 tests, and update this file to READY FOR REVIEW before the next push.

## Evidence / measurements
- Focused App Release tests after the latest changes: 3/3 passed (toggle shortcut routing, member presentation/compare, and MainViewModel member toggle).
- Related Core Settings Release tests: 113/113 passed, including optional shortcut normalization and conflict validation.
- Mutation check: reversing the JPEG/RAW toggle mapping made `ToggleCaptureGroupMemberAsync_SwitchesDisplayedPathWithoutSplittingCatalogEntry` fail; restored source and reran the focused App tests successfully.

## Open problems
- Full Release verification and localization/docs checks for this change are pending.
