# I18N — Multi-language UI (English + Vietnamese first, community-editable)

**Status:** approved 2026-09-24 (all recommendations, Q-L1..Q-L8 = a) · **ADR:** [0006](../adr/0006-localization-json-catalogs.md)

## Goal

Every user-visible string comes from a plain JSON translation file that users and the community can edit
without rebuilding. English is built in and always complete; Vietnamese ships next to the exe; anyone can
add or override a language by dropping a file into a folder.

## Baseline (2026-09-24)

- No localization infrastructure; UI text is hard-coded, mixed Vietnamese/English.
- ~108 text attributes in 7 XAML files; ~190 Vietnamese literal lines in C#
  (App: `StatusFormatter` 26, `MainViewModel`, coordinators, window code-behind;
  Core: `UndoService`, `FileActionService`, `RecoveryRetryService`, `DragDropInputService`,
  `SettingsValidator`, `OperationJournal`, `ReviewAction` defaults; dev tools ~70).
- Traps: `MainViewModel` branches on `FolderText.Contains("· Explorer")`; `OperationJournal` persists
  Vietnamese error text; Core throws `IOException` with Vietnamese text that the UI shows;
  ~20 test files assert Vietnamese strings (`SourcePresenceTests` reads literal `AutomationProperties.Name`).
- Out of scope: `ex.Message` produced by Windows (follows the OS language).

## Design (details and rationale in ADR 0006)

| Piece | Where | What |
|---|---|---|
| Catalog files | `src/PhotoReview.Core/Localization/Languages/*.json` | Flat JSON, `_meta` block, named placeholders `{count}`, plurals `key.one`/`key.other`. `en.json` embedded (fallback) and copied; `vi.json` copied to `<app>\Languages\`. |
| User files | `%LocalAppData%\PhotoReview\Languages\*.json` | Override single keys of a shipped language, or add a new one. |
| Lookup order | — | user file → shipped file → embedded English → the key itself. |
| `Localizer` | Core `Localization/` | Loads/merges/validates catalogs, pre-parses templates, `SafeFormatter` never throws. Ambient `Localizer.Current` (swapped atomically, like `CultureInfo.CurrentUICulture`). |
| Generated `Tr` API | `src/PhotoReview.Localization.Generator` (Roslyn source generator over `en.json`) | `Tr.StatusBatchDone(succeeded, failures)` — one method per key, parameters from placeholders; a typo is a build error. `TrKeys.*` constants for XAML/tests. |
| XAML | App `Localization/TrExtension` | `{loc:Tr settings.title}` binds to `LocalizationSource.Instance[key]` → live language switch and "Reload translations" without restart. |
| Setting | `AppSettings.UiLanguage` (`auto`/code) | Migration: existing config → `vi`; new install → `auto` (Windows UI language if a catalog exists, else `en`). |
| Not localized | — | Logs, CSV, journal, perf reports, key names (AGENTS.md rule 4). |

**Perf budget:** catalog load at startup < 5 ms (measured in L11 against the 1.8 s "start → first image");
per-navigation cost = one dictionary lookup + one pre-parsed template concat. XAML bindings are created once per window.

## Tasks

| ID | Task | Tests / evidence | Status |
|---|---|---|---|
| L00 | This plan, ADR 0006, decisions Q-L1..Q-L8 | docs only | 🔄 PR open |
| L01 | Core: `Localizer`, catalog loader (3 layers), `SafeFormatter` (named args, plurals), validation (size cap, unknown placeholders, bad braces → key rejected + logged), `en.json`/`vi.json` skeleton; source generator | Unit tests: broken JSON, partial catalog, placeholder mismatch, fallback order, plural rules | ⏳ |
| L02 | App wiring: DI + startup apply, `TrExtension`, `LocalizationSource`, `UiLanguage` + migration, `IAppPaths.UserLanguagesDir`; test assemblies pin `vi` via `ModuleInitializer` | Existing Vietnamese assertions still pass unchanged | ⏳ |
| L03 | Guard tests: no Vietnamese literals in `src/**/*.cs` outside catalogs (shrinking allowlist); no literal text attributes in XAML (symbol whitelist); every XAML `Tr` key exists in `en.json`; `vi.json` has no unknown keys | Architecture tests | ⏳ |
| L04 | Hot path: `StatusFormatter`, `MainViewModel`, coordinators; replace `Contains("· Explorer")` with state flag | Old tests pass in `vi`; new `en` tests | ⏳ |
| L05 | All XAML windows; `SourcePresenceTests` checks keys instead of literals | XAML tests | ⏳ |
| L06 | Core messages via `Tr`; journal stores stable error code + English text, Recovery window localizes by code (old entries: stored text) | Core + journal round-trip tests | ⏳ |
| L07 | Dialogs, folder picker title, enum combo items, BenchmarkWindow UI; Benchmarking/PerfAnalysis reports → fixed English | | ⏳ |
| L08 | Settings: language picker (bilingual label, native names), live switch, "Open languages folder", "Reload translations", "Export strings to translate" | ViewModel + settings round-trip | ⏳ |
| L09 | Translator mode: `--i18n-keys` (show keys) and pseudo-locale `qps-ploc` (+35 % length, accents) | Unit test for pseudo transform | ⏳ |
| L10 | `tools/i18n-check.ps1` in CI (JSON valid, no unknown keys, placeholder parity, completeness %), `docs/TRANSLATING.md` (EN+VI), PR template, README table; `verify-release.ps1` checks `Languages\` | CI green | ⏳ |
| L11 | Real-machine check: GUI in `en`, `vi`, pseudo (screenshots, overflow), startup perf vs 1.8 s | Evidence in this file | ⏳ |
| L12 | Vietnamese copy polish (folder/thư mục, Action/thao tác…) — separate PR, user reviews wording | | ⏳ (Q-L6 = later) |

Order: L00 → L01 → L02 → L03 → L04–L07 → L08–L10 → L11 → L12.
PR policy: every PR based on `master` (no stacks); code tasks L01–L11 may share one branch with one commit per task.

## Decisions (2026-09-24, user: "Thực hiện theo đề xuất")

| ID | Question | Decision |
|---|---|---|
| Q-L1 | Default language | Existing users `vi`; new install follows Windows, fallback `en` |
| Q-L2 | (v1 question, superseded by Q-L8) | — |
| Q-L3 | Core messages / journal | Core uses `Tr`; journal stores error code + English text, UI localizes by code |
| Q-L4 | Dev tools | Localize BenchmarkWindow UI; reports in fixed English |
| Q-L5 | Default actions "Loại 2/3/4", folders `Loai-*` | Keep as-is (user data) |
| Q-L6 | Vietnamese copy polish | Later, separate PR (L12) |
| Q-L7 | Community channel | GitHub PRs to `Languages/*.json` (repo is public); Weblate/Crowdin optional later — format compatible |
| Q-L8 | Language switch | Live switch + "Reload translations", no restart |

## Contributor rules (enforced by L03/L10)

- Changing the *meaning* of an English string → new key name (e.g. `...v2`); stale translations never silently mismatch.
- Translations may drop a placeholder (warning) but never introduce an unknown one (key rejected).
- Translator context lives in `en.notes.json` (same keys), not in the catalog.
