---
id: CTX-MENU-CUSTOMIZE
order: 147
summary: |-
  Decided in the PR (user request 2026-10-09): every right-click menu item can be switched off (Settings > Mouse & zoom > Right-click menu, one tick box per item, stored as `HiddenContextMenuItems`, travels with Export/Import); the Settings item is locked on; separators follow the groups. New per-user Explorer command "Browse with PhotoReview" on folders and on the empty space inside a folder (HKCU, no administrator rights) behind `IShellIntegration`, switched on/off from Settings > General > Explorer integration.
---

# Customisable right-click menu + Explorer folder command

## 1. Right-click menu: per-item on/off

- **One list.** `ContextMenuItems.Items` (`src/PhotoReview.Core/Settings/ContextMenuLayout.cs`) lists every item with a stable id (the enum NAME is what `config.json` stores - never rename, only append), its group and parent. Top level: `Undo`, `MoveToRecycleBin` | `Fit`, `ZoomToLevel`, `ZoomSubmenu` | `OpenFolder`, `NextFolder`, `PreviousFolder` | `ExternalEditor` | `CopyFileName`, `CopyFullPath` | `Settings`. Zoom submenu children: `ZoomFitWidth`, `ZoomFitWidth2`, `ZoomFitHeight`, `ZoomPresets` (presets + Custom...), `ZoomLevelOptions` ("also set as click level" + "set current").
- **Setting.** `AppSettings.HiddenContextMenuItems` (`List<string>?`): the ids switched OFF. `null` = not set yet (config from before this change) -> derived from the legacy flags; an EMPTY list = show everything. `Settings` is never stored and never hidden.
- **Migration (explicit).** `SettingsStore.Migrate` fills a missing list from `ShowZoomMenuItems` / `ShowFolderMenuItems` / `ShowRecycleMenuItem` (a false flag hides every item it gated), so an old config shows exactly what it showed before. A brand-new config therefore keeps today's defaults (zoom cluster and Move to Recycle Bin hidden, folder group shown) - NOT "show all": switching on a destructive item for everybody was not asked for. Once the list exists it wins; Settings > Save writes the list and mirrors it back into the three legacy flags (an older build reading the file still behaves sensibly). `SettingsNormalizer` drops unknown / duplicate / locked names (reported as a repair).
- **Visibility rule (pure).** `ContextMenuItems.Compute(hidden, context, parent)`: an item shows when it is not hidden, its context allows it (Copy items need an open photo) and, for a submenu, at least one child shows. A separator is emitted only between two groups that both have something to show, so none is ever first, last or doubled, and `Settings` guarantees the menu is never empty. `MainWindow.ApplyContextMenuLayout` applies it on every open.
- **Settings UI.** One tick box per item (children indented), built from the same list; the `Settings` box is disabled and ticked with a tooltip explaining why. Restore defaults sets the list back to "not set" (= defaults). The old three check boxes and their `settings.show*MenuItems.*` keys are no longer referenced (kept in the catalogs to avoid editing the middle of shared files; remove in a later cleanup).

## 2. Explorer: "Browse with PhotoReview" on folders

- **App side.** Folder arguments already worked (`StartupCoreAsync` takes the first existing directory as `initialFolder`, the second-instance hand-off forwards files AND folders). New: `LaunchArguments.Normalize` makes a folder argument canonical (absolute, no trailing separator except a drive root, `.` segments resolved) so the instance lock, the Explorer prefetch and the folder load agree, and repairs `D:"` (what the quoted `"D:\"` of a drive-root background becomes in .NET `args`).
- **Registry.** `IShellIntegration` (`Core.Abstractions`) with `GetState / Register / Unregister`; `WindowsShellIntegration` (`Platform.Windows`) writes `HKCU\Software\Classes\Directory\shell\PhotoReview` (command `"<exe>" "%1"`) and `...\Directory\Background\shell\PhotoReview` (`"<exe>" "%V"`), each with the text "Browse with PhotoReview" and `Icon` = `"<exe>",0`. Per user, no administrator rights; `Unregister` deletes only those two keys. The pure `ShellMenuCommand` holds the exact strings and classifies the stored pair (`NotRegistered` / `Registered` / `RegisteredElsewhere`, the last also for a half-written pair).
- **Settings > General > Explorer integration.** Status line + "Add to Explorer menu" / "Update path" (when the stored exe differs: the app was moved) + "Remove from Explorer menu". It acts immediately (it is OS state, not a saved setting). Disabled when the process is not running from its own `.exe` (`dotnet` host).
- **Scripts.** `deploy/install-photo-review-association.ps1` also registers the folder command (`-NoFolderMenu` to skip); the uninstall script removes it.
- **Not touched:** the real Recycle Bin (no code here goes near it); the real registry in tests (fake `IShellIntegration`; the registry test is `Category=Native` on a private `HKCU\Software\PhotoReviewTests\<guid>` key, deleted in `Dispose`).

## Needs a human look

Right-click menu in the running app with various combinations switched off (no stray separators), the new Settings list, and the Explorer entry itself (register from Settings, right-click a folder and the empty space inside one, including a drive root; remove again).
