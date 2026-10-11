# WP-06 tách PhotoReview.Imaging.Wpf: số đo decode (2026-10-11)

Nhánh `feat/nowpf-wp06-imaging-wpf-split`. Quyết định: [`NOWPF-WP06`](../decisions/NOWPF-WP06-IMAGING-WPF-SPLIT.md).
Công cụ: `Benchmark.Cli --decoder-bench <jpg24> <out> Wpf,WicDirect,TurboJpeg 1920 3` (có sẵn, không viết thêm).

## Cách đo và độ tin cậy

- Tập: 24 JPEG q92 (3332-3762 x 5000, 17-19 MP) chuyển từ PNG thật của F4 (`C:\Xiuren\[[WALLPAPER]`); 3 vòng/ảnh = 72 decode/backend/lượt.
- 6 lượt xen kẽ base (`origin/master` @ 33ea83ba, worktree riêng) và new (nhánh này), mỗi lượt chạy cả 3 backend trong cùng tiến trình. Con số dưới là
  **median của 6 lượt** (P50 và P95 của mỗi lượt).
- **Máy bận:** nhiều agent khác build/test song song trong suốt lúc đo (CPU nền không điều khiển được). Độ phân tán giữa 6 lượt hẹp
  (xem dải), nhưng số tuyệt đối chỉ so được trong cùng lô xen kẽ.

## Kết quả

| Backend | P50 base | P50 new | Δ P50 | P95 base | P95 new | Δ P95 | Dải P50 base | Dải P50 new |
|---|---:|---:|---:|---:|---:|---:|---|---|
| Wpf | 94,3 | 94,5 | +0,1 % | 108,3 | 107,8 | -0,5 % | 93,7-96,3 | 93,6-96,7 |
| WicDirect | 146,5 | 147,2 | +0,5 % | 163,6 | 161,5 | -1,3 % | 145,2-149,3 | 144,8-148,4 |
| TurboJpeg (fine-scale WIC Fant, mới) | 226,6 | 227,4 | +0,4 % | 248,8 | 248,8 | 0,0 % | 224,6-228,6 | 226,3-229,1 |

Không có hồi quy > 5 % (cả P50 lẫn P95); mọi chênh nằm trong dải phân tán giữa các lượt. Số lỗi 0/72 ở mọi lượt. Ghi chú: TurboJpeg chậm hơn Wpf
2,4 lần trên tập này là đặc tính sẵn có của backend (cùng ở base), không phải do WP-06.

## Không đo trong lát cắt này

Perf gate đợt 1 (mục 7.4 của kế hoạch) còn các chỉ số: `tune-matrix.ps1` S2/S3/S4, ảnh đầu (probe R2R), peak working set F4 và đếm copy qua
`ReviewMetrics`. Lát cắt này KHÔNG đổi đường key->present trong cache (RAM), chỉ đổi: (1) bước hoàn tất của TurboJpeg (WPF `TransformedBitmap` ->
`PixelBuffer` + `WicPixelScaler` + `PixelOps`; số trên), (2) cấu trúc project (thêm một assembly nạp lúc khởi động: `PhotoReview.Imaging.Wpf.dll`),
(3) LibRaw (cùng một bản chép `BitmapSource.Create`, không đổi). Lead nên chạy các chỉ số còn lại trên máy yên tĩnh khi đóng đợt 1.
