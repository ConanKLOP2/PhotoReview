# WP-21: trình bày ảnh Win32 - thời gian vẽ khung và độ chặn của Present (2026-10-12)

Gói: [NOWPF-WP21](../decisions/NOWPF-WP21-PRESENTATION.md). Base = `origin/master` `a42f5b13` (WP-20). Máy dev i7-9750H, 12 luồng, Intel UHD 630
điều khiển màn hình chính 1920x1080 (240 Hz), thiết bị D3D của shell chạy trên chính Intel UHD 630 (NVIDIA RTX 2080 Max-Q không dùng), Windows 11
build 26300, gói nguồn Turbo, cắm điện. Cửa sổ thật trên desktop tương tác (không phải desktop ẩn), 1200x800, không kích hoạt.

## Cách đo

- `PresentationPerfReportTests` (`Category=Manual`, `Shell.Integration.Tests`): `ShellRun` thật (`PreferWarp=false`), 10 JPEG ~12 MB (24 MP) giữa danh sách
  F4 `C:\Xiuren\[[WALLPAPER]` sao vào thư mục tạm. Mỗi khung ghi `FrameStats`: thời gian cả khung (layout + vẽ + `EndDrawAndPresent`) và riêng
  `EndDrawAndPresent` (bóc tách `EndDraw`/`Present` ở `D2DRenderSurface.LastEndDrawTicks/LastPresentTicks`). "Present >= 8 ms" = số khung bị chặn.
- Các pha: điều hướng (Next, 150 ms/ảnh), crossfade 200 ms (Previous, 450 ms/ảnh), zoom in x5 + nâng cấp full, pan 120 khung ở 60 Hz, zoom out.
- `PresentProbeReportTests` (`Category=Manual`): vòng vẽ khung trống tối thiểu trên cửa sổ thật để tách các nguyên nhân (sync interval, latency, buffer,
  nhịp thưa/dày).
- Tải nền: **77-95 % CPU toàn máy lúc đo lô cuối** (nhiều agent build/test cùng lúc); số tuyệt đối có nhiễu, so sánh trong cùng lô.

## Phát hiện chính: `Present(1)` chặn UI thread ở MỌI khung sau ảnh đầu, không chỉ khung đầu

Cùng build (chỉ đổi cách Present), cửa sổ thường, GPU thật:

| Cấu hình | Khung vẽ được trong pha | Present P50 | P95 | Max |
|---|---:|---:|---:|---:|
| `Present(1)` (WP-20, mặc định) | 8-10 / pha (mỗi khung chặn) | **222 ms** | 550-755 ms | 755 ms |
| `Present(0)` + chờ waitable không chặn (WP-21) | 67-120 / pha | 2,8-5,0 ms | 3-6 ms | 5 ms (khung đều) |

Thăm dò `PresentProbeReportTests` (40 khung trống, cách nhau 16 ms, cửa sổ 1200x800):

| Cấu hình | Present P50 | P95 | Max | Khung >= 8 ms |
|---|---:|---:|---:|---:|
| GPU sync 1, latency 1 (mặc định WP-15) | 222 ms | 332-630 ms | 742 ms | 37-38 / 40 |
| GPU sync 1, latency 2 | 222-232 ms | 692-738 ms | 1,3 s | 38-39 |
| GPU sync 1, vòng chặt (không ngủ) | 253-264 ms | 757-779 ms | 1,4 s | 38 |
| GPU sync 1, chờ waitable trước khi vẽ | Present 1,2 ms, **chờ waitable 221 ms** | - | 745 ms | (chuyển chỗ chặn) |
| GPU sync 1, cửa sổ được kích hoạt | 222 ms | 237-731 ms | 745 ms | 38 |
| GPU sync 0 (`ImmediatePresent`) | **1,1-1,2 ms** | 1,3-1,8 ms | 2-12 ms | 0-1 |
| WARP sync 1 | 0,26 ms | 219 ms | 504 ms | 3-4 |

Kết luận: trên máy này DWM trả buffer cho swap chain flip-model mỗi ~222 ms khi cửa sổ cập nhật thưa (Intel UHD 630 + DWM Win 11 build 26300),
bất kể GPU/WARP, latency 1-2, 2-4 buffer, FlipDiscard/FlipSequential, độ phân giải timer (`timeBeginPeriod(1)`) hay kích hoạt cửa sổ. Chờ waitable
chỉ chuyển chỗ chặn. Chỉ `Present(0)` không chặn.

