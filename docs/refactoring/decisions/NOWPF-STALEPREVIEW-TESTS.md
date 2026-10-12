---
id: NOWPF-STALEPREVIEW
order: 1060
summary: |-
  Bước 2 kế hoạch "chứng minh bằng bằng chứng" (2026-10-13): bằng chứng TẤT ĐỊNH cho lỗi "preview CŨ ghi đè preview MỚI" trên mọi đường
  hiển thị (ImagePresenter dùng chung WPF/shell, PreviewImageService, composer/GpuImageCache/prefetcher/sink của shell Win32). Scheduler và
  decoder giả ép thứ tự hoàn tất đảo ngược; bất biến kiểm trên TOÀN BỘ nhật ký sink, không chỉ trạng thái cuối. Tìm thấy 1 lỗi thật
  (Win32PresentationSink mất FIFO khi UI thread tới sau cập nhật cross-thread còn trong hàng đợi, PR #476, đã sửa).
---

# NOWPF-STALEPREVIEW - test tất định cho "preview cũ ghi đè preview mới" (2026-10-13)

Tiền đề: INV-1 trong [architecture.md](../../architecture.md) (cache identity đầy đủ, không present pixel cũ), `ImagePresenter`
([NOWPF-WP21-PRESENTATION](NOWPF-WP21-PRESENTATION.md) cho shell Win32). Không đo thời gian: mọi thứ có thể "về muộn" là một cổng
(gate) do test mở theo thứ tự test chọn; không `Thread.Sleep`/`Task.Delay`, không so `ManagedThreadId`.

## 1. Bảng đường hiển thị x kiểu đảo thứ tự

Ký hiệu: CÓ = đã có test trước PR này; THIẾU = chưa có; LẤP = PR này thêm test (tên trong cột cuối).

| Đường hiển thị | Kiểu đảo thứ tự | Trước | Test lấp / ghi chú |
|---|---|---|---|
| `ImagePresenter` (WPF App và shell) | burst, decode viewer hoàn tất ngẫu nhiên (7 hạt) | CÓ (`BurstCompletingInRandomOrder`) | nay thay bằng vét cạn: `Burst_ThreeFiles_EveryPreviewCompletionOrder_...` (15 thứ tự x {decode đã chạy / chưa}) |
| `ImagePresenter` | revisit: 0, 1, 0 (decode cũ của cùng file) | THIẾU | `Burst_RevisitingTheFirstFile_...` (8 thứ tự) |
| `ImagePresenter` + `ThumbnailCache` | thumbnail của ảnh cũ về SAU preview ảnh mới | THIẾU (chỉ có "bị thay giữa chừng, không lỗi chưa quan sát") | `LateThumbnailOfAnOlderNavigation_...`, `Burst_TwoFiles_EveryThumbnailAndPreviewOrder_...` (40), mẫu 3 file |
| `ImagePresenter` + `PreviewImageService` | decode preload ĐANG CHẠY (không huỷ được) về ngược thứ tự | THIẾU | `JoinedPreloadDecodes_FinishingInReverseOrder_...` |
| `ImagePresenter` + `PreviewImageService` | decode viewer đã bắt đầu rồi bị thay, về muộn: không hiện, cache đúng key, lần thăm lại không decode lại | THIẾU (chỉ "decode chưa bắt đầu thì bị bỏ") | `SupersededRunningDecode_FinishingLate_...` |
| `ImagePresenter` | decode cũ LỖI muộn (FileNotFound = gỡ khỏi catalog; lỗi chung = trạng thái lỗi) | CÓ cho nav hiện tại, THIẾU cho nav đã bị thay | `LateDecodeFailureOfAnOlderNavigation_...` |
| `ImagePresenter` + `ClearCache` | xoá cache giữa lúc decode: nav hiện tại vẫn hiện, nav cũ không nạp lại cache | CÓ ở mức service | `ClearCacheDuringDecodes_...` |
| `ImagePresenter` + `ZoomDetailLoader` | bản gốc (preview -> full) về sau khi đã đổi ảnh | CÓ (`ZoomDetailTests`, `ZoomDetailLoaderGapTests`) | bổ sung ở mức nhật ký sink: `LateZoomOriginal_...`, `ZoomOriginal_OfTheCurrentImage_...` |
| `ImagePresenter` | stat làm mới sau present về muộn (Ready/session của ảnh cũ) | THIẾU | `PostPresentStat_OfASupersededNavigation_...` |
| `ImagePresenter` + `CompareViewModel` (2 ô) | partner / cặp cũ về muộn; cặp B đè cặp A; partner lỗi muộn | CÓ ở `CompareViewModel` (token), THIẾU ở presenter | `ComparePartnerOfASupersededPair_...`, `ComparePairOverwrittenByANewerPair_...`, `ComparePartnerFailingLate_...` |
| status / overlay / PhotoInfo / kích thước gốc | thuộc ảnh đúng sau mọi đảo thứ tự | THIẾU (chỉ kiểm trạng thái cuối) | `AssertNoStaleOverwrite` kiểm từng cuộc gọi sink; `AssertSettledOnAsync` kiểm trạng thái cuối |
| `PreviewImageService` | ClearCache giữa lúc decode; persist cũ; joiner của creator bị huỷ | CÓ (`PreviewImageServiceGapTests`, `...PersistStaleTests`, `...MutationTests`) | không thêm |
| `ThumbnailCache` | ClearMemory giữa lúc tải; caller huỷ | CÓ (`ThumbnailCacheMutationTests`, `...LifecycleTests`) | không thêm |
| `PresentationComposer` | upgrade preview->full: hạn chót cũ, file đổi, đĩa bị giải phóng | CÓ (`PresentationComposerTests`) | thêm `Composer_EverySequenceOfFiveEvents_...` (16 807 chuỗi, bất biến sau TỪNG bước), `Composer_OlderUpgradeUploadingFirst_...` |
| `GpuImageCache` | mất thiết bị giữa lúc prefetch, dải cũ chạy sau | CÓ phần (`DeviceRecreated_ReleasesEverything...`) | `GpuCache_DeviceLostMidPrefetch_...`, `Composer_DeviceLostDuringPendingUpgrade_...` |
| `PresentationPrefetcher` | tra cache (nền) của lượt cũ xong SAU khi lượt mới đã áp | THIẾU (host test chạy nền đồng bộ) | `Prefetcher_LateLookupResultOfAnOlderNavigation_...` |
| `Win32PresentationSink` | cập nhật cross-thread còn trong hàng đợi, UI thread tới sau | THIẾU, **LỖI THẬT** | PR #476: `UiThreadUpdate_WhileAnEarlierCrossThreadUpdateIsQueued_RunsAfterIt` (đỏ -> xanh) |
| `WpfPresentationSink` | cùng kiểu | CÓ theo cấu trúc (`Dispatcher.Invoke` đồng bộ) | không thêm |
| Crossfade giữa chừng khi ảnh đổi | `BeginCrossfade` giữa chuỗi sự kiện | CÓ (`CrossfadeLayerTests`) | nằm trong chuỗi vét cạn của composer (toán tử vẽ+đóng băng) |
| Keo `ShellMainWindow.Viewing` (shell thật) | decode ảnh mới xong trước, ảnh cũ về muộn | CÓ nhưng theo thời gian (`RapidNavigation_*`) | `ShellStalePreviewTests` (cổng decode, không chờ thời gian) |

## 2. Cách hoạt động

- `StalePreviewHarness` (App.Tests): `ImagePresenter` + `PreviewImageService` + `ThumbnailCache` + `GenerationClock` thật, chạy trên
  một thread UI giả (`FakeUiThread`, có `SynchronizationContext`) nên "kiểm token rồi chạm màn hình" là nguyên tử như trên UI thread
  thật. Chỉ decoder và đọc thumbnail là giả (cổng theo từng file: preview, thumbnail, bản gốc, lỗi; cổng stat sau present).
- Rào chắn (barrier) thay cho thời gian: bước trả về khi presenter đã THẬT SỰ phản ứng (nav task kết thúc, hoặc thumbnail đã hiện).
  Khi cả hai khe viewer bị decode cũ giữ (thiết kế `ViewerDecodeSlots = 2`) thì decode mới chưa chạy được; rào chắn nhận ra và không chờ.
- Bất biến (`AssertNoStaleOverwrite`), kiểm trên MỌI sự kiện của sink: (1) sự kiện thuộc navigation đang hiện hành khi nó xảy ra,
  (2) navigation gắn với sự kiện không bao giờ đi lùi, (3) ảnh/status/OnPresented đúng file, (4) `ApplyInitialViewMode` chỉ khi ảnh
  đang hiện thuộc navigation đó (khỏi đặt lại zoom ảnh khác). Trạng thái cuối (`AssertSettledOnAsync`): ảnh, kích thước gốc, PhotoInfo,
  đường dẫn, status, chỉ mục catalog, compare đều của navigation cuối.
- Số thứ tự: vét cạn mọi tuyến tính hoá (N0<N1<N2, Pf sau Nf): 3 file = 15, 0-1-0 = 8, 2 file có thumbnail = 40; 3 file có thumbnail
  là mẫu hạt cố định (không khả thi vét cạn).

## 3. Thêm một ca mới

1. Test đơn lẻ: tạo `StalePreviewHarness(["p0.jpg", ...])`, `NavigateAsync(i)`, mở cổng bằng `ReleasePreviewAsync/ReleaseThumbnailAsync/
   OpenPreview/OpenOriginal`, `SettleAsync()`, rồi `AssertNoStaleOverwrite(...)` + `AssertSettledOnAsync(...)`. Cần chờ một mốc mà
   rào chắn không biết (compare, stat) thì chờ `PreviewStartedAsync`/`StatGate.SecondStatStarted`, không chờ thời gian.
2. Thêm thứ tự: sửa `Orders(targets, thumbnails)`; khẳng định số thứ tự để chính bộ liệt kê cũng được bảo vệ.
3. Lớp shell: dùng `PresentationStaleOrderTests.Rig` (backend GPU giả + `ManualDispatcher` + đồng hồ/hạn chót là danh sách hành động);
   thêm một mã thao tác vào `Rig.Apply` thì vét cạn tự phủ.

## 4. Lỗi tìm thấy

**Win32PresentationSink mất thứ tự FIFO** (đã sửa, PR #476): `InvokeUi` chạy inline khi ở UI thread và `Post` khi ở thread khác; một
cập nhật UI thread tới sau một cập nhật cross-thread chưa chạy sẽ chạy TRƯỚC nó, nên ảnh/status cũ đè lên mới. ADR 0005 kỳ vọng
`CrossThreadPresentCount = 0` nên đây là lưới an toàn, nhưng lưới an toàn phải giữ thứ tự. Sửa tối thiểu: đếm việc đã Post chưa chạy; còn
việc chờ thì mọi cập nhật đều Post.

Quan sát (không sửa, không phải stale overwrite): `NavigationStatWorker` là MỘT thread FIFO và stat làm mới sau present không huỷ được;
một stat treo (chia sẻ mạng) của ảnh cũ làm stat đầu của ảnh mới phải đợi. Và khi cả hai khe viewer bị decode cũ giữ thì decode của
ảnh hiện tại phải đợi một trong hai xong. Cả hai là đánh đổi hiệu năng đã chọn (xem `perf(preload)`), ghi lại để biết khi đọc số đo.

## 5. Đột biến (hai bước, docs/MUTATION-TESTING.md)

30 đột biến chọn lọc (sửa MỘT chỗ, build, chạy test phủ chỗ đó, hoàn nguyên) trong `ImagePresenter`, `ZoomDetailLoader`,
`PreviewImageService`, `PresentationComposer`, `GpuImageCache`, `PresentationPrefetcher`; script chạy trong worktree riêng.

- Bước 1 (test phủ vùng đó: App.Tests lớp `ImagePresenterTests*`/`StalePreview*`/`ZoomDetail*`/`CompareViewModel*`, Imaging.Tests
  `PreviewImageService*`, Shell.Tests `Viewing`+`GpuImageCache`): **18 bị giết**, 9 sống, 3 không biên dịch (bộ lọc hằng `when(true/false)`
  bị CS7095/CS8359, `++` bỏ thì CS0649) rồi viết lại thành dạng hợp lệ.
- Bước 2 (9 sống + 3 viết lại, chạy TOÀN BỘ App.Tests/Imaging.Tests/Shell.Tests + ShellViewingTests/ShellStalePreviewTests trên desktop
  ẩn): giết thêm **6**; 6 còn sống, đều tương đương (xem dưới).

| Đột biến | Kết quả | Bị giết bởi |
|---|---|---|
| M01 bỏ kiểm token sau thumbnail | giết (bước 1) | `Burst_TwoFiles_EveryThumbnailAndPreviewOrder`, `LateThumbnailOfAnOlderNavigation` |
| M02 bỏ kiểm token sau preview | giết (1) | `JoinedPreloadDecodes_...`, `ClearCacheDuringDecodes_...`, các chuỗi vét cạn |
| M03 bỏ kiểm token sau stat đầu | giết (1) | `JoinedPreloadDecodes_...` |
| M05b lỗi chung của nav cũ vẫn xử lý (`when(... \|\| ex is not null)`) | giết (2) | `ComparePartnerFailingLate_...` |
| M07 bỏ `_zoomDetail.Reset()` đầu điều hướng | sống ở bước 1, giết (2) | `NavigationStart_ReleasesThePreviousZoomOriginalImmediately` (test mới) |
| M08 bỏ kiểm token trước stat làm mới | sống ở bước 1, giết (2) | `HeaderReadFinishingLate_OfASupersededNavigation_...` (test mới: đếm stat) |
| M09 bỏ `IsNavigationCurrent` ở catch lỗi partner compare | giết (1) | `ComparePartnerFailingLate_...` |
| M10 bỏ kiểm token trong `ZoomDetailLoader.LoadAsync` | giết (1) | `ZoomDetailLoaderGapTests.ASupersededTarget_...` |
| P01 `_cache.Set` không kiểm epoch | giết (1) | `PreviewImageServiceGapTests.ClearCache_DuringInflightDecode_...` |
| P03 bỏ `MatchesCurrentSource` sau decode | giết (1) | `PreviewImageServiceFaultTests.SourceReplacedDuringDecode_...` |
| P04b joiner không thử lại khi creator bị huỷ | giết (2) | `ViewerJoiner_CreatorCancelledBeforeSlot_...` |
| P05 `ClearCache` không bump epoch | giết (1) | `PreviewImageServicePersistStaleTests...` |
| C01 `SetIncoming` không xoá pending | giết (1) | `RandomNavigation_AlwaysEndsOnTheLastAssignedImage` |
| C02 `isFileChange` không thay ngay | giết (1) | `Composer_EverySequenceOfFiveEvents_...` (test mới) |
| C03 hạn chót cũ không kiểm token | giết (1) | `StaleDeadline_OfASupersededUpgrade_...` |
| C05 thăng cấp không chờ upload | giết (1) | `Upgrade_KeepsTheOldBitmapOnScreenUntil...` |
| C06 `LastDrawn` bỏ so id | sống ở bước 1, giết (2) | `Composer_CrossfadeRequestedBeforeTheNewImageWasDrawn_...` (test mới) |
| C07b token hạn chót tăng sau khi dùng | giết (2) | `PendingUpgrade_AfterTheDeadline_IsPromoted...` |
| C08 pending không xoá sau thăng cấp | giết (1) | `Composer_DeviceLostDuringPendingUpgrade_...` |
| G01 dải của ảnh đã giải phóng vẫn chạy | giết (1) | `Prefetch_EvictedBeforeStripesRun_StopsUploading` |
| G02 mất thiết bị không xoá cache | giết (1) | `Composer_DeviceLostDuringPendingUpgrade_...`, `GpuCache_DeviceLostMidPrefetch_...` |
| F01 `Apply` không kiểm thế hệ | giết (1) | `Prefetcher_LateLookupResultOfAnOlderNavigation_...` (test mới) |
| F02 lượt thử lại không kiểm thế hệ | giết (1) | `RetryPassesOfAnOlderNavigation_AreDropped` |
| F03 thế hệ không tăng | giết (1) | `RetryPassesOfAnOlderNavigation_...` |

Còn sống sau bước 2 (6) - đều TƯƠNG ĐƯƠNG, vì có lớp phòng thủ thứ hai cùng chỗ:

| Đột biến | Vì sao tương đương |
|---|---|
| M04 catch FileNotFound bỏ kiểm token | bên trong vẫn `if (!IsNavigationCurrent(token)) return` sau stat lại, trước khi chạm catalog |
| M06 `if (!loaded) return` (bỏ kiểm token) | `CompareViewModel.LoadAsync` chỉ trả true khi token còn hiện hành ở lần kiểm cuối, không có await giữa hai chỗ |
| M11 bỏ kiểm token trong `LoadAsync` (còn `ReferenceEquals`) | `Update()` kiểm lại token trước khi hiện; và `Reset()` đã đặt `_target = null` |
| P02 bỏ `ThrowIfCancellationRequested` sau khi lấy khe | `ReadAsync(token)` đã thấy token trước; chỉ còn cửa sổ nano-giây giữa hai dòng, không tạo lại được tất định |
| C04 bỏ so `_pending.Id` ở `OnUploadCompleted` | `TryPromotePending` kiểm lại upload của chính `_pending` |
| G03 `IsFullyUploaded` bỏ `!IsReleased` | ảnh đã giải phóng luôn bị gỡ khỏi `_entries` trước khi đánh dấu |

## 6. Kiểm chứng

- Build Release `PhotoReview.slnx`: 0 cảnh báo.
- `Category!=Manual&Category!=Native` (CI filter, UI chạy trên desktop ẩn): App.Tests, Imaging.Tests, Shell.Tests, Shell.Integration.Tests,
  Architecture.Tests xanh (số liệu ở mô tả PR).
- Bộ mới lặp 200 lần, mỗi lần một tiến trình `dotnet test --no-build` (2 luồng nền): 0 lỗi; ba bộ vét cạn (`Burst_*`) lặp mỗi
  10 lần. Không `Thread.Sleep`/`Task.Delay`/`ManagedThreadId` (luật TEST-FLAKY, TEST-THREADID).
- Chi phí: lớp `StalePreviewOverwriteTests` ~25 s trên máy rảnh (phần lớn là 3 bộ vét cạn); `PresentationStaleOrderTests` < 2 s;
  `ShellStalePreviewTests` ~4 s (desktop ẩn).
