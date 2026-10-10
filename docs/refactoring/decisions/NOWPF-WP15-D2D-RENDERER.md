---
id: NOWPF-WP15
order: 412
summary: |-
  WP-15 (đợt 1 NO-WPF, 2026-10-10): renderer Direct2D (`Shell.Rendering`: DeviceResources, D2DRenderSurface, D2DDrawContext, GpuImageCache, TiledGpuImage) cho C-09/C-10. 96 test Rendering xanh (WARP + GPU thật); 1:1 giống từng byte, PSNR 47-56 dB so với tham chiếu CPU, tile 30000x2000 không đường nối; đột biến 32 ca: 29 bị bắt, 3 sống sót là tương đương (stride cố định = rộng x 4, nội suy khi đã snap 1:1). RỦI RO: HQC thu nhỏ ảnh 24 MP mỗi khung 27-45 ms trên GPU (zoom < 100 % trên ảnh gốc ở WP-21). Lệch thẻ: không có `--demo`, tham chiếu CPU là area/bilinear (không WIC Fant), opacity nhân vào từng lệnh (không layer), AllowTearing = sync 0, tile kẹp 16384. Đề xuất v1.1: C-09 `static abstract` -> `IRenderSurfaceFactory`.
---

# NOWPF-WP15-D2D-RENDERER - renderer Direct2D + GpuImageCache + tile (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md), thẻ WP-15 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); interop dùng lại
[NOWPF-WP13B-INTEROP-GRAPHICS](NOWPF-WP13B-INTEROP-GRAPHICS.md) (quy ước ABI COM). Hợp đồng v1 không đổi (ContractSurfaceTests xanh,
không sửa file duyệt). Số đo chi tiết: [perf/2026-10-10-wp15-d2d-renderer.md](../perf/2026-10-10-wp15-d2d-renderer.md).

## 1. Đã làm (`src/PhotoReview.Shell.Rendering`)

| File | Nội dung |
|---|---|
| `DeviceResources.cs` | D3D11 device BGRA (GPU thật, lùi về WARP), D2D device/context, swap chain flip-model `FLIP_DISCARD` 2 buffer waitable, `SetMaximumFrameLatency(MaxFrameLatency)` (mặc định 1), `Generation` tăng mỗi lần tạo lại, seam lỗi `EndDrawFault`/`PresentFault` (test mất thiết bị), `LiveBitmapCount` (đếm rò) |
| `D2DRenderSurface.cs` | C-09 `IRenderSurface`: `CreateForWindow`, `CreateOffscreen` (WARP), `Resize`, `BeginDraw`/`EndDrawAndPresent` (trả `Presented/Occluded/DeviceRecreated/Failed`), `DeviceRecreated`, `WaitForNextFrame`, `WaitForGpu` |
| `D2DDrawContext.cs` | `IDrawContext`: ảnh (tile + clip core), `DrawImageOriented` (8 orientation EXIF), hình, chữ DirectWrite, clip, opacity, `PushTransform`, `TryReadPixels` (offscreen) |
| `GpuImageCache.cs` + `TiledGpuImage.cs` | C-10 `IGpuImageCache`: LRU theo byte, `Prefetch` từng dải 2 MB ở `UiPriority.Background`, `Retain`, ảnh hiện tại không bị evict, mất thiết bị xoá cache |
| `RenderGeometry.cs` | thuần (test không cần GPU): `ImageTransforms` (ma trận orientation), `TileLayout` (chia tile + gutter 64), `DrawGeometry` (snap 1:1, giao nguồn), `ImageInterpolations` (ScalingQuality -> C-09) |
| `Shell.Interop/Graphics/RenderingAdditions.cs` | CHỈ THÊM: `FillRoundedRectangle` slot 19 qua vtable thô, `CloseHandle`/`WaitForSingleObjectEx` |

## 2. Kết quả kiểm chứng

- **Test:** `Shell.Tests` lọc `FullyQualifiedName~Rendering` = 96 xanh (42 không Native + phần `Category=Native` chạy WARP/GPU thật), Release 0 warning.
  Architecture.Tests (ContractSurface, ShellRules, Encoding, TestQuality) xanh.
