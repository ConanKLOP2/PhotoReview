# TITLE-BAR-INSTANCE-LABEL

The user runs the real built app themselves every day; agents and manual verification sometimes launch the
same `PhotoReview.App.exe` directly (`Start-Process`, computer-use GUI checks), and `Category=UI`
integration tests spin up real `MainWindow` instances that can occasionally flash visibly on screen (e.g.
during a WPF `Application`-singleton race). Both cases needed a foolproof, always-visible way to tell such a
window apart from the user's own everyday window.

## What changed

- `MainViewModel.InstanceLabel` (nullable, default unset): when set, every title this window renders
  (`Tr.AppTitle` and the folder-loaded title alike) gets a `"[label] "` prefix, applied in exactly one place
  (`MainViewModel.ApplyInstanceLabel`). Unset is byte-for-byte identical to today's title.
- **Tests (`tests/PhotoReview.Integration.Tests/Infrastructure/TestAppHost.CreateMainWindow`):** sets a fixed
  `TestAppHost.TestInstanceLabel = "TEST"` unconditionally, right after resolving `MainWindow` from the DI
  container -- no environment variable, so it can never be forgotten. This is the only place in `tests/` that
  creates a real `MainWindow` (enforced by `AppCompositionTests`'s AR02d check for `new MainWindow(`
  outside DI-based composition), so every `Category=UI` test's window is covered.
- **Manual/agent real-exe launches:** new diagnostic env var `PHOTOREVIEW_DIAG_INSTANCE_LABEL` (same
  `PHOTOREVIEW_DIAG_*` family as `PHOTOREVIEW_DIAG_FORCE_LOG` etc., `DiagOptions.InstanceLabel`), read once
  at production startup (`App.xaml.cs`, right after `MainWindow` is resolved). Set it before launching the
  built exe directly, e.g. `PHOTOREVIEW_DIAG_INSTANCE_LABEL="AGENT CHECK"` -> title shows
  `[AGENT CHECK] Photo Review - ...`. Unset (the normal everyday launch) has zero effect, same contract as
  every other `DiagOptions` flag.
- Secondary windows (`SettingsWindow`, `RecoveryWindow`, `BenchmarkWindow`, `SkippedFilesWindow`, etc.) are
  all opened with `Owner = Application.Current?.MainWindow` (`WpfDialogService`) -- their taskbar/Alt-Tab
  entry already carries the owner's (labelled) title, so none of them needed their own label.
- The label text (`[TEST]`, `[AGENT CHECK]`, ...) is literal ASCII debug/diagnostic text, not user-facing UI
  copy, so it is not localized; it is concatenated onto the already-localized `Tr.AppTitle` /
  `Tr.MainTitleWithFolder` output in code, so translations are unaffected.

## Tests

- `MainViewModelNavigationTests.InstanceLabel_PrefixesTitle_UnsetLeavesTitleUnchanged`: unit-level, both the
  empty-folder and folder-loaded title, plus the unset/cleared case matches today's title exactly.
- `MainWindowWiringTests.CreateMainWindow_AlwaysTagsTitleWithTestLabel` (`Category=UI`): the real
  `TestAppHost.CreateMainWindow` window's `Title` starts with `[TEST] ` before and after a folder loads.
- `DiagOptionsTests.InstanceLabelParsesNonBlankValues` / `DescribeAndAnyEnabledCombineAllFlags`: env-var
  parsing (blank/unset -> null, zero effect on `AnyEnabled`), following the existing `PHOTOREVIEW_DIAG_*`
  test pattern (`DiagOptions.ResetForTests()`, no process-wide `Environment.SetEnvironmentVariable` races).
- Mutation-checked: disabling `ApplyInstanceLabel`'s prefix logic makes both the unit and the UI test fail.
