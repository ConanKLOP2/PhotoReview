---
id: UX-FIXES-ROUND1
order: 32
summary: |-
  Four UX findings addressed: Delete/Recycle added to context menu, gated behind an off-by-default setting
  (high); the dead, hardcoded-hidden shortcut hint line removed outright instead of un-hiding it (high);
  Recycle-Bin terminology standardized for Delete/batch actions (medium); the hint-wording fix (medium) is
  moot now that the hint itself is gone.
---

# UX-FIXES-ROUND1 — Four UX findings from cost-optimized review

## Findings Addressed

### 1. Delete/Recycle missing from right-click context menu (HIGH — FIXED)
**Status:** Fixed

The context menu (`ImageContextMenu` in MainWindow.xaml) was missing a Delete/Recycle menu item, even though Delete is bound to a keyboard shortcut and is a primary action in fast-review workflows.

**Solution:** Added `DeleteMenuItem` as the second item in the context menu (right after Undo), consistent with Q-R38's ordering principle: primary actions first, then clustered features, then folders, then Settings. The menu item calls the same `_viewModel.RecycleAsync()` path as the Delete key.

**Follow-up (user review before merge):** a destructive item sitting right under Undo is an easy mis-click, so it's gated behind a new `AppSettings.ShowDeleteMenuItem` setting, **default off** — the item stays hidden until the user opts in from Settings → Right-click menu, same on/off pattern as `ShowFolderMenuItems`/`ShowZoomMenuItems`. The Delete keyboard shortcut is unaffected either way.

**Files changed:**
- `src/PhotoReview.App/MainWindow.xaml`: Added DeleteMenuItem
- `src/PhotoReview.App/MainWindow.xaml.cs`: Added Delete_Click handler; `ImageContextMenu_Opened` toggles `DeleteMenuItem.Visibility` from `AppSettings.ShowDeleteMenuItem` (default off)
- `src/PhotoReview.App/SettingsWindow.xaml` / `.xaml.cs`: Added the "Show Delete item" checkbox to the Right-click menu settings group
- `src/PhotoReview.Core/Settings/AppSettings.cs`: Added `ShowDeleteMenuItem` (default `false`)
- `src/PhotoReview.Core/Localization/Languages/en.json`: Added main.menu.delete, main.menu.delete.automationName, and settings.showDeleteMenuItem.{label,automationName,hint}
- `src/PhotoReview.Core/Localization/Languages/vi.json`: Added Vietnamese translations
- `tests/PhotoReview.Integration.Tests/ContextMenuRedesignTests.cs`: Updated test to expect 12 menu items (was 11); Delete now starts Collapsed (off) and toggles with the setting, like the zoom cluster; added DeleteMenuItem_Click_RecyclesCurrentImage, DeleteMenuItem_Visible_WhenSettingOn and DeleteMenuItem_Hidden_WhenSettingOff_ByDefault tests
- `tests/PhotoReview.Core.Tests/Settings/ContextMenuRedesignSettingsTests.cs`: Added default/round-trip/normalize coverage for `ShowDeleteMenuItem`

### 2. Shortcut hint visibility hardcoded as Collapsed (HIGH — REMOVED, not fixed)
**Status:** Removed

Line 140 of MainWindow.xaml had a TextBlock bound to `{loc:Tr main.shortcutHint}` but with hardcoded `Visibility="Collapsed"`, making it permanently invisible. This was inconsistent with every other info-overlay element, which bind visibility to a ViewModel property.

**Original plan (superseded):** un-hide it by binding to `{Binding InfoOverlay.IsFileInfoVisible, ...}`, same as StatusText.

**Actual decision (user review before merge):** the user didn't ask for this element to start showing, and the translator note already on the key agreed it wasn't worth keeping (`en.notes.json`: "Recommend deleting it instead of extracting" -- hard-coded shortcut names that drift from the real, user-configurable shortcuts, one more line of on-image clutter). So it was deleted outright rather than un-hidden: the TextBlock and its `main.shortcutHint` key are gone from the app entirely, not just hidden again.

**Files changed:**
- `src/PhotoReview.App/MainWindow.xaml`: Removed the shortcut-hint TextBlock
- `src/PhotoReview.Core/Localization/Languages/en.json` / `vi.json` / `en.notes.json`: Removed `main.shortcutHint`
- `docs/refactoring/i18n-inventory.json`: Removed the `main.shortcutHint` entry

### 3. Inconsistent label terminology: "Recycle" vs "Move to Recycle Bin" (MEDIUM — FIXED)
**Status:** Mostly Fixed

Inconsistent wording was found across the codebase:
- `action.recycle.name`: "Recycle" (English) vs "Đưa vào Thùng rác" (Vietnamese = "Move to Recycle Bin")
- `enum.fileOperation.recycle`: "Recycle" (English) vs "Đưa vào Thùng rác" (Vietnamese)
- `settings.shortcut.recycle`: "Move to Recycle Bin key"
- `batchReview.confirm`: "Move to Recycle Bin"
- New `main.menu.delete`: "Move to Recycle Bin"

Vietnamese already used the longer form, so no change was needed there. English terminology remains slightly inconsistent between brief forms ("Recycle") in action/enum names (used in status messages and dropdowns) and full forms ("Move to Recycle Bin") in user-facing button labels. This is acceptable since action.recycle.name and enum.fileOperation.recycle are used in context where brevity is valued (status messages, combo box options), while menu labels benefit from clarity.

**Files changed:**
- `src/PhotoReview.Core/Localization/Languages/en.json` / `vi.json`: `main.menu.delete`, `batchReview.confirm`, etc. use "Move to Recycle Bin"/"Đưa vào Thùng rác" consistently (the shortcut-hint string this finding originally touched no longer exists — see finding #2)

### 4. "Enter move" shortcut hint ambiguous about the modal picker (MEDIUM — MOOT, hint removed)
**Status:** Moot

The shortcut hint said "Enter move", which could imply an instant action. However, Enter actually opens a modal folder-picker dialog (MoveToFolderAsync calls the picker), making the wording ambiguous.

**Outcome:** finding #2 removed the shortcut-hint line entirely rather than un-hiding it, so this wording no longer exists anywhere in the app. No files changed for this finding specifically.

## Testing

Added `DeleteMenuItem_Click_RecyclesCurrentImage` integration test (Category=UI) to verify the Delete menu item routes correctly to RecycleAsync.

Updated `ContextMenuStructure_UndoFirst_DeleteSecond_FolderGroupTogglable_SettingsAlwaysLast` (formerly `ContextMenuStructure_UndoFirst_FolderGroupTogglable_SettingsAlwaysLast`) to account for the new menu item.

All changes are mutation-checked: tests fail if the guarded behavior is broken.

## Decision Log

No architectural or feature-flag decisions required. All changes are UI-facing only:
- Menu structure: follows established Q-R38 ordering
- Visibility binding: follows Q-R34 pattern (info overlays bind to ViewModel properties)
- Terminology: aligns with majority usage in codebase
- Shortcut clarification: wording change only, no behavior change