- **Chính xác:** 1:1 giống từng byte (kể cả offset lẻ, DPI 150 %, Pbgra32 và Bgr32, 8 orientation so với `PixelOps.ApplyOrientation`);
  thu nhỏ/phóng to PSNR 47-56 dB so với tham chiếu CPU (ngưỡng thẻ >= 40 dB); ảnh 30000x2000 chia tile ở 16384, đường nối chính xác ở 1:1
  và PSNR cao ở Fit; 1000 lần upload/evict không rò bitmap (`LiveBitmapCount` về 0) và `TotalGpuBytes` không vượt ngân sách (trừ ảnh hiện tại).
- **Đột biến (32 ca chọn lọc, `Shell.Tests` mục tiêu Rendering gồm Native):** ma trận orientation (M01-M03), ánh xạ nội suy (M04-M05),
  tile biên/đường nối (M06-M08), ngân sách `>` / `>=` (M09), Touch LRU (M10), thứ tự evict (M11), Retain (M12, M14), ảnh hiện tại (M13),
  xoá cache khi mất thiết bị (M15), cộng/trừ byte (M16, M28), thế hệ thiết bị (M17), MaxFrameLatency (M18), clip tile (M19, M31),
  alpha premultiplied/Ignore (M20), stride upload (M21, M22), opacity (M23, M30), snap NearestNeighbor (M24), thứ tự ma trận lồng (M25),
  giao nguồn (M26), kích thước dải prefetch (M27), `BudgetBytes` setter (M29), dải prefetch dở (M32).
  Lần đầu 26/32 bị bắt; 6 sống sót. Đã viết test lấp M17 (`TiledGpuImage_IsDrawable_...`), M25 (`PushTransform_Nested_...`),
  M30 (`PushOpacity_Nested_...`) - chạy lại cả ba đều bị bắt. Còn 3 ca TƯƠNG ĐƯƠNG, không thể bắt bằng hành vi:
  M21/M22 (`PixelBuffer.Stride == Width * 4` luôn đúng; đã ghim bằng `PixelBuffer_HasNoRowPadding_...` để đỏ khi PixelBuffer có đệm hàng),
  M24 (đã snap 1:1 vào lưới pixel thì Linear/HQC cho kết quả y hệt NearestNeighbor).

## 3. Hiệu năng (máy dev, i7-9750H, tải CPU nền 60-100 % do agent khác: số có nhiễu, xem file perf)

| Phép đo (GPU thật, ảnh 24 MP 6000x4000, viewport 1920x1080) | P50 | P95 |
|---|---:|---:|
| upload 24 MP (GetOrUpload + GPU xong) | 35 ms | 49 ms |
| upload 1920x1080 | 4,2 ms | 6,1 ms |
| prefetch một dải 2 MB (UI thread bị chiếm) | 0,43 ms | 6,2 ms |
| pan 100 % HQC, CPU submit (thẻ: <= 3 ms) | 0,26 ms | 0,79 ms (lần trước tới 1,74) |
| pan 200 % HQC, CPU submit | 0,51 ms | 1,7 ms |
| **Fit 24 MP -> 1620x1080 HQC, GPU xong** | **26,8 ms** | **41 ms** |
| Fit 24 MP Linear, GPU xong | 6,3 ms | 7,2 ms |

**RỦI RO:** `HighQualityCubic` khi thu nhỏ ảnh 24 MP tốn 27-45 ms GPU mỗi khung (đã gửi xong CPU < 8 ms nhưng GPU chiếm). Ở chế độ Fit
app vẽ preview cỡ viewport nên không gặp; gặp khi zoom < 100 % trên ảnh original (WP-21). Giảm thiểu: dùng `Linear`/mipmap ảnh tạm khi
đang zoom động, HQC chỉ cho khung dừng; hoặc tiền thu nhỏ trên CPU (đã có `ResizeArea`). WARP chậm hơn nhiều (Fit HQC P50 321 ms) -
chỉ dùng cho test/RDP.

## 4. Lệch so với thẻ WP-15

1. **Không có cửa sổ demo `--demo <file>`:** `Shell.Win32` chưa có điểm vào (WP-20/WP-21); thay bằng `D2DWindowSurfaceTests` (HWND thật,
   flip-model, Resize, Present, mất thiết bị, occluded) và `RendererPerfTests`.