### Phần còn lại sau giảm thiểu (NHẬN BIẾT, CHƯA GIẢI QUYẾT)

Nhịp thưa vẫn có khung bị chặn ~2 x 222 ms **trong `ID2D1DeviceContext::EndDraw`** (không phải `Present`): khoảng cách giữa hai khung 100-450 ms
chặn ~25-40 % khung (khoảng 440-500 ms), nhịp dày 60 Hz thì không (P95 5 ms):

| Cấu hình (sync 0, khung trống) | Present+EndDraw P50 | P95 | Max | Khung >= 8 ms |
|---|---:|---:|---:|---:|
| cách nhau 16 ms | 1,1 ms | 1,5-8,5 ms | 3,6-490 ms | 0-3 / 40 |
| cách nhau 100 ms | 1,1-1,8 ms | 488-501 ms | 503-509 ms | 6-12 / 40 |
| cách nhau 450 ms | 1,2-1,4 ms | 410-440 ms | 410-440 ms | 3-5 / 12 |
| cách nhau 1000 ms | 1,4-17 ms | 49-142 ms | 49-142 ms | 3-9 / 12 |

Không khắc phục được bằng: 3 hay 4 buffer, FlipSequential, `timeBeginPeriod(1)`, kích hoạt cửa sổ, `Present(0)`. Nguyên nhân gốc chưa xác định
(nghi quản lý nguồn của Intel iGPU/DWM sau khoảng nghỉ; WP-20 cũng đã gặp ở khung đầu). Lô cuối (GPU thật, tải nền 77-95 %):

| Pha | Khung | Khung P50 | P95 | Present P50 | Khung bị chặn (>= 8 ms) | Max |
|---|---:|---:|---:|---:|---:|---:|
| Điều hướng (8 ảnh, 150 ms) | 8 | 2,9-3,0 ms | 3,3-416 ms | 2,8-2,9 ms | 0-1 | 416 ms |
| Crossfade 200 ms (6 lần) | 67-103 | 4,0-4,1 ms | 4,6-6,0 ms | 3,9-4,0 ms | 2-3 | 152-454 ms |
| Zoom in x5 + nâng cấp full | 7-9 | 4,1-10,4 ms | 457-499 ms | 4,1-10,4 ms | 2-4 | 457-499 ms |
| Pan 120 khung ở 60 Hz | 91-112 | 5,0 ms | 5,2-5,4 ms | 5,0 ms | 1 | 17-366 ms |
| Zoom out về Fit | 1 | 4,9-8,8 ms | - | 3,9-7,6 ms | 0 | 8,8 ms |

(3 lần chạy liên tiếp; "khung" của crossfade/pan < 120 là khung bị hoãn do swap chain chưa sẵn sàng, FramePacer.)
Tức: khung đều đặn P95 ~5 ms (đạt <= 16 ms; mục tiêu thẻ key->present P95 <= 3 ms CHƯA đạt trong lô này vì tải nền 80-95 %), nhưng các khung
**đơn lẻ** (một lần bấm phím sau khi nghỉ) có thể bị chặn tới ~0,5 s. Phương án tiếp theo: xem fragment mục 4.

## Thời gian chặn đã giảm thiểu

- Ảnh đầu (WP-20): `Present(0)` như cũ.
- Khung sau: `FramePacer` - cửa sổ thường luôn `Present(0)`; F11 (phủ màn hình) `Present(1)` + chờ waitable không chặn, tự hạ cấp nếu vẫn chặn.
- Nâng cấp preview -> full: upload nền theo dải 2 MB ở `UiPriority.Background`, bản mới chỉ thay khi đã sẵn (không còn khung upload đồng bộ
  24 MP ~35-49 ms của WP-15); ảnh cũ vẫn vẽ vào cùng hộp nên không nhảy layout.
- Thu nhỏ nặng khi chuyển động (zoom < 100 % trên ảnh gốc, rủi ro WP-15: HQC 27-45 ms GPU/khung): tạm Linear (6-7 ms), vẽ lại HQC sau 120 ms đứng yên.
