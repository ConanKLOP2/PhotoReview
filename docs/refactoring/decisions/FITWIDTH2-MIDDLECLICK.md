---
id: FITWIDTH2-MIDDLECLICK
order: 141
summary: |-
  Decided in the PR (user request 2026-10-09): a second, independent Fit width command (`FitWidthAnchor2`, default `BottomThird`, shortcut `FitWidth2` default `D4`, Zoom menu item) and a `MiddleClickAction` setting (default `ActualSize` = 100 %) that reuses the keyboard `ReviewCommand` dispatch; `KeyboardZoomAnchor` still never affects Fit width.
---

# Second Fit width + middle-click action

## Fit width 2

- `FitWidthAnchor` gains `BottomThird = 2` (image point (0.5, 2/3) kept at the viewport centre). `FitWidthAnchor` (UI label "Fit width shows 1", default `Centre`, also used by the initial view) is unchanged for old configs.
- `AppSettings.FitWidthAnchor2` ("Fit width shows 2", default `BottomThird`) drives the new `ReviewCommandType.FitWidth2` (appended to the enum), the Zoom-menu item "Fit width 2" and the middle-click choice "Fit width 2". It uses `SettingsEnumConverter` (RV-D2): an unparsable value resets to `BottomThird` and is listed in `LastLoadRepairs`.
- `PointerInputController.FitWidthAsync(FitWidthAnchor)` takes the anchor; `MainWindowHelpers.FitWidthAnchorFor` picks it. `ApplyFitViewAsync`/T89 is untouched.
- "Keyboard zoom anchors at" does not affect Fit width (Fit width always anchors at the chosen image point placed at the viewport centre); unchanged and now pinned by a test.
- Shortcut `ShortcutMappings.FitWidth2` (optional, default `D4`). `Shift+W` was NOT possible: a shortcut string is a bare key name (`ShortcutKeyName.TryParse` rejects `+`) and `ShortcutRouter.TryResolve` ignores modifiers for zoom commands (RV-D3 only restricts file-changing commands), so Shift+W would also fire plain W. `D4` sits beside `D1`/`D2`/`D3` (100 %, click zoom, custom zoom). An older config whose action already uses `D4` gets `FitWidth2` disabled by `DisableConflictingOptionalShortcuts` (the user's binding wins).

## Middle click

Before this change nothing handled the middle button (no `MouseDown`/`MouseUp`/`ChangedButton` for Middle anywhere in `src/PhotoReview.App`, XAML or code-behind; "Open Folder" is only the menu item, toolbar button and `Ctrl+O`). `MainWindow.ImageScroll_PreviewMouseDown` now handles the middle button (viewport only, not the scroll bars; single press, `ClickCount == 1`) and runs `MiddleClickResolver.Resolve(MiddleClickAction)` through the same `ExecuteReviewCommandAsync` switch the keyboard uses (extracted from `Window_KeyDown`). `ClickZoom`, `ActualSize` (and Fit width/Fit) therefore behave exactly like their shortcut, including `KeyboardZoomAnchor` for 100 % and click-zoom. `MiddleClickAction` uses `SettingsEnumConverter` (default `ActualSize`).
