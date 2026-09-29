# RAW-42 Pair UI — progress
Branch: feat/raw-support-integration · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: READY FOR REVIEW

## Steps
- [x] Add optional, unassigned-by-default shortcut for switching the displayed pair member; include settings UI, validation, normalization, and routing.
- [x] Present either capture-group member without splitting the catalog entry; add the `JPG+RAW` overlay badge.
- [x] Keep normal navigation single-image; explicit Compare opens both capture-group members and preserves selection.

## Next action
No further RAW-42 work remains. The next listed RAW wave is RAW-60 benchmark; PR #239 remains a draft umbrella for unfinished RAW waves. The user waived RAW-41's Recovery-window visual review.

## Evidence / measurements
- Focused App Release tests after the latest changes: 3/3 passed (toggle shortcut routing, member presentation/compare, and MainViewModel member toggle).
- Related Core Settings Release tests: 113/113 passed, including optional shortcut normalization and conflict validation.
- Mutation check: reversing the JPEG/RAW toggle mapping made `ToggleCaptureGroupMemberAsync_SwitchesDisplayedPathWithoutSplittingCatalogEntry` fail; restored source and reran the focused App tests successfully.
- First full-gate attempt caught the expected SettingsWindow shortcut-field-map tripwire; added `ToggleCaptureMember` to the read-from-UI set, and the focused guard passed 1/1.
- `tools/verify-all.ps1 -Hidden` passed: Release build 0 warnings/0 errors; Architecture 64, Core 1780, Imaging 642, Integration 635, App 1199; publish and release verification passed.
- `tools/i18n-check.ps1`, `tools/docs-budget.ps1 -Check`, and `tools/check-doc-links.ps1` passed. The pre-existing unused Vietnamese key warning remains.

## Open problems
- None for RAW-42. The full RAW umbrella remains open for later RAW waves, including RAW-60/61/62/70.
