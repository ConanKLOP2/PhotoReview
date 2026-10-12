---
id: NOWPF-WP21
order: 810
summary: |-
  WP-21 (đợt 3 NO-WPF, 2026-10-12): trình bày ảnh cho shell Win32. `Shell.Win32/Viewing`: `Win32PresentationSink` (C-16, thay sink tối thiểu của WP-20; không chặn trên Task), `PresentationComposer` (ảnh chính + lớp ảnh cũ crossfade Q-R37 + compare theo thứ tự XAML; nâng cấp preview -> full upload nền rồi mới thay, không nhảy layout; hạ Linear khi thu nhỏ nặng lúc chuyển động), `CrossfadeLayer` (Animator WP-17, đồng hồ khung tiêm được, QuadraticOut 1 -> 0, điều hướng mới huỷ tween cũ), `CompareLayout/CompareLayer` (ComparePanel của XAML, viền LimeGreen/#555), `PresentationPrefetcher` (GpuImageCache.Prefetch + Retain theo hướng duyệt), `FramePacer`, `FrameStats`. PHÁT HIỆN: `Present(1)` chặn UI thread P50 222 ms ở MỌI khung (không chỉ khung đầu) trên máy dev; cửa sổ thường nay luôn `Present(0)` (~1,2 ms), pan 60 Hz P95 5 ms. CÒN LẠI: khung đơn lẻ sau khoảng nghỉ 100-450 ms vẫn có thể bị `EndDraw` chặn ~0,5 s (nguyên nhân gốc chưa rõ, xem mục 4, CHƯA XÁC NHẬN).
---

# NOWPF-WP21 - trình bày ảnh, compare, crossfade (2026-10-12)

Thẻ: WP-21 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); hợp đồng C-02/C-09/C-10/C-11 trong [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md).
Tiền đề đã merge: WP-15 ([renderer](NOWPF-WP15-D2D-RENDERER.md)), WP-16 ([engine viewport](NOWPF-WP16-VIEWPORT-ENGINE.md)), WP-17
([chữ + animator](NOWPF-WP17-TEXT-OVERLAY.md)), WP-20 ([startup shell](NOWPF-WP20-STARTUP-SHELL.md)). App WPF (`PhotoReview.App`) **không đổi**; không đổi
liên kết `.jpg`. Số đo: [perf/2026-10-12-wp21-presentation.md](../perf/2026-10-12-wp21-presentation.md).

## 1. Đã làm

