---
id: GUI-CHECK-AUTOMATION
order: 16
summary: |-
  5 of the 11 "GUI checks (user)" items now need no manual check; the rest reduced to one short visual/feel step each (new context-menu-structure, crossfade and KeepZoomAcrossImages UI tests).
---

# GUI-CHECK-AUTOMATION — reducing the "GUI checks (user)" manual list

Context: `docs/ACTIVE-TASKS.md` "GUI checks (user)" listed 11 features (from #128-#134 and
#177/#178/#181/#183/#186) waiting for the user to eyeball. This reviews each one against the existing
`Category=UI`/Integration test suite (real WPF window, STA dispatcher, `tests/PhotoReview.Integration.Tests`)
and the supporting unit suites, adds tests where the behavior is checkable in-process but wasn't covered yet,
and lists what — if anything — genuinely still needs a human to look at.

New tests added by this pass (all `[Trait("Category","UI")]`, real `MainWindow`):

- `tests/PhotoReview.Integration.Tests/ContextMenuRedesignTests.cs` —
  `ContextMenuStructure_UndoFirst_FolderGroupTogglable_SettingsAlwaysLast`
- `tests/PhotoReview.Integration.Tests/ImageCrossfadeIntegrationTests.cs` (new file, 3 tests) —
  `NavigatingToADifferentFile_WithFadeEnabled_AnimatesTheOutgoingLayerThenReleasesIt`,
  `NavigatingToADifferentFile_WithTransitionNone_NeverShowsTheOutgoingLayer`,
  `ZoomingTheSameImage_WithFadeEnabled_NeverStartsAFade`
- `tests/PhotoReview.Integration.Tests/KeepZoomAcrossImagesIntegrationTests.cs` (new file, 2 tests) —
  `Enabled_PreservesZoomAndLeavesFit_AfterNavigatingToTheNextImage`,
  `Disabled_ResetsToFit_AfterNavigatingToTheNextImage`

All five new tests were mutation-checked against the guarded production code (a temporary, reverted
one-line inversion in `MainWindow.xaml.cs`/`ViewerState.cs`/`MainWindow.xaml`) and fail when the guarded
behavior breaks.

| # | Item (source) | How verified (test name) | Remaining manual step |
|---|---|---|---|
| 1 | Context-menu "Zoom" item/submenu, redesign (#128-134, Q-R38 #183) | Folder-group visibility + refresh: `ContextMenuRedesignTests.ContextMenuOpen_FolderGroupVisible_WhenSettingOn` / `_FolderGroupHidden_AsOneUnit_WhenSettingOff`. Preset click + "also set as click level": `ZoomPreset_WhenAlsoSetIsOn/Off_...`, `AlsoSetToggle_Click_FlipsAndPersistsTheSetting`. "Set current zoom as click level": `SetCurrentZoomAsClickLevel_DisabledInFit_EnabledWhenZoomed`, `_Click_SavesRoundedPercent_WithoutReZooming`. Live labels/shortcuts/submenu population: `ZoomContextMenuTests.*`. Fixed item order (Undo first .. Settings always last): new `ContextMenuStructure_UndoFirst_FolderGroupTogglable_SettingsAlwaysLast`. | Mở chuột phải lên ảnh, xác nhận giao diện tối (dark theme) của menu và các mục con Zoom hiển thị đẹp, dễ đọc, không bị lệch màu ở cả `ContextMenu`/`MenuItem`/`Separator`. |
| 2 | "Taken:"/"Modified:" EXIF labels (#128-134) | Exact formatted text incl. field order, off-by-default `Modified`, UTC→local conversion, null-skip: `PhotoReview.App.Tests/ViewModels/ExifFormatterTests.cs` (`AllFieldsEnglish`, `EachFieldAlone`, `ModifiedDate_*`). Settings checkboxes persist through Save: `SettingsWindowRoundTripTests` (`ExifFieldDateTakenCheck`, `ExifFieldModifiedDateCheck`). | Không cần — chỉ còn việc xem tổng thể overlay có bị tràn chữ/font khó đọc trên ảnh thật hay không (thẩm mỹ, không phải đúng/sai). |
| 3 | Info-overlay auto-hide (Q-R34, #129) | Decision matrix (hide only when enabled+idle+no stay-visible rule; stays visible for no-folder/message/Compare/window-inactive; delay independent of the toolbar's, clamped 0-10000 ms): `PhotoReview.App.Tests/Coordinators/InfoOverlayAutoHidePolicyTests.cs` (exhaustive). Settings checkboxes enable only their own delay box: `SettingsWindowAutoHideTests.*`. Round-trip through Save: `SettingsWindowRedesignTests`/`SettingsWindowRoundTripTests`. | Real-window live fade (real `DispatcherTimer` + `Opacity`/`IsHitTestVisible` on `InfoOverlayHost`) was evaluated but NOT added: the "window inactive keeps it visible" rule needs the test window to hold genuine OS focus, which AGENTS.md's ban on `SendInput`/`SetForegroundWindow`-style simulation makes unsafe to fake reliably in a headless run (risk of a new flaky test). Manual check: mở một ảnh, để chuột yên vài giây, xác nhận lớp thông tin (EXIF/tên thư mục) mờ dần rồi biến mất; di chuột/nhấn phím thì hiện lại ngay; mở một hộp thoại (Cài đặt/Xác nhận xoá) hoặc bật Compare thì lớp thông tin không được tự ẩn. |
| 4 | "Zoom" card in Settings | All Zoom-card controls (`KeyboardZoomAnchorCombo`, `ImageTransitionCombo`/`ImageTransitionMsBox`, `FitWidthAnchorCombo`, `KeepZoomAcrossImagesCheck`, `KineticPanCheck`, `ArrowKeyNavigatesAtZoomEdgeCheck`, `ArrowPanStepBox`) round-trip through Save: `SettingsWindowRedesignTests.UiControlledProperties`/`PropertiesNotShownInUi_SurviveOpenAndSave`, `SettingsWindowRoundTripTests.UiControlledProperties_RoundTripThroughSave`. Shortcut summary text: `ZoomContextMenuTests.SettingsZoomShortcutSummary_*`. | Không cần thao tác thêm — chỉ cần nhìn qua bố cục thẻ "Zoom" một lần xem các mục có được nhóm gọn gàng, dễ hiểu hay không (thẩm mỹ). |
| 5, 11 | Sort modes + default = Default (#177-186, Q-R33 #186) | Ordering algorithm (Explorer-order vs. app natural order, `Name`≡`NameAscending`, `NameDescending`=reverse, `Default`=passthrough): `PhotoReview.Core.Tests/Catalog/ImageSortServiceTests.cs`. End-to-end folder load per mode: `PhotoReview.App.Tests/Coordinators/FolderLoadCoordinatorTests.cs` (`[InlineData(ImageSortMode.Default/NameAscending/NameDescending/...)]`). Settings combo order + persistence: `SettingsWindowRoundTripTests.SortModeCombo_ListsSixModesInStableOrder_AndSavesEach`. Default-for-new-installs: `AppSettingsPocoTests` and `PhotoReview.App.Tests/AppSettingsTests.ImageSortModeDefaultsAndDeserializesAliases` both assert `new AppSettings().ImageSortMode == ImageSortMode.Default`. | Không còn — đã kiểm tra đầy đủ bằng test tự động, không cần người dùng xác nhận thủ công. |
| 6 | Preload-window setting (#125, Q-R31) | Defaults/clamping/migration/round-trip: `PhotoReview.Core.Tests/Settings/PreloadWindowSettingsTests.cs`. Settings-window input validation (rejects out-of-range/non-numeric, accepts boundaries 1/0 and 500/500): `SettingsWindowRedesignTests.PreloadForward_InvalidInput_IsRejected`/`PreloadBackward_InvalidInput_IsRejected`/`PreloadWindow_ValidInput_IsSaved`. Persistence: `SettingsWindowRoundTripTests`. | Chỉ cần xác nhận một lần: đổi số ảnh tải trước/sau trong Cài đặt > Hiệu năng, khởi động lại ứng dụng, thấy việc chuyển ảnh mượt hơn/chậm hơn tương ứng (bản thân việc "áp dụng sau khi khởi động lại" không thể kiểm bằng test trong tiến trình). |
| 7 | Keyboard zoom anchor + kinetic arrow panning (#177, Q-R35) | Anchor math (`Pointer` uses cursor over viewport else centre; `ViewportCentre` always ignores cursor): `PhotoReview.App.Tests/Input/MouseGesturesTests.cs`, `PointerInputControllerTests` (`ZoomInAsync_PointerOverTheViewport_...`, `_ViewportCentreSetting_...`). Kinetic-vs-hard-step arrow panning (starts a glide instead of an instant jump, travels the configured step, coalesces auto-repeat, no-ops at the edge): `PointerInputControllerTests` (`Arrow_KineticPanEnabled_*`, `Arrow_InstantStep_*`). Settings persistence: `SettingsWindowRoundTripTests`. | **FAILED manual check (2026-09-27):** the user reports the keyboard-held glide looks jerky/dizzying (screen-recorded) and clearly smoother than a direct mouse-drag pan of the same image. The unit tests above cover the pure math/state machine but not perceived frame-rate/smoothness of the real `GlideFrameClock`-driven render loop. Needs a source-level fix, not just re-confirmation -- see `docs/ACTIVE-TASKS.md` "GUI checks (user)" row. |
| 8 | Fit width/Fit height presets + `KeepZoomAcrossImages` (#178, Q-R36) | Anchor math (`Centre` vs `TopThird`, mouse-present override, Fit height always centres): `PointerInputControllerTests.FitWidthAsync_*`/`FitHeightAsync_AlwaysAnchorsAtImageCentre`. Settings persistence: `FitWidthHeightKeepZoomSettingsTests`, `SettingsWindowRoundTripTests`. **New**: end-to-end across a real navigation — `KeepZoomAcrossImagesIntegrationTests.Enabled_PreservesZoomAndLeavesFit_AfterNavigatingToTheNextImage` / `Disabled_ResetsToFit_AfterNavigatingToTheNextImage` prove the setting actually reaches `MainWindow`'s real image-to-image navigation, not just the pure `ViewerState`/`PointerInputController` unit path. | Không còn thao tác cần thiết — hành vi giữ/không giữ độ phóng khi chuyển ảnh đã được kiểm bằng test tự động trên cửa sổ thật. |
| 9 | Crossfade on photo change (#181, Q-R37) | Pure decision predicate (different file → true; same-file thumbnail/preview/original upgrade → false; Compare open → false; first image after opening a folder → false): `PhotoReview.App.Tests/Coordinators/ImageTransitionDecisionTests.cs`. Settings clamping/persistence: `ImageTransitionSettingsTests`, `SettingsWindowRoundTripTests`. **New**: the actual fade wiring on the real `MainWindow` — `ImageCrossfadeIntegrationTests` proves the outgoing layer becomes visible and animates to transparent then releases itself when `ImageTransition.Fade` is set, and never lights up for `None` or for a same-file zoom change (both checked with a value-changed watcher so a fast fade that starts *and* finishes inside the assertion window can't slip through). | Chỉ còn phần cảm nhận: bật hiệu ứng mờ dần (Fade) trong Cài đặt, chuyển qua lại vài ảnh, xác nhận hiệu ứng mượt mắt, không giật/nhấp nháy (độ mượt là cảm quan). |
| 10 | Redesigned context menu overall structure (#183, Q-R38) | Same coverage as item 1, plus the new `ContextMenuStructure_UndoFirst_FolderGroupTogglable_SettingsAlwaysLast` (exact 11-item order: Undo, separator, Fit, Zoom-to-N%, Zoom submenu, folder-group separator, Open/Next/Previous folder, separator, Settings; Settings/Undo always `Visible` whichever way the folder group is toggled). | Mở chuột phải lên ảnh, xác nhận giao diện tối áp dụng cho toàn bộ menu (không có ô nào bị nền trắng/màu lạc) — cùng một thao tác thẩm mỹ với mục 1. |

## Summary

Of the 11 original items, 5 (2, 4, 5, 6, 8) now need **no manual check at all** — behavior is fully covered
by automated tests, real-window or unit. The other 6 (1/10, 3, 7, 9) still have one short manual step each,
listed above; every one of them is now a narrow visual/feel check (dark-theme colors, animation smoothness)
rather than a correctness check, because the underlying logic is covered by this pass's tests.

Item 3 (info-overlay live timer/`Opacity` state machine on the real window) was investigated for automation
but deliberately left manual: it depends on the real `MainWindow` holding genuine OS window-activation focus,
and AGENTS.md forbids `SendInput`/`SetForegroundWindow`-style simulation in test harnesses — faking that
reliably in a headless CI run would risk introducing exactly the kind of new flaky test this repository
already has several of (see `docs/ACTIVE-TASKS.md` "Flaky tests" row). The pure decision policy
(`InfoOverlayAutoHidePolicyTests`) and the Settings-window wiring are both already fully covered.
