---
id: NOWPF-WP21STALL
order: 940
summary: |-
  WP21-STALL (2026-10-12): nguyên nhân gốc khung đơn lẻ bị `EndDraw` chặn ~0,47 s trên Intel UHD 630 = GPU rỗi > ~25 ms thì LẦN NỘP VIỆC ĐẦU bị chặn (25-50 % số lần, càng nghỉ lâu càng hay); swap chain/buffer/latency/Flush/threading D2D-D3D không liên quan, WARP không bị. Sửa: `GpuKeepAlive` (luồng nền, thiết bị D3D/D2D offscreen riêng, lệnh tí hon + Flush mỗi 20 ms) trong 5 s sau mỗi tương tác của cửa sổ ĐANG ACTIVE; tắt `PHOTOREVIEW_GPU_KEEPWARM=0`. key->frame P95 215-229 -> 8,8 ms, khung >= 100 ms 26,5 -> 0 %, +0,6 % CPU một lõi, ~4 % engine GPU khi đang tương tác. Phím đầu sau khoảng nghỉ > 5 s vẫn có thể bị chặn. CHƯA XÁC NHẬN: bật mặc định, cửa sổ 5 s, pin.
---

# NOWPF-WP21STALL - giữ GPU ấm để khung đơn lẻ không bị chặn (2026-10-12)

Thẻ: WP21-STALL ([NOWPF-WP21 mục 4](NOWPF-WP21-PRESENTATION.md), gate đợt 3 WP-29). App WPF (`PhotoReview.App`) KHÔNG đổi; không đổi liên kết `.jpg`.
Số đo + bảng từng thí nghiệm: [perf/2026-10-12-wp21-stall.md](../perf/2026-10-12-wp21-stall.md).

## 1. Nguyên nhân gốc

Trên máy dev (Intel UHD 630 điều khiển màn hình, Windows 11 build 26300) lần NỘP VIỆC ĐẦU TIÊN cho GPU sau khi nó rỗi bị chặn ~0,45-0,52 s (thời gian gần hằng):

- Xảy ra ở `ID2D1DeviceContext::EndDraw` (nộp lệnh cho GPU), không ở `Present`; nếu vẽ trên adapter khác (NVIDIA hiện qua Intel) thì chặn dời sang `Present`.
- Tần suất tăng theo khoảng nghỉ: 4 % (33 ms), 9 % (66), 16 % (100-150), 26 % (250), 40 % (450), 37-51 % (>= 800 ms). GPU "rỗi" chỉ sau ~25 ms.
- KHÔNG liên quan: FLIP_SEQUENTIAL/DISCARD, 2/4 buffer, MaximumFrameLatency 1/2/3, `DO_NOT_WAIT`, `Flush` trước/sau Present, D2D multithreaded, D3D single-threaded,
  `timeBeginPeriod`. WARP (cùng swap chain + DWM, không GPU) KHÔNG bị chặn.
- Nên "khung giữ ấm" (a) đúng hướng, nhưng phải nộp việc THẬT (cần `Flush`) và cách nhau <= 25 ms; cách 33 ms trở lên vẫn bị.

## 2. Đã làm

| File | Nội dung |
|---|---|
| `Shell.Rendering/GpuKeepAlive.cs` | Luồng nền (BelowNormal, mở lười ở `Activate` đầu): thiết bị D3D11/D2D offscreen 16x16 RIÊNG (`DeviceResources.CreateOffscreen`, GPU thật), mỗi `intervalMs` (waitable timer độ phân giải cao) `TouchGpu` = `BeginDraw/Clear/EndDraw` + `ID3D11DeviceContext::Flush`, tới hạn chót đã `Activate` (không bao giờ rút ngắn). Không GPU thật (WARP)/lỗi thiết bị = tự tắt, không ném; ngoài khoảng hiệu lực ngủ trên event. Seam `IGpuKeepAliveWork` cho test. |
| `Shell.Rendering/DeviceResources.cs`, `Shell.Interop/Graphics/D3D11.cs`, `RenderingAdditions.cs` | `DeviceResources.TouchGpu`, `D3D11.Flush` (vtable thô), `CreateWaitableTimerEx/SetWaitableTimer`. Thiết bị của cửa sổ KHÔNG đổi. |
| `Shell.Win32/Viewing/KeepWarm.cs` | `KeepWarmOptions` (env `PHOTOREVIEW_GPU_KEEPWARM` 0/off/false/no tắt; `_MS` 250-60000 mặc định 5000; `_INTERVAL_MS` 5-100 mặc định 20); `KeepWarmController`: handler đứng đầu chuỗi thông điệp, CHỈ NGHE (luôn trả false), phím/chuột/wheel/cảm ứng/kích hoạt khi cửa sổ ĐANG ACTIVE kéo dài hạn chót, gộp 100 ms. |
| `Shell.Win32/Viewing/ShellMainWindow.KeepWarm.cs`, `ShellMainWindow.cs` | Nối: `RegisterKeepWarm` (1 dòng ở `RegisterFeatures`), dọn ở `ReleaseRenderer`; WARP (không có seam giả) = không tạo gì. `ShellMainWindowOptions/ShellStartupOptions` thêm `KeepWarm`, `KeepWarmWork` (test). |
| `Shell.Win32/Viewing/FrameLog.cs` | `PHOTOREVIEW_FRAME_LOG=<tệp>`: một dòng mỗi khung (gap/frame/endDraw/present); mặc định tắt (công cụ đo). |
| `tools/diag/stall-experiment.ps1`, `stall-cost.ps1`, `stall-experiment-knobs.patch` | Ma trận thí nghiệm xen kẽ, đo chi phí, các công tắc giả thuyết (áp lên `36b55eef`). |