| File | Nội dung |
|---|---|
| `Shell.Win32/Viewing/Win32PresentationSink.cs` | `IPresentationSink`/factory của shell (C-16). Callback view model trước, hub (bề mặt) sau; thread khác lọt vào thì `Post` (không chặn, FIFO) + `ReviewMetrics.CrossThreadPresentCount` (ADR 0005). Thay `ShellPresentationSink(Factory)` của WP-20 (đã xoá), đăng ký bằng MỘT dòng `services.AddShellViewing()` ở cuối `ShellCompositionRoot.RegisterFeatures`. |
| `Viewing/PresentationComposer.cs` | Trạng thái + thứ tự vẽ: ảnh chính -> lớp ảnh cũ -> compare. `SetIncoming` (thay ngay khi đổi file/xoá/ảnh nhỏ < 16 MB GPU; bản nâng cấp cùng ảnh lớn thì `GpuImageCache.Prefetch` rồi chờ `UploadCompleted`, hạn chót 1 s), `BeginCrossfade` (đóng băng khung đã vẽ lần cuối), `TryPromotePending`, `CollectRetainedIds`, chọn nội suy theo chuyển động. |
| `Viewing/CrossfadeLayer.cs` | Lớp `OutgoingImage`: giữ buffer + hộp đích + nội suy của khung cũ, opacity 1 -> 0 `Easing.QuadraticOut` qua `Animator` của WP-17 (đồng hồ khung tiêm được); `Start` HUỶ tween cũ rồi chạy lại từ 1 (như `BeginAnimation(SnapshotAndReplace)`); hết hiệu ứng thì nhả ảnh và ngừng nghe đồng hồ; không có hiệu ứng thì không đăng ký đồng hồ. |
| `Viewing/CompareLayer.cs` | `CompareLayout` (thuần): Panel Margin 8, hai cột bằng nhau, ô Margin 4 + viền 2, ảnh Uniform căn giữa, `HitTest`; `CompareLayer.Draw`: nền canvas phủ panel, viền LimeGreen (#32CD32) khi chọn / #555555, ảnh trong clip nội dung. |
| `Viewing/ImageLayer.cs` | `TryDraw(cache, ctx, PixelBuffer, hộp, nội suy, opacity, exifOrientation)`: buffer đã giải phóng/hộp rỗng -> false thay vì ném; orientation 2..8 cho buffer THÔ qua `DrawImageOriented` (mọi decoder của app đã áp orientation nên shell dùng 1). |
| `Viewing/ScalingPolicy.cs` | HQC -> Linear chỉ khi ĐANG chuyển động và thu nhỏ > 4 lần diện tích (rủi ro WP-15: HQC 24 MP 27-45 ms GPU/khung); vẽ lại HQC sau `SettleMs` = 120 ms đứng yên. NE-9 (a) giữ nguyên. |
| `Viewing/PresentationPrefetcher.cs` | Sau mỗi `CurrentIndex` đổi: 2 ảnh phía trước theo hướng + 1 phía sau, tra cache preview ở thread nền (stat file), `Prefetch` ở UI thread (dải 2 MB, Background), thử lại 120/400/1000 ms vì preload hàng xóm đến muộn; lượt cũ bị bỏ khi đã điều hướng tiếp. |
| `Viewing/FramePacer.cs`, `FrameStats.cs` | Chính sách nhịp khung (mục 3) + thống kê khung (vòng 2048, P50/P95/max, đếm Present chặn) để đo thật. |
| `ShellMainWindow.Viewing.cs` (+ vài dòng ở `ShellMainWindow.cs`/`.Frame.cs`) | Keo: `MainViewModel.ImageChanging` -> crossfade (đúng thời điểm WPF: trước khi ViewerState/ảnh đổi), hub -> `SetIncoming`, `Compare.PropertyChanged`, prefetch/`Retain`, `PreviewImageService.CacheCleared` -> `GpuImageCache.Clear` (INV-2), `TrySelectCompareAt` (WP-22 nối chuột). |
| `Shell.Rendering/GpuImageCache.cs` | Thêm `IsFullyUploaded(id)` và sự kiện `UploadCompleted(id)` (additive, ngoài C-10 đóng băng). |
| `Shell.Rendering/D2DRenderSurface.cs`, `DeviceResources.cs` | Chẩn đoán additive: `AdapterDescription`, `LastEndDrawTicks`, `LastPresentTicks`, `HasFrameLatencyWaitHandle`. |
| `Imaging/Caching/PreviewImageService.cs` | Thêm sự kiện `CacheCleared` (raise sau `ClearCache()`; không subscriber = không chi phí). Hành vi WPF không đổi. |

## 2. Lệch thẻ / quyết định cần biết

1. **Điểm cắm là `ShellCompositionRoot.RegisterFeatures`**, không phải một hàm của `ShellMainWindow`; phần nối cửa sổ là partial `ShellMainWindow.Viewing.cs` + 3 dòng gọi
   (`InitializeViewing` ở constructor, `AttachViewing` sau khi tạo cache, `DisposeViewing` trước khi huỷ cache) và thân `OnImageChanged`/`DrawContent` đổi thành lời gọi.
2. **Tên file**: thẻ ghi `ImageLayer/CompareLayer/CrossfadeLayer/Win32PresentationSink/ShellMainWindow.Viewing`; thêm `PresentationComposer` (tách khỏi cửa sổ để
   test trên bề mặt offscreen + đồng hồ khung giả), `PresentationPrefetcher`, `ScalingPolicy`, `FramePacer`, `FrameStats`.
3. **Orientation**: không có việc xoay riêng ở shell - decoder đã áp orientation (`PixelOps.ApplyOrientation`), `ImageLayer` chỉ hỗ trợ buffer thô (có test 2/3/6/8
   khớp từng byte với `ApplyOrientation`).
4. **Nâng cấp cùng ảnh có độ trễ**: bản full chỉ thay khi đã upload xong (hoặc quá 1 s); trong lúc chờ khung vẫn có ảnh preview ở đúng hộp. Mốc `RenderedFrame` của bản mới
   chỉ phát khi khung vẽ chính ảnh mới (`ReferenceEquals(Current, hub.CurrentImage)`), không phát ở khung còn ảnh cũ.
5. **Chọn ô compare bằng chuột/phím**: `TrySelectCompareAt(PointD)` đã có và được test qua shell thật; nối `WM_LBUTTONDOWN`/Enter/Space là việc của WP-22.
6. `ImageTransition=None` (mặc định) = chi phí 0: không tạo tween, không đăng ký đồng hồ (có test `StartCount == 0`, `SubscriberCount == 0`).

## 3. `Present` không được chặn UI thread: FramePacer

WP-20 ghi "Present(1) của khung ảnh đầu chặn 250-500 ms, WP-21 kiểm các khung sau". Đo (thật, mục 1 của file perf): **mọi** `Present(1)` ở cửa sổ thường chặn P50 222 ms
(P95 550-750 ms), gồm pan, crossfade, zoom; WARP cũng có khung 220-500 ms; chờ waitable chỉ chuyển chỗ chặn; latency 2, 3-4 buffer, FlipSequential, `timeBeginPeriod(1)`,
kích hoạt cửa sổ không đổi gì. `Present(0)` ~1,2 ms. Chính sách đã làm:

- Cửa sổ thường (và khung ảnh đầu): `Present(0)` - flip-model có cửa sổ luôn do DWM ghép trọn khung nên không xé hình.
- Phủ cả màn hình (`ViewerState.IsFullscreen`, F11 của WP-25; có thể thành independent flip, mới xé được): `Present(1)` + chờ waitable KHÔNG chặn (hoãn khung nếu
  swap chain chưa nhận, tối đa 3 khung liên tiếp rồi vẽ luôn); VSync mà vẫn chặn >= 40 ms hai khung liên tiếp thì hạ cấp vĩnh viễn (phiên) xuống `Present(0)`
  và ghi log một lần.
- Chờ waitable không chặn áp dụng cho cả hai chế độ: khung bị hoãn thì `Invalidate` lại, không bao giờ treo (cửa sổ bị che không báo sẵn sàng -> vẽ luôn sau 3 khung).

## 4. Còn lại, CHƯA XÁC NHẬN (cần chủ dự án)

> Cập nhật 2026-10-12: nguyên nhân gốc ĐÃ XÁC ĐỊNH và đã sửa bằng giữ GPU ấm - xem [NOWPF-WP21STALL](NOWPF-WP21STALL-GPU-KEEPWARM.md). Phần dưới giữ làm lịch sử
> (giả thuyết DWM/swap chain ở đây SAI: chặn ở lần nộp việc đầu cho GPU Intel sau khi nó rỗi; WARP không bị).

Sau `Present(0)`, nhịp khung dày đều (pan/crossfade 60 Hz) P95 ~5 ms, nhưng khung ĐƠN LẺ sau khoảng nghỉ 100-450 ms (một lần bấm phím điều hướng/zoom) bị
`ID2D1DeviceContext::EndDraw` chặn ~0,44-0,5 s ở ~25-40 % số lần trên máy dev (cả `Present(0)`, cả WARP có biểu hiện tương tự ở Present); không tái hiện ở nhịp 16 ms.
Nguyên nhân gốc chưa xác định, không do adapter (Intel UHD 630 là adapter của màn hình), buffer, swap effect, latency, timer. Có thể là hành vi quản lý nguồn
của DWM/iGPU trên build Windows 26300 và cũng ảnh hưởng bản WPF nhưng bị che vì WPF vẽ ở luồng render riêng.

| Phương án | Ưu | Nhược |
|---|---|---|
| (a) Giữ nguyên (khuyến nghị bây giờ) | Không đổi kiến trúc; khung đều mượt; UI vẫn nhận phím giữa các khung | Một số lần bấm phím trễ ~0,5 s trên máy này; các agent/người dùng khác có thể không gặp |
| (b) Luồng render riêng cho D2D/D3D (UI thread chỉ đăng lệnh vẽ) | UI thread không bao giờ chờ GPU/DWM; input luôn mượt | Phức tạp: cache GPU, composer, mất thiết bị, đo layout đều phải đổi chủ sở hữu thread (đụng WP-15/16/17); ~2 ngày + rủi ro |
| (c) Present qua DirectComposition (`CreateSwapChainForComposition`) | Có thể tránh ràng buộc "cửa sổ + DWM đợi buffer" | Chưa chắc hết chặn (EndDraw vẫn chờ buffer); đổi `DeviceResources`, WP-15 |
| (d) Giữ ấm: phát khung nền nhẹ 30-60 Hz trong ~0,5 s sau mỗi cập nhật | Rất ít mã | Tốn điện/CPU; không chứng minh được hiệu quả (nghi nghỉ > 0,5 s vẫn bị) |

Đề xuất: (a) tới khi người dùng chạy `PresentationPerfReportTests` trên máy thật của mình và thấy khung đơn lẻ vẫn bị chặn; khi đó (b). Đã ghi mục WP21-STALL ở
[NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md).

## 5. Kiểm chứng

- Build `PhotoReview.slnx` Release: 0 cảnh báo / 0 lỗi.
- `Shell.Tests` (Viewing): `CrossfadeLayerTests` (opacity 1 / 0,5625 / 0,25 / 0,0625 theo QuadraticOut, nhả ảnh + ngừng nghe đồng hồ, điều hướng mới huỷ tween cũ, ảnh bị
  giải phóng giữa chừng), `PresentationComposerTests` (thứ tự lớp, hộp đích không nhảy khi nâng cấp, hoãn/promote/hạn chót/INV-1 ngẫu nhiên 6 hạt giống, Retain, bộ lọc theo
  chuyển động + DPI), `CompareLayoutTests` (số tính tay từ XAML, hit-test), `ScalingPolicyTests`, `PresentationPrefetcherTests`, `Win32PresentationSinkTests`,
  `FramePacerTests`, `FrameStatsTests`; `PresentationRenderingTests` (`Category=Native`, WARP đọc lại pixel: 1:1 từng byte, clip viewport, blend opacity 1/0,25/0,0625,
  compare viền/ô/lề, che ảnh chính, orientation 2/3/6/8, ảnh 17000x1200 chia tile không đường nối, mất thiết bị giữa lúc mờ).
- `Shell.Integration.Tests` (`ShellStartup` thật, desktop ẩn, WARP): `ShellViewingTests` - điều hướng từng bước, điều hướng dồn dập 100 lần (INV-1: ảnh cuối = ảnh của chỉ số
  cuối), crossfade bật/tự kết thúc, compare mở thẳng + chọn ô theo toạ độ + thoát, mất thiết bị giữa lúc mờ, xoá cache xoá cả cache GPU, ảnh đầu.
- Kiểm toàn bộ: Architecture 105, Shell 595, Shell.Integration 63, Core 3154, Imaging 4178 (+1 flaky WIC đã biết, chạy lại xanh), App 2403, Integration 1142 (+1 flaky
  `AppForwardedOpenTests.A3` timeout 30 s dưới tải, chạy lại xanh), WpfBridge 21.

## 6. Đột biến

40 đột biến thủ công (2 agent sonnet, worktree riêng, mỗi cái: sửa một chỗ -> build -> chạy Shell.Tests Viewing/GpuImageCache + ShellViewingTests -> trả lại):
- Nhóm A (crossfade/composer/compare/chính sách nội suy/cache, 20 + 1 biến thể): hủy tween cũ, opacity đầu, easing, nhả ảnh đã giải phóng, clip viewport, thứ tự lớp,
  hoãn nâng cấp khi đổi file, bỏ `_pending`, hạn chót, nhận diện chuyển động, đóng băng khung đã vẽ, lề/viền/màu chọn/hit-test compare, canvas panel, ngưỡng thu nhỏ,
  đồng hồ settle, `IsFullyUploaded`, `UploadCompleted`. 17 bị bắt ngay; 2 sống sót đã lấp (`OutgoingBufferDisposedMidFade_IsReleasedBeforeTheContextIsTouched`,
  `SettleClock_AsksForTheHighQualityFrame_ExactlyWhenSettleMsHaveElapsed`); thêm test cache `Prefetch_RaisesUploadCompletedOnce_...`; 1 tương đương (điều kiện
  `!IsReleased` phòng thủ: mọi đường giải phóng đều xoá entry cùng lệnh).
- Nhóm B (prefetch/sink/pacer/cửa sổ, 20): hướng + thử lại + prefetch trùng, thứ tự sink, đếm cross-thread, trace, VSync/hoãn/hạ cấp, hiệu ứng khi nâng cấp/None,
  xoá cache GPU, mốc RenderedFrame, `TryPromotePending` ở Present, `Retain`, layout theo ảnh mới, chọn ô compare, prefetch khi điều hướng. 11 bị bắt ngay; 9 sống sót
  đã lấp (`RetryPassesOfAnOlderNavigation_DoNotEvenStartABackgroundLookup`, `Win32Sink_TracePresented_ReachesTheHub...`,
  `ImageChanging_ThatIsNotAFileChange_NeverStartsACrossfade`, `ClearCache_DropsEveryUnretainedGpuBitmap_NotOnlyTheCounter`,
  `FrameDrawnWhileAnUpgradeIsStillUploading_DoesNotCountAsTheNewImagePresented`, `AnUpgradeThatIsFullyUploadedWithoutTheCompletionEvent_IsPromotedByTheNextFrame`,
  `CurrentImage_IsRetainedOnTheGpu_WhenTheCacheIsOverBudget`, `Navigating_BackAndForth_FeedsTheDirectionToThePrefetcher`). Mỗi test mới đỏ khi áp đột biến, xanh ở bản gốc.
- Không phát hiện lỗi thật trong mã nguồn.

## 7. Việc còn lại / bàn giao

- WP-22: nối chuột vào `ShellMainWindow.TrySelectCompareAt`; phím mũi tên pan dùng `ArrowPanRule`.
- WP-23: overlay vẽ sau `DrawContent(context)` trong `Present()` (cùng khung); `FrameStats` có thể hiển thị trong HUD chẩn đoán.
- WP-25: F11 bật `FramePacer` VSync (đọc `ViewerState.IsFullscreen`), `WM_DPICHANGED` đa màn hình đổi `DpiScale` của `PresentationInputs`.
- WP-27 (probe): `PresentationPerfReportTests`/`PresentProbeReportTests` là mẫu để thêm vào probe 2 vòng smoke.
