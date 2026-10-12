---
id: NOWPF-STALEPREVIEW
order: 1020
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

MUTATION_TABLE

## 6. Kiểm chứng

VERIFICATION
