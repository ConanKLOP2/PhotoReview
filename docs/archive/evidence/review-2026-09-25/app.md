# PhotoReview App/Tools Code Review — 2026-09-25

Scope: `src/PhotoReview.App`, `src/PhotoReview.Benchmarking`, `src/PhotoReview.PerfAnalysis`, `tools/`, `.github/workflows/ci.yml`. Read-only static review (no build/test run). Branch `master`.

## Summary

The App layer is unusually clean for the checklist this review was built from: ADR 0005 (UI-thread affinity) is fully respected (zero `ConfigureAwait(false)`, no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` in `src/PhotoReview.App`), ADR 0006 (JSON i18n) is consistently used in XAML (`{loc:Tr ...}` everywhere, no hardcoded `Content=`/`Text=` English strings found), the T89 `ApplyFitViewAsync` two/three-pass convergence loop (`MainWindow.xaml.cs:381-413`) is implemented correctly, and `FileActionGate`/global exception handlers (`App.xaml.cs:241-243`) make the `async void` event handlers safe in practice. `MainViewModel` (665 lines) and `ImagePresenter` (504 lines) are large but each delegates to focused collaborators (`FileActionController`, `SiblingFolderNavigator`, `DuplicateCleanupController`, `ZoomDetailLoader`) rather than hoarding logic, so they read more like façades than god-classes.

Findings below are concentrated in accessibility, dark-theme resource hygiene, and two benchmarking/tooling correctness gaps — no Critical issues were found.

Counts: **Critical: 0, High: 0, Medium: 4, Low: 3**

---

## High
(none found)

## Medium

### APP-01 — `ActionProfilesWindow.xaml` has zero AutomationProperties on any control
- **Severity:** Medium
- **File:** `src/PhotoReview.App/ActionProfilesWindow.xaml:18-34`
- **Problem:** The window has 11 interactive controls (4 `Button`s in the list toolbar, `NameText`/`ShortcutText`/`DestinationText` `TextBox`es, `OperationCombo`, `ConfirmCheck`, plus Cancel/Apply buttons) and none carries `AutomationProperties.Name`. Screen-reader users hear only "button"/"edit" with no label.
- **Why it matters:** Violates the accessibility bar the rest of the app partially meets (`MainWindow.xaml` has 14 `AutomationProperties.Name` entries); this window is completely unlabeled for assistive tech.
- **Fix steps:**
  - Add `AutomationProperties.Name="{loc:Tr actionProfiles.add}"` (etc.) to each `Button`, matching its visible `Content` binding so labels localize too.
  - Add names to `NameText`, `ShortcutText`, `DestinationText`, `OperationCombo`, `ConfirmCheck` describing the field they edit.
- **Test:** A UI Automation smoke test (e.g. via `AutomationElement.FromHandle` + `FindAll(Control)`) asserting every `Button`/`TextBox`/`ComboBox`/`CheckBox` in this window has a non-empty `Name` property.
- **Effort:** S
- **Risk of fix:** S (attribute-only change, no logic touched)

### APP-02 — `SettingsWindow.xaml` accessibility coverage gap
- **Severity:** Medium
- **File:** `src/PhotoReview.App/SettingsWindow.xaml` (33 interactive controls: 8 `Button`, 3 `CheckBox`, 6 `ComboBox`, 2 `RadioButton`, 14 `TextBox`; only 5 have `AutomationProperties.Name`)
- **Problem:** The largest settings surface in the app is ~85% unlabeled for automation/screen readers.
- **Why it matters:** Same accessibility gap as APP-01 but on the window a user is most likely to need keyboard/AT navigation for (many stacked TextBoxes with terse adjacent `TextBlock` labels only).
- **Fix steps:**
  - Audit each `TextBox`/`ComboBox`/`CheckBox`/`RadioButton` next to a `TextBlock` label and add `AutomationProperties.Name="{loc:Tr <same key>}"` or `AutomationProperties.LabeledBy` pointing at the adjacent `TextBlock`.
- **Test:** Same UI Automation enumeration test as APP-01, parametrized per window.
- **Effort:** M
- **Risk of fix:** S

### APP-03 — Dark-theme colors hardcoded as hex literals instead of `Dark.*` brush resources
- **Severity:** Medium
- **File:** `src/PhotoReview.App/ActionProfilesWindow.xaml:1,16,23,26,32` (`Background="#171717"`, `Foreground="#F5F5F5"`, `Foreground="#FFFFFF"`, `Background="#252525"`, `BorderBrush="#505050"`); same pattern in `MainWindow.xaml`, `SettingsWindow.xaml`, `RecoveryWindow.xaml`, `RecoveryPathPanel.xaml`, `BenchmarkWindow.xaml`, `DiagnosticsWindow.xaml`, `BatchReviewWindow.xaml` (all matched by `grep -l 'Background="#\|Foreground="#'`)
- **Problem:** `src/PhotoReview.App/Themes/DarkControls.xaml:10-21` already defines the exact same colors as named brushes (`Dark.TextStrong`=`#FFFFFF`, `Dark.Input`=`#252525`, `Dark.Border`=`#3C3C3C`, `Dark.BorderStrong`=`#5A5A5A`, etc.), but most windows re-declare the raw hex instead of `{DynamicResource Dark.*}` (`#505050` on `ActionProfilesWindow.xaml:23` doesn't even match any defined brush — it's a fifth ad-hoc gray).
- **Why it matters:** Any future theme change (e.g. a light theme, or just retuning the dark palette) requires hunting through 8 XAML files for literal hex instead of one resource dictionary; `#505050` being an undocumented one-off also means it silently drifts from the palette.
- **Fix steps:**
  - Replace window-level `Background`/`Foreground` and inline control colors with `{DynamicResource Dark.Panel}` / `Dark.TextStrong` / `Dark.Input` / `Dark.BorderStrong` as appropriate.
  - Either fold `#505050` into `Dark.BorderStrong` (`#5A5A5A`) or add it as a named resource if it's intentionally distinct.
- **Test:** A simple XAML lint/grep-based test (or a build-time analyzer rule) failing when `Background="#`/`Foreground="#`/`BorderBrush="#` literals appear outside `Themes/DarkControls.xaml`.
- **Effort:** M
- **Risk of fix:** S (visual regression risk only if a literal doesn't map 1:1 to an existing brush — verify each substitution visually)

### TOOL-01 — `verify-release.ps1` has no `$ErrorActionPreference = 'Stop'`
- **Severity:** Medium
- **File:** `tools/verify-release.ps1:1-9` (missing; contrast with every other script in `tools/`, all of which set it — e.g. `tools/verify-all.ps1:25`, `tools/smoke-test.ps1:1`, `tools/i18n-check.ps1:17`)
- **Problem:** Non-terminating cmdlet failures (e.g. `Get-Content -LiteralPath $hashFile` when `native\turbojpeg.sha256` is missing, or `Get-Item` on a missing `PhotoReview.App.exe`) print a red error to the host but the script keeps running with `$null`/stale values, producing confusing downstream errors (or, worse, a false "PASS" if a later check happens to short-circuit past the corrupted variable) instead of a clean, immediate failure.
- **Why it matters:** This script gates CI release publishing (`.github/workflows/ci.yml` "Verify Release" step) and is also the last line of defense verified by `verify-all.ps1`; a swallowed non-terminating error here undermines the whole point of the gate.
- **Fix steps:**
  - Add `$ErrorActionPreference = 'Stop'` at the top, matching the other scripts.
  - Wrap the `dotnet msbuild` invocation and `ConvertFrom-Json` call in a `try/catch` since `$ErrorActionPreference` doesn't affect exit codes from external processes.
- **Test:** A local test that deletes/renames `native/turbojpeg.sha256` (or points `-ReleaseDirectory` at an empty folder) and asserts the script exits non-zero immediately rather than continuing past the missing file.
- **Effort:** S
- **Risk of fix:** S

---

## Low

### TOOL-02 — `PerformanceTestHarness.MeasureColdAsync` includes first-call JIT/codec warm-up in its timed samples
- **Severity:** Low
- **File:** `src/PhotoReview.Benchmarking/PerformanceTestHarness.cs:59-71`
- **Problem:** `MeasureColdAsync` starts timing (`Stopwatch.StartNew()`) on the very first file with no prior warm-up call to `Decode(...)`. The first sample pays for WPF `BitmapImage`/codec JIT and any one-time native init, then that inflated sample is fed directly into `Percentile` (P50/P95/Max) alongside the rest. Contrast with `BenchmarkEngine.RunAsync` (`src/PhotoReview.Benchmarking/BenchmarkEngine.cs:33-50`), which explicitly runs `profile.WarmupCount` untimed iterations before collecting samples.
- **Why it matters:** For small `take` counts (default 30) a single outlier first sample measurably skews P95/Max, which is exactly the metric `Status = p95 <= p50*8 ? PASS : WARN` (`PerformanceTestHarness.cs:89`) uses to decide pass/warn — a cold-JIT outlier can flip a healthy run to WARN.
- **Fix steps:**
  - Call `Decode(...)` once on a throwaway file (or the first fixture file) before the timed loop starts, discarding that result, mirroring `BenchmarkEngine`'s `WarmupCount` pattern.
- **Test:** Unit test asserting `MeasureColdAsync`'s first recorded sample is not disproportionately larger than the median when called in a fresh AppDomain/process (or simply assert a warm-up decode call happens before the loop, via a call-count spy).
- **Effort:** S
- **Risk of fix:** S

### APP-04 — Fire-and-forget `ApplyFitViewAsync` calls give the user no feedback on failure
- **Severity:** Low
- **File:** `src/PhotoReview.App/MainWindow.xaml.cs:113,259,364` (`_ = ApplyFitViewAsync();`)
- **Problem:** Double-click-to-fit, `ResetFitView()`, and the `ToggleFit` keyboard command all discard the returned `Task`. Any exception surfaces only via the global `DispatcherUnhandledException` handler (`App.xaml.cs:241`), which logs it and sets `Handled = true` — the Fit operation silently no-ops with zero user-visible indication that anything went wrong.
- **Why it matters:** Not a crash risk (global handlers cover it), but a debuggability/UX gap: a user pressing Fit and seeing nothing happen has no way to know it failed versus e.g. already being fit.
- **Fix steps:**
  - Either await these calls from `async void` wrappers already used elsewhere (`FitImage_Click` already does `await ApplyFitViewAsync()`), or add a `.ContinueWith`/try-catch around the fire-and-forget call sites that surfaces a status-bar message via `StatusFormatter` on failure.
- **Test:** Inject a fault into `ImageScroll.UpdateLayout()`/`UpdateFitSize()` path (or a viewer state that throws) and assert the status text reflects the failure instead of staying silent.
- **Effort:** S
- **Risk of fix:** S

### TOOL-03 — CI's Integration-category exclusion isn't mentioned in `AGENTS.md`'s documented local filter
- **Severity:** Low
- **File:** `.github/workflows/ci.yml:74-78` (filter `Category!=Manual&Category!=Stress&Category!=Native&Category!=Slow&Category!=Integration`) vs. `AGENTS.md` "Tests" section (documents `Category!=Manual&Category!=Native&Category!=Slow&Category!=Stress`, no `Category!=Integration`)
- **Problem:** `tools/verify-all.ps1:33-39` actually matches CI exactly (it does add `&Category!=Integration` by default), so the *scripts* are consistent with each other — but the prose in `AGENTS.md` that a developer reads before running tests ad hoc (e.g. `dotnet test --filter "..."` copy-pasted from the doc) omits the `Integration` exclusion, so a manually-typed filter would run more tests than both CI and `verify-all.ps1` actually run.
- **Why it matters:** Minor inconsistency between documented guidance and actual gate behavior; a dev relying on the doc's filter string verbatim gets a different (slower, possibly environment-sensitive) test set than CI.
- **Fix steps:**
  - Update `AGENTS.md`'s "Tests" bullet to include `Category!=Integration`, or explicitly say "see `tools/verify-all.ps1` for the authoritative filter."
- **Test:** N/A (doc-only fix); could add a CI doc-lint step diffing the filter string in `AGENTS.md` against `ci.yml`/`verify-all.ps1` if this class of drift recurs.
- **Effort:** S
- **Risk of fix:** S

---

## Notes on categories with no findings

- **UI-thread violations / ADR 0005:** clean — no `ConfigureAwait(false)`, `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` in `src/PhotoReview.App`.
- **T89 two-pass rule:** `ApplyFitViewAsync` (`MainWindow.xaml.cs:381-413`) correctly loops up to 3 passes with viewport-convergence checks; all 4 call sites (`ResetFitView`, double-click, `ToggleFit`, `FitImage_Click`) route through this one method, so the two-pass contract can't be bypassed by a caller doing a single pass.
- **Event-handler leaks:** `Localizer.CurrentChanged` is paired correctly in `MainWindow` (`+=`/`-=` at `MainWindow.xaml.cs:77,199`) and `BenchmarkWindow` (`:34,68`); `RecoveryWindow`'s per-row `PropertyChanged` subscriptions (`RecoveryWindow.xaml.cs:61`) are between objects with identical lifetime (rows are owned by the window), so not a leak.
- **Hardcoded i18n strings:** none found in XAML `Content=`/`Text=`; all use `{loc:Tr ...}`.
- **Large classes:** `MainViewModel` (665 lines) and `ImagePresenter` (504 lines) are sizable but delegate to `FileActionController`/`SiblingFolderNavigator`/`DuplicateCleanupController`/`ZoomDetailLoader`; no single-responsibility violation found worth flagging separately from APP-01/02/03 above.
- **PS 5.1 compatibility:** no `??`/`?.`/ternary operators found in any `tools/*.ps1` script; all except `verify-release.ps1` (TOOL-01) set `$ErrorActionPreference = 'Stop'`.