2. **Tham chiếu CPU là area (thu nhỏ) và bilinear (phóng to) tự viết** (`RenderTestImages.ResizeArea/ResizeBilinear`), không phải WIC Fant:
   tham chiếu viết tay độc lập với WIC/WPF để test không phụ thuộc codec; thẻ ghi "chỉ để bắt lỗi thô" nên giữ ngưỡng 40 dB, đo được 47-56 dB.
3. **Opacity nhân vào từng lệnh vẽ** (brush/bitmap), không dùng layer: khác layer chỉ khi các hình trong nhóm chồng nhau (overlay của app
   không chồng nhau); tránh cấp phát layer mỗi khung.
4. **`AllowTearing` = `Present(0)` (sync 0)** mà chưa dùng cờ `DXGI_PRESENT_ALLOW_TEARING` (cần `IDXGIFactory5::CheckFeatureSupport` +
   swap chain tạo với cờ): v1 chấp nhận; khung vẫn bị giới hạn bởi waitable object.
5. **Tile kẹp ở 16384** dù WARP báo `GetMaximumBitmapSize` = 8388608: mọi thiết bị chia tile như GPU thật (test WARP kiểm đúng đường tile).
6. Thêm ngoài C-09: `PushTransform`/`PopTransform`, `DrawImageOriented`, `TryReadPixels`, `WaitForGpu`, `D2DRenderSurface.CreateWindowSurface/
   CreateOffscreenSurface` (kiểu cụ thể cho cache) - không đổi chữ ký hợp đồng.

## 5. Slot COM đã dùng: đã/chưa có test thật

Đã có test chạy thật (WARP/GPU): `ID3D11Device` CreateTexture2D[5]/GetImmediateContext[40]; `IDXGIFactory2` CreateSwapChainForHwnd[15];
`IDXGISwapChain` Present[8], GetBuffer[9], ResizeBuffers[13]; `IDXGISwapChain2` SetMaximumFrameLatency[31], GetFrameLatencyWaitableObject[33]
(thêm đột biến M18); `ID2D1DeviceContext` CreateBitmapEx[57], SetTarget[74], DrawBitmapEx[85], SetDpi; `ID2D1RenderTarget` FillRectangle[17],
DrawRectangle[16], FillRoundedRectangle[19, vtable thô, `FillRoundedRectangle_*`], DrawTextLayout[28], SetTransform[30],
PushAxisAlignedClip[45]/PopAxisAlignedClip, Clear[47], BeginDraw[48], EndDraw[49]; `ID2D1Bitmap1` CopyFromMemory, CopyFromBitmap, Map/Unmap.
CHƯA có test riêng: `IDXGISwapChain1::Present1` (v1 dùng `Present`), `IDXGIFactory5` (tearing, mục 4.4), `IDXGISurface` đường
`CreateBitmapFromDxgiSurface[62]` ngoài đường offscreen/swap chain đã chạy, `D3D11 ClearStateAndFlush` chỉ chạy gián tiếp qua `Resize`
của swap chain (không có assert riêng), mọi slot DWrite ngoài CreateTextLayout/DrawTextLayout thuộc WP-17.

## 6. Đề xuất hợp đồng v1.1

- **C-09 `static abstract` -> `IRenderSurfaceFactory`:** `IRenderSurface` khai báo `static abstract` factory (`CreateForWindow`,
  `CreateOffscreen`) cản fake (không implement được qua interface thường), không dùng được làm type argument. Thay bằng
  `IRenderSurfaceFactory { IRenderSurface CreateForWindow(...); IRenderSurface CreateOffscreen(...); }` tiêm qua DI; `D2DRenderSurface`
  giữ hai hàm `static` hiện có làm triển khai của một lớp factory mỏng. Nhóm thay đổi cùng v1.1 C-07 (lead).
- Cân nhắc thêm `IDrawContext.PushTransform` vào C-09 (hiện chỉ có trên `D2DDrawContext`; WP-16/21 cần ma trận pan/zoom).

## 7. Việc mở / cho gói sau

- WP-17 (chữ): `INativeTextLayout` đã định nghĩa trong `Shell.Rendering` - WP-17 implement.
- WP-21: chọn nội suy khi thu nhỏ động (mục 3 rủi ro); gọi `GpuImageCache.Retain` với cửa sổ prefetch của app.
- Đo lại hiệu năng khi máy thật rảnh (lần này tải nền 60-100 %).
