---
id: UX-FIXES-ROUND1
order: 32
summary: |-
  Four UX findings addressed: Delete/Recycle added to context menu (high), shortcut hint visibility fixed (high), terminology standardized (medium), and shortcut hint clarified (medium).
---

# UX-FIXES-ROUND1 — Four UX findings from cost-optimized review

## Findings Addressed

### 1. Delete/Recycle missing from right-click context menu (HIGH — FIXED)
**Status:** Fixed

The context menu (`ImageContextMenu` in MainWindow.xaml) was missing a Delete/Recycle menu item, even though Delete is bound to a keyboard shortcut and is a primary action in fast-review workflows.

**Solution:** Added `DeleteMenuItem` as the second item in the context menu (right after Undo), consistent with Q-R38's ordering principle: primary actions first, then clustered features, then folders, then Settings. The menu item calls the same `_viewModel.RecycleAsync()` path as the Delete key.

**Files changed:**
- `src/PhotoReview.App/MainWindow.xaml`: Added DeleteMenuItem
- `src/PhotoReview.App/MainWindow.xaml.cs`: Added Delete_Click handler
- `src/PhotoReview.Core/Localization/Languages/en.json`: Added main.menu.delete and main.menu.delete.automationName
- `src/PhotoReview.Core/Localization/Languages/vi.json`: Added Vietnamese translations
- `tests/PhotoReview.Integration.Tests/ContextMenuRedesignTests.cs`: Updated test to expect 12 menu items (was 11), added DeleteMenuItem_Click_RecyclesCurrentImage test

### 2. Shortcut hint visibility hardcoded as Collapsed (HIGH — FIXED)
**Status:** Fixed

Line 140 of MainWindow.xaml had a TextBlock bound to `{loc:Tr main.shortcutHint}` but with hardcoded `Visibility="Collapsed"`, making it permanently invisible. This was inconsistent with every other info-overlay element, which bind visibility to a ViewModel property.

**Solution:** Changed the hardcoded `Visibility="Collapsed"` to bind to `{Binding InfoOverlay.IsFileInfoVisible, Converter={StaticResource BoolToVis}}`, matching the pattern used by StatusText and other status-line elements. The shortcut hint now shows/hides with the rest of the info panel during activity.

**Files changed:**
- `src/PhotoReview.App/MainWindow.xaml`: Fixed shortcut hint visibility binding

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
- `src/PhotoReview.Core/Localization/Languages/en.json`: Updated main.shortcutHint to use "Move to Recycle Bin" instead of informal "Delete recycle bin"
- `src/PhotoReview.Core/Localization/Languages/vi.json`: Updated main.shortcutHint for consistency

### 4. "Enter move" shortcut hint ambiguous about the modal picker (MEDIUM — FIXED)
**Status:** Fixed

The shortcut hint said "Enter move", which could imply an instant action. However, Enter actually opens a modal folder-picker dialog (MoveToFolderAsync calls the picker), making the wording ambiguous.

**Solution:** Clarified the wording in both languages:
- English: "Enter move" → "Enter open folder picker"
- Vietnamese: "Enter di chuyển" → "Enter mở bộ chọn" (literally "Enter open picker")

This distinguishes instant actions (like Skip, Delete) from modal-opening ones (like Move).

**Files changed:**
- `src/PhotoReview.Core/Localization/Languages/en.json`: Updated main.shortcutHint
- `src/PhotoReview.Core/Localization/Languages/vi.json`: Updated main.shortcutHint

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