## 3. Kết quả (xen kẽ, cùng lô; chi tiết perf note)

| | trước | sau |
|---|---:|---:|
| key->frame P50 / P95 (gap 250 ms) | 6,7-6,9 / 215-229 ms | 6,0 / 8,8 ms |
| khung >= 100 ms | 26,5 % | 0 % (0/233) |
| phím không có khung riêng | 47-54 / 186-193 | 1 / 239 |
| CPU tiến trình khi tương tác | 4,8-5,1 % một lõi | 5,4-5,6 % (+0,6) |
| GPU engine util (pid) | 0,2 % | 4,1-4,3 % |
| CPU khi không tương tác (10 s rỗi) | 0 | 0 |

Mục tiêu: key->frame P95 <= 20 ms ĐẠT (8,8); khung >= 100 ms < 1 % ĐẠT (0); thẻ gốc <= 3 ms P95 CHƯA đạt (8,8 ms; P50 6,0 ms là nền của đường key->frame ở shell).

## 4. CHƯA XÁC NHẬN (cần chủ dự án)

Đã làm theo khuyến nghị (bật mặc định, 5 s, 20 ms). Các điểm còn mở:

| Điểm | Phương án | Ưu | Nhược |
|---|---|---|---|
| Mặc định bật hay tắt | (a) BẬT (đang làm; khuyến nghị) | Gate đợt 3 đạt; người dùng thấy ngay | ~4 % engine GPU + 0,6 % một lõi trong 5 s sau mỗi tương tác; giữ iGPU khỏi vào trạng thái rỗi (pin) |
| | (b) TẮT mặc định, bật bằng env/Settings | Không đổi tiêu thụ điện | Gate đợt 3 vẫn hụt; hầu hết người dùng không biết bật |
| Cửa sổ giữ ấm | (a) 5 s (đang làm) | Phủ nhịp duyệt thường (<= 3 s/ảnh), tắt nhanh | Phím sau khoảng nghỉ > 5 s vẫn bị chặn ~0,47 s ở 25-50 % số lần |
| | (b) giữ ấm LIÊN TỤC khi cửa sổ active | Không còn khung bị chặn khi đang dùng | Tiêu thụ nền liên tục khi cửa sổ mở (pin, nhiệt) |
| | (c) 5 s + nâng khi chuột di chuyển trong cửa sổ | Phủ người dùng chuột trước khi bấm phím | Đã có (chuột trong danh sách tương tác); không thêm gì |
| Khi dùng pin | (a) không đổi (đang làm) | Đơn giản | Chưa đo pin |
| | (b) tắt giữ ấm khi `SystemPowerStatus` pin/tiết kiệm | Tiết kiệm | Quay lại ~0,47 s chặn trên pin |

Phương án đã loại: (b-cũ) luồng render riêng, (c-cũ) DirectComposition (không đụng nguyên nhân: chặn là ở nộp việc cho GPU, WARP cùng DWM không bị); mở lại
chỉ khi người dùng thấy chặn dù đã giữ ấm. Hoàn tác: đặt `PHOTOREVIEW_GPU_KEEPWARM=0` hoặc bỏ dòng `RegisterKeepWarm()`.

## 5. Kiểm chứng

- `Shell.Tests`: `GpuKeepAliveTests` (không luồng tới `Activate` đầu, chạy/dừng ở hạn chót, mở lại cùng thiết bị, hạn chót không bị rút ngắn, khoảng nghỉ không phải vòng bận,
  không GPU/lỗi mở/lỗi chạm = tự tắt, `Dispose` chờ luồng trả thiết bị, idempotent), `KeepWarmTests` (env, kẹp giá trị, phân loại thông điệp, active/throttle/hạn chót,
  luôn trả false), `FrameLogTests`, `DeviceKeepAliveNativeTests` (`Category=Native`, WARP: 200 lần chạm không rò bitmap).
- `Shell.Integration.Tests`: `ShellKeepWarmTests` (shell thật, desktop ẩn, việc giả): tương tác của cửa sổ active chạy rồi tự dừng + đóng cửa sổ trả thiết bị; không
  active/đã mất active/công tắc tắt/WARP = không có gì.
- Đột biến: xem mục dưới (cập nhật khi xong).
