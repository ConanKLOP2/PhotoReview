---
id: FEAT-RENAME-STATE
order: 1420
summary: |-
  PR2 "Trạng thái" của FEAT-RENAME-DESIGN (CHƯA XÁC NHẬN, Q3/Q6/Q7 theo khuyến nghị): ReviewCatalog.RenameEntry/RenameEntries (re-key tại chỗ, giữ chỉ số, nhóm Jpeg/Raw/Xmp), ImagePresenter.RetargetPath (không PresentAsync), re-key session CurrentPath/Skipped, evict cache và _originalDimensions, ZoomDetailLoader.RetargetPath, FileActionController.RenameAsync (StopForAction, hủy preload, Undo-rename re-key), MainViewModel.RenameCurrentAsync (cổng cho PR3). Chưa UI/menu/phím (PR3), chưa sidecar nâng cao (PR4). 31 mutant: 0 sống.
---

# FEAT-RENAME-STATE — Trạng thái đổi tên tệp (PR2, 2026-10-15, CHƯA XÁC NHẬN)

PR2 của [`FEAT-RENAME-DESIGN.md`](FEAT-RENAME-DESIGN.md), dựa trên lõi [`FEAT-RENAME-CORE.md`](FEAT-RENAME-CORE.md) (#527). **Chưa có UI/menu/phím** (PR3):
không đường nào trong giao diện gọi `MainViewModel.RenameCurrentAsync` nên hành vi hiện tại không đổi. Không sửa bản WPF
(`PhotoReview.App`); code dùng chung phải biên dịch và test WPF/App.Tests xanh.

## 1. Đã làm (theo lớp)

| Lớp | Nội dung |
|---|---|
| Core | `ReviewCatalog.RenameEntry/RenameEntries`: re-key tại chỗ, giữ index/CurrentIndex/Length/mtime/kích thước, nhóm Jpeg/Raw/Xmp, tăng `StructuralVersion`, từ chối khi trùng tên (catalog nguyên vẹn) |
| Imaging | `ImageCacheKey.WithPath`; `PreviewImageService.EvictCachedPath` xoá thêm `_originalDimensions`. KHÔNG re-key cache RAM (tối ưu tuỳ chọn, bỏ qua; khoá cũ mồ côi lành tính vì chứa path+len+mtime) |
| App.Shared | `ZoomDetailLoader.RetargetPath`; `ImagePresenter.RetargetPath` + `IsPresentInFlight` (KHÔNG `PresentAsync`; compare đang hiện hoặc present đang bay: trả false để caller present lại); `IFileActionSink.OnFilesRenamed` (mặc định false) |
| ViewModel | `MainViewModel.OnFilesRenamed` (re-key session `CurrentPath`/`Skipped` + persist, `_lastNotifiedImagePath`, evict cache cũ); `RenameCurrentAsync(newName)` qua `FileActionGate` + chờ `PendingOrder` (INV-9), bỏ khi đổi thư mục (R02) |
| Controller | `FileActionController.RenameAsync`: validate, `StopForAction`, huỷ preload, `RenameAsync`/`RenameGroupAsync`, `RenameEntries`, `OnFilesRenamed`, preload lại; `NoChange`/`Invalid` không đụng gì; thất bại = re-arm; đổi thư mục giữa chừng = late status. Undo-rename re-key new sang old (nhớ tên bằng `_renamedTo`, nhận biết bằng tham chiếu `LastUndoAction`, `StopForAction` trước undo); Q3: dừng ở Undo ô đơn |
| i18n | 4 khoá thêm cuối en/vi/notes: `status.renamed`, `status.renameFailed`, `status.renameUnchanged`, `status.renameUndone` |

## 2. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

| ID | Quyết định | Phương án khác | Hoàn tác |
|---|---|---|---|
| RS-1 (Q6) | Ảnh ở NGUYÊN CHỖ (cùng chỉ số), không nạp lại thư mục, không `PresentAsync`: zoom/pan/Keep zoom nguyên vẹn | Chèn lại theo tên mới; nạp lại | Đổi `RenameEntries` thành xoá và chèn lại |
| RS-2 (Q3) | Rename vào ô Undo đơn, ghi đè Undo trước | Ngăn xếp Undo riêng | Revert nhánh `TryHandleRenameUndoAsync` |
| RS-3 (Q7) | Thất bại không đổi gì = Dismissed (không vào Recovery) | Failed | Đổi điều kiện `IsRenameUntouched` (PR1) |
| RS-4 | Không re-key cache RAM; evict theo khoá cũ | Re-key để giữ ảnh đã giải mã | Thêm re-key trong `EvictCachedPath` |

Hoàn tác tổng: không gọi `MainViewModel.RenameCurrentAsync` (chưa có người gọi) hoặc revert PR này.

## 3. Đột biến thủ công (hai bước, `docs/MUTATION-TESTING.md`)

31 mutant (M1-M30 + M19b), mọi mutant biên dịch: **27 killed bước 1, 4 killed bước 2, 0 sống, 0 tương đương**.
M10/M12/M13/M15 chỉ bị giết ở bước 2 vì filter bước 1 (`~ImagePresenterRetarget`) không khớp: các test Retarget nằm trong lớp
partial `ImagePresenterTests` (không phải lớp riêng); kiến nghị đổi filter thành `ImagePresenterTests.RetargetPath|IsPresentInFlight`.

| Id | Vị trí | Đột biến | Kết quả | Test bắt được |
|---|---|---|---|---|
| M1 | ReviewCatalog.RenameEntries | bỏ InvalidateIndex() | killed bước 1 | ReviewCatalogRenameTests.Remove_AfterRename_RemovesTheRenamedEntryAndNotAnother |
| M2 | ReviewCatalog.RenameEntries | bỏ kiểm tra xung đột tên (Path và member) | killed bước 1 | RenameEntries_GroupMemberTakenByOtherEntry_ReturnsFalseAndLeavesCatalogUntouched |
| M3 | RenameInEntry | giữ CaptureGroup cũ | killed bước 1 | RenameEntries_GroupWithSubsetOfKeys_RespellsOnlyMappedMembers |
| M4 | RenameInEntry | bỏ ánh xạ XmpPath | killed bước 1 | RenameEntries_PairedGroup_RespellsEveryMemberAndRepresentative |
| M5 | ImageCacheKey.WithPath | Length = 0 | killed bước 1 | ImageCacheKeyTests WithPath |
| M6 | PreviewImageService.EvictCachedPath | bỏ _originalDimensions.RemoveWhere | killed bước 1 | PreviewImageServiceGapTests2.EvictCachedPath_DropsOnlyThatPathsOriginalDimensions |
| M7 | ZoomDetailLoader.RetargetPath | bỏ CancelPending() | killed bước 1 | ZoomDetailLoaderRetargetTests.RetargetPath_DecodeInFlightForTheOldPath_ItsPixelsAreNeverShown |
| M8 | ZoomDetailLoader.RetargetPath | giữ Token cũ | killed bước 1 | ZoomDetailLoaderRetargetTests...AfterStopForActionAndZoom_DecodesTheOriginalViaTheNewPathOnly |
| M9 | ZoomDetailLoader.RetargetPath | giữ Key cũ | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_ZoomedAndTheOriginalNotYetDecoded_DecodesItViaTheNewName |
| M10 | ImagePresenter.RetargetPath | không remap _presentedFilePath | killed bước 2 | ImagePresenterTests.RetargetPath_ThenPresentSameEntry_IsNotReportedAsAFileChange |
| M11 | ImagePresenter.RetargetPath | không remap _currentNavigationPath | killed bước 1 | ImagePresenterTests.RetargetPath_ShownFileRenamed_RekeysPathInfoAndStatus... |
| M12 | ImagePresenter.RetargetPath | bỏ IsPresentInFlight | killed bước 2 | ImagePresenterTests.RetargetPath_PresentInFlight_ReturnsFalseSoTheCallerPresentsAgain |
| M13 | ImagePresenter.RetargetPath | không Clear compare | killed bước 2 | ImagePresenterTests.RetargetPath_CompareVisible_ClearsCompareAndReturnsFalse... |
| M14 | ImagePresenter.RetargetPath | bỏ _zoomDetail.RetargetPath | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_ZoomedAndTheOriginalNotYetDecoded_... |
| M15 | ImagePresenter.PresentAsync | không tăng _presentsInFlight | killed bước 2 | ImagePresenterTests.RetargetPath_PresentInFlight_ReturnsFalse... |
| M16 | FileActionController.RenameAsync | bỏ _clock.StopForAction() | killed bước 1 | FileActionControllerRenameTests.RenameAsync_EffectsRunInTheDocumentedOrder... |
| M17 | FileActionController.RenameAsync | bỏ _preloadController?.Cancel() | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_CancelsPreloadThenWarmsAroundTheCurrentIndex |
| M18 | FileActionController.RenameAsync | bỏ _catalog.RenameEntries(renames) | killed bước 1 | RenameAsync_CaptureGroup_RespellsEveryMemberAndRegistersTheGroupForUndo |
| M19 | FileActionController.RenameAsync | bỏ RegisterRenameForUndo (nhánh chính) | killed bước 1 | RenameAsync_CaptureGroup_..._RegistersTheGroupForUndo |
| M19b | FileActionController.RenameAsync | bỏ RegisterRenameForUndo (nhánh đã rời thư mục) | killed bước 1 | RenameAsync_FolderSwitchedDuringTheIo_RenameStaysUndoableAndTheNewFoldersStateIsUntouched |
| M20 | FileActionController.RenameAsync | luôn PresentAsync (kept=false) | killed bước 1 | RenameAsync_EffectsRunInTheDocumentedOrder... |
| M21 | FileActionController.RenameAsync | StopForAction cả khi IsUnchanged | killed bước 1 | FileActionControllerRenameTests.RenameAsync_SameName_IsNoChangeAndTouchesNothing |
| M22 | RegisterRenameForUndo | không nhớ _renamedTo | killed bước 1 | FileActionControllerRenameUndoTests.UndoLast_AfterCaptureGroupRename_UsesTheRememberedNamesForEveryMember |
| M23 | UndoLastAsync | bỏ StopForAction của nhánh rename-undo | killed bước 1 | FileActionControllerRenameUndoTests.UndoLast_ClockAndPreloadAreStoppedBeforeTheUndoIo |
| M24 | UndoLastAsync | bỏ TryHandleRenameUndoAsync | killed bước 1 | UndoLast_AfterSingleRename_RekeysBackInPlaceWithoutInsertingOrPresenting |
| M25 | TryHandleRenameUndoAsync | bỏ _catalog.RenameEntries(undone) | killed bước 1 | UndoLast_AfterCaptureGroupRename_...; UndoLast_AfterSingleRename_... |
| M26 | MainViewModel.OnFilesRenamed | bỏ re-key session CurrentPath | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_RekeysTheSessionAndPersistsIt |
| M27 | MainViewModel.OnFilesRenamed | bỏ re-key Skipped | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_RekeysTheSkippedList |
| M28 | MainViewModel.OnFilesRenamed | bỏ re-key _lastNotifiedImagePath | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_ThenNotifyAndZoomSwap_TheViewerStillSeesTheSameImage |
| M29 | MainViewModel.OnFilesRenamed | bỏ vòng EvictCachedPath | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_EvictsThePreviewCacheOfTheOldName |
| M30 | MainViewModel.OnFilesRenamed | bỏ PersistSession | killed bước 1 | MainViewModelRenameTests.RenameCurrentAsync_RekeysTheSessionAndPersistsIt |

## 4. Chưa kiểm
- GUI trên máy thật và test UI/Shell (chưa có UI: PR3).
- Re-key cache RAM (bỏ qua có chủ ý), ZoomDetail với RAW full decode thật.
- Native/NTFS thật cho các ca case-only (đã ghi ở FEAT-RENAME-CORE.md).

## 5. Còn lại
PR3: hộp thoại nhập tên (`RenamePromptDialog`), `ContextMenuItemId.RenameFile`, phím F2 tuỳ chọn, i18n, test BitBlt có khẳng định không-toàn-nền. PR4: dò sidecar nâng cao.
