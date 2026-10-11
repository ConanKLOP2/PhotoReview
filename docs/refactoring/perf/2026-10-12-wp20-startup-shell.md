# WP-20: startup shell Win32 - ảnh đầu so với bản WPF (2026-10-12)

Gói: [NOWPF-WP20](../decisions/NOWPF-WP20-STARTUP-SHELL.md). Base = `origin/master` `c44ec5bf`.

## Cách đo

- Hai bản `dotnet publish -c Release -r win-x64 --self-contained false` (R2R): `PhotoReview.exe` (Shell.Win32) và `PhotoReview.App.exe` (WPF, cùng commit).
- Một JPEG 11,7 MB trong F4 (`C:\Xiuren\[[WALLPAPER]`, file thứ 1300 theo tên, thư mục 2867 file); mỗi lần chạy một tiến trình mới với
  data root riêng (`PHOTOREVIEW_DATA_ROOT`, `PHOTOREVIEW_ISOLATE_CONFIG=1`: cache đĩa rỗng, không placement) và `PHOTOREVIEW_PERF_TRACE`.
- Chỉ số: ms từ lúc tạo tiến trình tới sự kiện `RenderedFrame` trong CSV perf (mốc `appStartup` + chênh `utcTicks`); thêm `windowShown` và
  `windowRevealed`. 1 vòng khởi động bỏ, shell/WPF xen kẽ từng vòng, 10 vòng mỗi bản, báo median [min-max].
- Máy dev (i7-9750H, Intel UHD 630 + RTX 2080 Max-Q), tải nền thay đổi do nhiều agent cùng chạy: chỉ so sánh trong cùng lô.

## Kết quả (lô cuối, build cuối của gói)

| Bản | Ảnh đầu đã vẽ (RenderedFrame) | Cửa sổ hiện (windowShown) | Cửa sổ thấy được (windowRevealed) |
|---|---:|---:|---:|
| Shell Win32 R2R | **493 ms** [480-928] | 260 ms | **306 ms** |
| WPF R2R | 757 ms [738-1272] | 623 ms | 759 ms |

NW-5 (<= 600 ms R2R): **đạt** (493 ms). Thẻ WP-20 ghi ảnh đầu <= 450 ms và cửa sổ thấy được <= 150 ms: ảnh đầu hụt ~40 ms (phần lớn là decode
JPEG 11,7 MB ~210 ms bắt đầu sau khi đọc config ~140 ms), cửa sổ thấy được 306 ms KHÔNG đạt (xem fragment mục 3.2).

Lô sáng cùng ngày (build trước khi sửa Present, máy yên hơn): shell median 542 ms [517-581], WPF 818-1005 ms.

## Phát hiện: Present(1) của khung ảnh đầu chặn UI thread 250-500 ms

Giữa ngày, cùng build, ảnh đầu bị chậm tới 700-1000 ms: `Assign` (gồm vẽ + Present đồng bộ ảnh đầu) 270-510 ms thay vì 12-19 ms. Đo từng bước:
BeginDraw -> upload -> EndDraw nhanh (< 2 ms), `EndDrawAndPresent` 250-510 ms với `Present(1)`; cùng mã với `Present(0)` 9 ms; với WARP 8 ms;
nội suy Linear hay HighQualityCubic như nhau; làm nóng đồ hoạ trên pool không đổi. Nguyên nhân gốc chưa xác định (nghi pacing của DWM/GPU lai
sau khi bỏ cloak; chỉ xảy ra trên GPU thật, lúc đó). Giảm thiểu đã làm: `D2DRenderSurface.ImmediatePresent` (sync 0) cho tới khi khung có ảnh đầu
đã Present (ảnh tĩnh, không tearing nhìn thấy), sau đó `Present(1)`. VIỆC CHO WP-21: kiểm khung thứ hai trở đi (pan/crossfade) có bị chặn tương tự
không; nếu có thì đổi sang `AllowTearing` hoặc `Present1` + waitable, không chặn UI thread.

## Làm nóng đồ hoạ (A/B, 8 vòng)

Làm nóng (thiết bị D3D offscreen + một lần vẽ HQC trên pool từ đầu tiến trình): median 568 ms vs không làm nóng 668 ms ở lô sáng (nhiễu lớn);
giữ bật, tắt được bằng `PHOTOREVIEW_SHELL_NOWARM=1`.

## Dòng thời gian một lần chạy điển hình (ms từ lúc tạo tiến trình)

`appStartup` 75 -> `servicesBuilt` 100 -> `settingsFileLoaded` 205 -> `instanceLock` 230 -> `earlyDecodeStarted` 240 -> `windowShown` 255 ->
`d3dDeviceReady` 290 -> `firstPresent`/`windowRevealed` 295 -> decode xong ~465 -> `Assign` + `RenderedFrame` ~490.
Đường găng: khởi tạo runtime (75) -> đọc config + ngôn ngữ (~110) -> khoá instance -> decode (~210-230) -> vẽ.