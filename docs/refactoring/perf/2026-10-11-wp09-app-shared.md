# WP-09: tách PhotoReview.App.Shared - ảnh đầu từ Explorer, trước/sau (2026-10-11)

Gói: [NOWPF-WP09](../decisions/NOWPF-WP09-APP-SHARED.md). Base = `origin/master` `2257115b` (WP-06); new = nhánh `feat/nowpf-wp09-app-shared`. Không đổi mã đường nóng, chỉ dời file giữa hai assembly.

## Cách đo

- Cả hai bản `dotnet publish -c Release -r win-x64 --self-contained false` (R2R từ csproj); một JPEG trong F4 (`C:\Xiuren\[[WALLPAPER]`, file thứ 1300 theo tên), tiến trình mới mỗi lần.
- Mỗi lần chạy: data root riêng (`PHOTOREVIEW_DATA_ROOT`, `PHOTOREVIEW_ISOLATE_CONFIG=1`) = cache đĩa rỗng, `PHOTOREVIEW_PERF_TRACE`; không có `window-placement.json` (kích thước mặc định).
- Chỉ số: ms từ `Process.Start` tới khi CSV perf có `RenderedFrame` (thăm dò 15 ms, nên cộng ~0-15 ms). 1 vòng khởi động bỏ, base/new xen kẽ thứ tự xáo mỗi vòng. Công cụ: script thăm dò ở scratchpad (không commit), như P-1.
- **Máy rất bận** (CPU 97-100 % do nhiều agent chạy test/benchmark song song): tuyệt đối chậm gấp ~3,5 lần lô yên tĩnh của P-1 (782 ms). Chỉ so sánh tương đối.

## Kết quả (hai lô, tổng 22 vòng mỗi bản)

| Lô | base median [min-max] | new median [min-max] | delta median |
|---|---:|---:|---:|
| 1 (10 vòng) | 2838 [2567-4767] | 2887 [2408-4337] | +1,7 % |
| 2 (12 vòng) | 2957 [2514-7100] | 3088 [2567-7538] | +4,4 % |
| gộp 22 vòng | 2923 | 3008 | **+2,9 %** |
| nửa thấp (11 vòng nhanh nhất), trung bình | 2723 | 2716 | -0,3 % |

Kết luận: không hồi quy > 5 % (mốc thẻ); chênh median nằm trong nhiễu của máy bận, nửa thấp (ít nhiễu) ngang nhau. Chi phí lý thuyết: một assembly R2R nữa nạp từ đầu (App.Shared đã được nạp từ WP-01 vì App tham chiếu nó,
nay chứa mã nóng thay vì App) nên số lần nạp assembly không đổi so với master. Cần đo lại trên máy yên tĩnh khi đóng đợt 2 (cùng lời dặn của WP-06).
Chưa đo: `tune-matrix` S3 Repeat 3 (máy bận làm P95 vô nghĩa; mã decode/preload/preview không đổi, chỉ đổi assembly chứa).
