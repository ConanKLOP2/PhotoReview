---
id: RV-D4
order: 47
summary: |-
  Changing "Image order" in Settings reloads the open folder at the current image (the same path as RawSupport/RawPairMode), so the new order applies immediately instead of at the next folder (option A, 2026-10-01).
---

# RV-D4 - Image order change in Settings (RV-A12)

**Question:** the sort mode is read once per folder load (`FolderLoadCoordinator`), so changing "Image order" in Settings left the open folder in the old order until the next folder was opened.

**Options:** A reload the open folder on change (same path as RawSupport/RawPairMode) - B keep, change the hint text to "applies to the next folder you open".

**Decision (user, 2026-10-01): A.** `MainViewModel.ShowSettings` adds `ImageSortMode` to the `reloadFolder` condition; the reload keeps the current image as `initialPath`
(`ReloadFolderAfterSettingsAsync`). Waits for an in-flight file action like the other reload triggers. Test:
`MainViewModelAdvancedTests.ShowSettings_ImageSortModeChanged_ReloadsTheFolderAtTheCurrentImage`.
