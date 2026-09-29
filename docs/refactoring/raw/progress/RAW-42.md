# RAW-42 Pair UI — progress
Branch: feat/raw-support-integration · PR: #239 draft umbrella · Agent model: Codex
Last update: 2026-09-29 · State: IN PROGRESS

## Steps
- [x] Add optional, unassigned-by-default shortcut for switching the displayed pair member; include settings UI, validation, normalization, and routing.
- [x] Present either capture-group member without splitting the catalog entry; add the `JPG+RAW` overlay badge.
- [x] Keep normal navigation single-image; explicit Compare opens both capture-group members and preserves selection.

## Next action
Run the full bounded Release gate after the field-map tripwire update. If it passes, update this file to READY FOR REVIEW and report that the Recovery-window visual review was waived by the user.

## Evidence / measurements
- Focused App Release tests after the latest changes: 3/3 passed (toggle shortcut routing, member presentation/compare, and MainViewModel member toggle).
- Related Core Settings Release tests: 113/113 passed, including optional shortcut normalization and conflict validation.
- Mutation check: reversing the JPEG/RAW toggle mapping made `ToggleCaptureGroupMemberAsync_SwitchesDisplayedPathWithoutSplittingCatalogEntry` fail; restored source and reran the focused App tests successfully.
- First full-gate attempt caught the expected SettingsWindow shortcut-field-map tripwire; added `ToggleCaptureMember` to the read-from-UI set, and the focused guard passed 1/1.

## Open problems
- Full Release verification is pending after the tripwire fix. Localization, docs budget, and links already pass; the existing unused Vietnamese key warning remains.
