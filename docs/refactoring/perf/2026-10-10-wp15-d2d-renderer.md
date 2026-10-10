# WP-15 renderer Direct2D: upload, prefetch, pan, Fit (2026-10-10)

Quyết định: [`NOWPF-WP15`](../decisions/NOWPF-WP15-D2D-RENDERER.md). Nhánh `feat/nowpf-wp15-d2d-renderer`.

## Cách đo

- `tests/PhotoReview.Shell.Tests/Rendering/RendererPerfTests.cs` (`Category=Manual`, không assert thời gian), Release:
  `dotnet test tests/PhotoReview.Shell.Tests -c Release --filter "FullyQualifiedName~RendererPerfTests" --logger "console;verbosity=detailed"`.
- Ảnh 24 MP (6000x4000 Pbgra32 = 96 MB), viewport 1920x1080 offscreen, DPI 1. "submit" = `BeginDraw..EndDrawAndPresent` (CPU);
  "done" = thêm chờ GPU xong (đọc 1 pixel qua texture staging). Bỏ 10 khung đầu; upload bỏ lượt đầu.
- Máy: i7-9750H, GPU thật qua D3D11 hardware (FL 11, max bitmap 16384) và WARP.
- **Tải CPU nền: 60-100 % trong suốt lần đo** (nhiều agent build/test chạy song song trên cùng máy; mẫu 1 s trước lần đo:
  99,100,94,85,72,71,60,97,96,100 %; sau: 66,80,94,87,93 %). Số dưới đây có nhiễu, nhất là cột "done" và WARP; **chưa phải
  số "máy rảnh"** - cần đo lại khi người dùng vắng và không có agent khác.

## GPU thật

| Phép đo | n | P50 | P95 | max |
|---|---:|---:|---:|---:|
| upload 24 MP (GetOrUpload + GPU xong) | 8 | 34,9 | 48,9 | 48,9 |
| upload 1920x1080 | 40 | 4,20 | 6,10 | 6,48 |
| prefetch dải 2048 KB (x46) | 46 | 0,43 | 6,15 | 21,2 |
| clear-only (chi phí đồng bộ) submit / done | 100 | 0,12 / 1,01 | 0,36 / 3,07 | 0,96 / 3,77 |
| pan 100 % 1920x1080 HQC submit / done | 200 | 0,26 / 1,70 | 0,79 / 3,88 | 4,90 / 34,7 |
| pan 200 % HQC submit / done | 100 | 0,51 / 4,16 | 1,70 / 8,76 | 4,20 / 15,4 |
| Fit 24 MP -> 1620x1080 HQC submit / done | 50 | 2,34 / **26,8** | 7,61 / **41,4** | 14,3 / 71,4 |
| Fit 24 MP -> 1620x1080 Linear submit / done | 50 | 0,56 / 6,31 | 0,70 / 7,20 | 0,84 / 7,71 |

## WARP (tham khảo, đường test CI)

| Phép đo | P50 | P95 |
|---|---:|---:|
| upload 24 MP | 54,0 | 191,9 |
| upload 1920x1080 | 10,8 | 36,6 |
| pan 100 % HQC submit / done | 0,14 / 9,10 | 0,86 / 21,4 |
| pan 200 % HQC submit / done | 0,37 / 76,9 | 2,02 / 179 |
| Fit 24 MP HQC submit / done | 3,82 / 321 | 10,5 / 948 |
| Fit 24 MP Linear submit / done | 0,13 / 7,20 | 0,41 / 16,8 |

## Đọc kết quả

- Ngưỡng thẻ "pan CPU submit <= 3 ms" đạt (P95 0,79 ms; lần đo trước dưới tải 99 %: 0,26-1,74 ms).
- Upload 1920x1080 P95 6,1 ms vượt mục tiêu thẻ (<= 2 ms) khi tính "GPU xong" trong lúc tải 60-100 %; phần CPU của `GetOrUpload`
  (copy vào bitmap) nhỏ hơn (cột "done" gồm chờ GPU). Đo lại khi máy rảnh trước khi kết luận.
- **Rủi ro HQC thu nhỏ 24 MP:** GPU xong 27-41 ms (lần trước 37-45 ms); Linear 6-7 ms. Chỉ gặp khi zoom < 100 % trên ảnh original
  (WP-21), không gặp ở Fit vì app vẽ preview cỡ viewport.
- Prefetch theo dải: UI thread bị chiếm P50 0,43 ms mỗi dải 2 MB; đỉnh 6-21 ms là nhiễu lên lịch dưới tải.
