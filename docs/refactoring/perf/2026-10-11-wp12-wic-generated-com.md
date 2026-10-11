# WP-12 WIC sang `[GeneratedComInterface]`: số đo decode và cache (2026-10-11)

Nhánh `feat/nowpf-wp12-wic-generated-com`. Quyết định: [`NOWPF-WP12`](../decisions/NOWPF-WP12-WIC-GENERATED-COM.md).
Công cụ: `Benchmark.Cli --decoder-bench <jpg24> <out> Wpf,WicDirect,TurboJpeg 1920 2` (có sẵn) và một vi-bench cache dùng một lần (test tạm,
không commit; mã mô tả ở mục 2).

## Cách đo và độ tin cậy

- **Máy bận:** nhiều agent khác build/test song song suốt lúc đo (CPU nền không điều khiển được; số tuyệt đối chậm hơn WP-06 khoảng 3 lần).
  Mọi so sánh chỉ có giá trị trong cùng lô **xen kẽ** base (`origin/master` @ 2257115b, worktree riêng) / new (nhánh này), 6 lượt mỗi bên,
  số dưới là **median của 6 lượt**.
- Backend `Wpf` KHÔNG đi qua mã đã đổi, nên chênh của nó là nền nhiễu của lô.

## 1. Decode (`--decoder-bench`, 24 JPEG q92 17-19 MP -> 1920, 2 vòng/ảnh = 48 decode/backend/lượt)

| Backend | P50 base | P50 new | Δ P50 | P95 base | P95 new | Δ P95 | Dải P50 base | Dải P50 new |
|---|---:|---:|---:|---:|---:|---:|---|---|
| Wpf (không đổi, nền nhiễu) | 323,5 | 329,8 | +1,9 % | 593,5 | 667,0 | +12,4 % | 208,8-347,1 | 326,7-357,3 |
| WicDirect | 503,5 | 495,1 | -1,7 % | 835,0 | 938,5 | +12,4 % | 310,4-542,5 | 480,9-533,5 |
| TurboJpeg (fine-scale WIC Fant) | 626,6 | 636,7 | +1,6 % | 1.043,9 | 1.004,0 | -3,8 % | 459,7-666,3 | 598,2-680,4 |

0 lỗi / 144 decode mỗi lượt. P50 của đường đã đổi nằm trong +-2 % (cùng cỡ nền nhiễu của `Wpf` +1,9 %). Δ P95 +12 % của WicDirect trùng với Δ P95
của `Wpf` không đụng mã (+12,4 %) và dải của base rộng (nhiều lượt base chạy lúc máy rảnh hơn: 208-310 ms), nên là nhiễu lô chứ không phải hồi quy; không
có lượt nào của new vượt dải của base. Không hồi quy > 5 % ở P50.

## 2. Cache đĩa + scaler (vi-bench một lần, ảnh 1920x1280 Bgr32, 60 vòng/lượt, 6 lượt xen kẽ)

| Phép | base (ms) | new (ms) | Δ |
|---|---:|---:|---:|
| `WicImageEncoder.EncodeJpeg` q95 | 28,8 | 27,1 | -5,7 % |
| `WicCacheImageReader.Decode` nguyên cỡ | 43,0 | 40,3 | -6,2 % |
| `WicCacheImageReader.Decode` hộp 960x640 | 39,0 | 38,1 | -2,2 % |
| `WicPixelScaler.Resize` 1920x1280 -> 960x640 | 13,1 | 13,4 | +2,2 % |

Median của 6 lượt (median trong lượt). Mọi chênh nằm trong nhiễu (dải giữa các lượt 22-45 ms); không có hồi quy > 5 %, đường mã hoá/giải mã cache
không chậm đi.

## 3. Ghi chú

- Đường nóng của `[GeneratedComInterface]` là cùng một lời gọi vtable như RCW cũ; khác biệt duy nhất mỗi decode là N lần tạo wrapper
  `UniqueInstance` (QueryInterface của phép ép kiểu giữ nguyên số lần) và `ManagedIStream.Read` ghi thẳng vào bộ đệm của WIC bằng `Span<byte>`
  (bỏ bản chép qua `byte[]` của marshaller `[Out] byte[]` cũ): không đo được chênh trong nhiễu.
- Không đo trong lát cắt này: peak working set / ảnh đầu của perf gate đợt 1 (xem NOWPF-WP06 mục 2), vì lát cắt không đổi đường key->present.
