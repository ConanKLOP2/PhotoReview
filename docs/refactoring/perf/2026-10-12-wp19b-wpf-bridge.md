# WP-19b: App.WpfWindows + Shell.WpfBridge - startup bản WPF và thời gian mở cửa sổ phụ (2026-10-12)

Gói: [NOWPF-WP19B](../decisions/NOWPF-WP19B-WPF-BRIDGE.md). Base = `origin/master` `c44ec5bf` (WP-09); new = nhánh `feat/nowpf-wp19b-wpf-bridge`.

## Startup bản WPF (ảnh đầu từ Explorer)

- Cả hai bản `dotnet publish -c Release -r win-x64 --self-contained false` (R2R); một JPEG trong F4 (file thứ 1300 theo tên), tiến trình mới mỗi lần, data root riêng
  (`PHOTOREVIEW_DATA_ROOT`, `PHOTOREVIEW_ISOLATE_CONFIG=1`), `PHOTOREVIEW_PERF_TRACE`; chỉ số = ms từ `Process.Start` tới `RenderedFrame` trong CSV (thăm dò 15 ms). 1 vòng khởi động bỏ,
  10 vòng xen kẽ, thứ tự xáo. Tải máy khi đo: CPU ~22 % (khá yên tĩnh).

| | median | min | max |
|---|---:|---:|---:|
| base | 1228 ms | 1208 | 1254 |
| new | 1252 ms | 1207 | 3201 (1 ngoại lai) |

delta median **+1,9 %** (min ngang nhau): không hồi quy > 5 %. Chi phí mới: một assembly R2R (`App.WpfWindows`) nạp ngay ở startup (MainWindow dùng Themes/TrExtension/converter của nó).

## Mở cửa sổ phụ qua cầu (probe `--probe-timing`, build Release chưa R2R, tiến trình sạch, máy bận)

| Cửa sổ | lần đầu (tới khi hiện) | lần sau (tới khi hiện) |
|---|---:|---:|
| Diagnostics | 466-669 ms | 100-159 ms |
| Settings | 500-688 ms | 171-216 ms |

Thẻ: <= 600 ms lần đầu, <= 150 ms lần sau. Lần đầu nằm quanh mốc (thay đổi theo tải); Settings lần sau ~170-215 ms vượt mốc 150 ms (dựng XAML ~470 dòng + 1257 dòng code-behind):
ghi nhận chưa đạt, cần đo lại với R2R publish của shell ở WP-29.
