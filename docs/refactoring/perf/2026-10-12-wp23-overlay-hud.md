# WP-23: overlay HUD (toolbar, trạng thái, thông tin thư mục, zoom, huy hiệu cặp ảnh) - số đo (2026-10-12)

Gói: [NOWPF-WP23](../decisions/NOWPF-WP23-OVERLAY-HUD.md). Base = `origin/master` `a42f5b13`. Máy dev (i7-9750H, Intel UHD 630 + RTX 2080 Max-Q),
nhiều agent cùng chạy nên số tuyệt đối nhiễu; chỉ so sánh trong cùng lô.

## 1. Vẽ một khung HUD đầy đủ

`ShellOverlayPerfTests` (Category=Manual): cảnh = toolbar + StatusPanel (3 dòng, có hàng cảnh báo bỏ qua) + FolderInfoPanel + ZoomIndicator +
CapturePairBadge, 1920x1080 ở DPI 1,5 (1280x720 DIP), tiếng Việt, DirectWrite thật, bề mặt D2D offscreen. Mỗi khung =
`ShellOverlay.Render` (Arrange + Render + đồng bộ vùng tooltip) + `EndDrawAndPresent`. n = 270 khung ổn định (30 khung đầu bỏ), 180-200 khung
cho hai ca sau.

| Ca | Thiết bị | P50 | P95 | max |
|---|---|---:|---:|---:|
| Arrange (layout đã cache) | GPU | 0,013 ms | 0,025 | 0,035 |
| Arrange + Render + Present, CPU submit | GPU | 0,338 ms | 0,472 | 1,438 |
| Như trên, chờ GPU xong | GPU | 1,034 ms | 1,333 | 2,467 |
| Chữ zoom đổi mỗi khung (layout miss), chờ GPU | GPU | 1,010 ms | 1,296 | 1,715 |
| Đang fade (opacity < 1, đẩy qua lớp), chờ GPU | GPU | 0,901 ms | 1,114 | 1,252 |
| Arrange + Render + Present, CPU submit | WARP | 0,311 ms | 0,514 | 0,805 |
| Như trên, chờ GPU xong | WARP | 1,004 ms | 1,827 | 2,989 |
| Chữ zoom đổi mỗi khung | WARP | 1,246 ms | 2,763 | 6,318 |

Khung HUD đầu (layout lạnh, chưa làm nóng): GPU 49 ms trong test (JIT + DirectWrite lạnh), WARP 4,9 ms. Trong exe thật chi phí lạnh được
đưa sang thread pool (mục 2). Ngân sách khung 60 Hz = 16,7 ms: HUD ~0,3 ms CPU, ~1 ms tới khi GPU xong.

## 2. Chi phí khởi động (exe R2R, `dotnet publish -r win-x64`, ảnh đầu là file thứ 1300 của F4)

Đo hiệu số TRONG cùng một lần chạy (mốc `swapChainReady` -> `mainWindowConstructed` -> `firstPresent` của CSV perf), vì tuyệt đối giữa hai bản
bị nhiễu thứ tự chạy (bản chạy sau luôn chậm hơn 20-70 ms trên máy này). 8-10 vòng xen kẽ, median:

| Bản | AttachRenderer -> constructed | constructed -> firstPresent |
|---|---:|---:|
| base (a42f5b13, chưa HUD) | 0,1 ms | 2,6-6,8 ms |
| WP-23 lần 1 (DirectWrite + tooltip tạo đồng bộ) | 11,1 ms | 12,6 ms |
| WP-23 cuối (tooltip tạo sau khung đầu + DirectWrite làm nóng trên pool) | 5,4-8,0 ms | 7,6 ms |

=> HUD thêm ~10 ms (~2 %) vào đường tới khung đầu; ảnh đầu ~490 ms (WP-20) không đổi ngoài nhiễu. Phân rã `AttachOverlay` (build chưa
làm nóng): DirectWrite factory 1,5-2,7 ms, tooltip native (nạp comctl32 v6) 2,7-6,7 ms (đã chuyển ra sau khung đầu), dựng
`ShellOverlay` + `Tr` 3-5 ms, `Update` 0,15 ms.
