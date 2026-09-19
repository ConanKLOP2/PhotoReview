# D05 — Tách đọc/decode và `--io-decode-split`

- **Ngày chạy:** 2026-09-16/17 (UTC), máy chẩn đoán duy nhất, đơn luồng, không có tải nền khác được biết trước.
- **Công cụ:** `tools/PhotoReview.Benchmark.Cli --io-decode-split <folder> <outDir> [widths] [max]` (D05). Mã nguồn: `tools/PhotoReview.Benchmark.Cli/IoDecodeSplit.cs`.
- **Liên quan:** `PERF-DIAGNOSIS-PLAN.md` mục 4 (H8, H9) và mục 8 (R-IO, R-DEC, R-DISK).

## 1. Điều kiện đo và giới hạn

- Mỗi phép đo (`read`, `headerOnly`, `decodeFromMem[w]`, `decodeFromFile[w]`, `pngEncode`, `pngDecode`) chạy 3 lần liên tiếp trên cùng một file. Lần 1 được gọi là **"cold"**, P50 của lần 2–3 là **"warm"**. **Cold ở đây chỉ cold đối với process này** — file rất có thể đã nằm trong bộ nhớ đệm standby của Windows từ lần liệt kê thư mục/preload trước (hoặc từ chính lần Coordinator kiểm tra fixture), nên số "cold" không phản ánh cold-OS-cache thật. Không có bước làm trống standby list (cần RAMMap + quyền admin, thuộc D07/D08, người dùng tự thao tác).
- Đo trên **một luồng, tuần tự** (không qua Dispatcher/STA, không qua `PreloadScheduler`), nên không phản ánh tranh chấp tài nguyên với preload — chỉ đo chi phí I/O/decode thuần túy của `PreviewImageService.DecodeSource`.
- `decodeFromFile[w]` gọi thẳng `PreviewImageService.DecodeSource(path, w)` (đường decode thật, không bật `PHOTOREVIEW_DIAG_PREREAD`). `decodeFromMem[w]` decode cùng bitmap nhưng từ `MemoryStream` đã đọc sẵn, cùng tuỳ chọn `BitmapCacheOption.OnLoad`.
- `pngEncode`/`pngDecode` mô phỏng disk cache tại width cố định 2560: encode bằng `PngBitmapEncoder`, ghi ra file tạm trong `%TEMP%`, rồi decode lại file đó bằng `DecodeSource` (giống một lần đọc disk cache thật). Việc decode để lấy bitmap trước khi encode **không** tính vào thời gian `pngEncode`.
- Đường dẫn ảnh **không** được ghi vào `raw.csv`/`summary.md`; file chỉ được tham chiếu bằng số thứ tự.
- Dữ liệu thô (`raw.csv`, `summary.md`) nằm ở `C:\MyProjects\PhotoReview\work\diag\io-split\<F1|F1b|F2|F3>\` — **không commit** (ngoài phạm vi git, nằm ngoài worktree/repo).

## 2. Bộ dữ liệu đã chạy

| Bộ | Mô tả (theo `fixtures.local.json`) | Số file đo | Widths |
|---|---|---|---|
| F1  | JPEG 24MP nén mạnh (~1.4 MB/ảnh), 58 file thật | 40 | 0, 1920, 2560, 3840 |
| F1b | JPEG 27MP chất lượng cao (~12 MB/ảnh), 54 file thật | 40 | 0, 1920, 2560, 3840 |
| F2  | JPEG 48MP, 98 file thật | 40 | 0, 1920, 2560, 3840 |
| F3  | PNG, 22 file thật (toàn bộ folder) | 22 | 0, 1920 |

(width=0 nghĩa là không đặt `DecodePixelWidth`, decode ở độ phân giải gốc.)

## 3. Bảng tổng hợp theo bộ

### F1 (JPEG 24MP nén mạnh, phần lớn ảnh trong mẫu thực tế là 592×1280 hoặc 4000×6000 lẫn lộn)

- Megapixel P50 = 0.8 MP (min 0.6, max 24.0) — mẫu 40 file đầu (thứ tự tên) không đại diện hết cho megapixel danh nghĩa của cả folder.
- Byte trung bình: 2.04 MB.

| | cold P50 | warm P50 | warm P95 |
|---|---:|---:|---:|
| read (ms) | 0.2 | 0.2 | 2.0 |
| headerOnly (ms) | 0.4 | 0.3 | 0.6 |

| width | mem warm P50 | mem warm P95 | file warm P50 | file warm P95 |
|---:|---:|---:|---:|---:|
| 0 | 7.6 | 145.5 | 6.8 | 164.0 |
| 1920 | 29.3 | 150.1 | 31.6 | 154.0 |
| 2560 | 48.7 | 225.1 | 47.1 | 229.5 |
| 3840 | 89.9 | 253.5 | 90.1 | 279.2 |

- Tỷ lệ read/(read+decodeFromMem): 0–1% (P50) mọi width.
- pngDecode warm P50 = 102.2 ms; read+decodeFromMem@2560 warm P50 = 48.8 ms → **PNG cache chậm hơn ~2×**. pngEncode P50 = 700.6 ms, PNG trung bình 8035 KB.

### F1b (JPEG 27MP chất lượng cao, ~12 MB/ảnh)

- Megapixel P50 = 29.9 MP (min 13.4, max 31.2). Byte trung bình 12.41 MB.

| | cold P50 | warm P50 | warm P95 |
|---|---:|---:|---:|
| read (ms) | 14.4 | 4.4 | 5.6 |
| headerOnly (ms) | 0.3 | 0.1 | 0.2 |

| width | mem warm P50 | mem warm P95 | file warm P50 | file warm P95 |
|---:|---:|---:|---:|---:|
| 0 | 242.0 | 278.3 | 254.1 | 307.3 |
| 1920 | 227.0 | 273.3 | 239.0 | 286.9 |
| 2560 | 283.4 | 355.2 | 305.8 | 359.1 |
| 3840 | 340.0 | 399.9 | 360.9 | 436.7 |

- Tỷ lệ read/(read+decodeFromMem): 1–2% mọi width.
- pngDecode warm P50 = 107.1 ms; read+decodeFromMem@2560 warm P50 = 287.3 ms → **PNG cache nhanh hơn ~2.7×**. pngEncode P50 = 758.9 ms, PNG trung bình 7967 KB.

### F2 (JPEG 48MP)

- Megapixel P50 = 48.0 MP (đồng đều). Byte trung bình 7.72 MB.

| | cold P50 | warm P50 | warm P95 |
|---|---:|---:|---:|
| read (ms) | 3.2 | 2.8 | 4.2 |
| headerOnly (ms) | 0.3 | 0.1 | 0.2 |

| width | mem warm P50 | mem warm P95 | file warm P50 | file warm P95 |
|---:|---:|---:|---:|---:|
| 0 | 1358.7 | 1498.3 | 1377.3 | 1514.2 |
| 1920 | 1354.1 | 1534.3 | 1366.9 | 1524.5 |
| 2560 | 1328.4 | 1471.3 | 1372.5 | 1541.7 |
| 3840 | 1462.2 | 1648.4 | 1535.3 | 1719.0 |

- Tỷ lệ read/(read+decodeFromMem): ~0% mọi width — decode chiếm gần như toàn bộ thời gian.
- pngDecode warm P50 = 111.2 ms; read+decodeFromMem@2560 warm P50 = 1331.4 ms → **PNG cache nhanh hơn ~12×**. pngEncode P50 = 885.4 ms, PNG trung bình 9698 KB.

### F3 (PNG, toàn bộ 22 file, width 0 và 1920)

- Megapixel P50 = 2.1 MP (đồng đều). Byte trung bình 1.65 MB.

| | cold P50 | warm P50 | warm P95 |
|---|---:|---:|---:|
| read (ms) | 0.7 | 0.4 | 0.8 |
| headerOnly (ms) | 0.1 | 0.1 | 0.2 |

| width | mem warm P50 | mem warm P95 | file warm P50 | file warm P95 |
|---:|---:|---:|---:|---:|
| 0 | 16.5 | 20.7 | 17.7 | 22.2 |
| 1920 | 37.6 | 44.9 | 40.5 | 49.9 |

- Tỷ lệ read/(read+decodeFromMem): 1–2% mọi width.
- pngDecode warm P50 = 69.7 ms; read+decodeFromMem@2560 warm P50 = 53.6 ms → **PNG cache chậm hơn ~1.3×**. pngEncode P50 = 452.5 ms, PNG trung bình 4507 KB.

## 4. Kết luận H8

> H8: "`DecodePixelWidth` không giảm đáng kể thời gian decode JPEG (decode full rồi mới scale)"

**Kết quả trái ngược nhau tuỳ kích thước ảnh nguồn:**

- **F1, F1b, F3** (ảnh ≤ 30 MP, kể cả PNG): decode ở width nhỏ **nhanh hơn rõ rệt** so với width lớn hoặc full-res (F1: 7.6 ms @0 → 89.9 ms @3840; F1b: 227.0 ms @1920 → 340.0 ms @3840; F3: 16.5 ms @0 → 37.6 ms @1920). **H8 SAI** cho các bộ này — `DecodePixelWidth` thực sự giúp giảm chi phí decode, việc chọn target width nhỏ hơn là có lợi thật.
- **F2** (ảnh 48 MP đồng đều): decode gần như không đổi theo width (1328–1462 ms trên toàn dải 0–3840). **H8 ĐÚNG** cho bộ này — ở độ phân giải rất cao, chi phí decode JPEG áp đảo mọi lợi ích của downscale-khi-decode (có thể do decode baseline/progressive JPEG phải giải toàn bộ hệ số trước khi WIC mới áp scale transform).
- **Ghi chú phụ:** với ảnh gốc nhỏ hơn width yêu cầu (ví dụ ảnh 592×1280 trong F1 với width=1920), việc đặt `DecodePixelWidth` lớn hơn kích thước gốc vẫn tốn thêm chi phí (WIC vẫn chạy qua đường scale transform dù không thực sự phóng to) — đây là một phát hiện phụ, không phải giả thuyết H8 gốc nhưng đáng lưu ý cho chính sách chọn target width (không nên đặt `DecodePixelWidth` khi nó ≥ kích thước gốc).

**Tổng kết:** H8 không đúng/sai tuyệt đối — phụ thuộc megapixel nguồn. Với phần lớn dữ liệu người dùng thực tế (F1, F1b, F3: 2–30 MP), giảm target width **có** giảm decode time đáng kể, nên chính sách target width theo viewport (WC3/T82) vẫn có cơ sở. Với ảnh cực lớn (48 MP, F2), lợi ích giảm đi nhiều — decode luôn tốn ~1.3–1.5 giây bất kể width, nên với các folder toàn ảnh 48MP+ cần cân nhắc giải pháp khác (decode progressive/tiled, hoặc chấp nhận độ trễ).

## 5. Kết luận H9

> H9: "Đọc PNG disk cache chậm hơn decode lại JPEG nguồn"

**Kết quả cũng trái ngược tuỳ chi phí decode nguồn:**

| Bộ | pngDecode P50 | read+decodeFromMem@2560 P50 | H9 |
|---|---:|---:|---|
| F1  | 102.2 ms | 48.8 ms   | ĐÚNG (disk cache chậm hơn ~2.1×) |
| F1b | 107.1 ms | 287.3 ms  | SAI (disk cache nhanh hơn ~2.7×) |
| F2  | 111.2 ms | 1331.4 ms | SAI (disk cache nhanh hơn ~12×) |
| F3  | 69.7 ms  | 53.6 ms   | ĐÚNG (disk cache chậm hơn ~1.3×) |

**Quy luật rút ra:** thời gian decode PNG cache gần như hằng số (~70–111 ms P50, không phụ thuộc nhiều vào megapixel nguồn vì PNG luôn được encode ở width cố định 2560 và WIC decode PNG (không nén lossy phức tạp) khá ổn định). Khi decode JPEG/PNG nguồn ở 2560 rẻ hơn ~100 ms (ảnh nhỏ/nén mạnh như F1, F3), disk cache PNG lỗ. Khi decode nguồn đắt hơn ~150 ms (ảnh lớn/chất lượng cao như F1b, F2), disk cache PNG lãi rất lớn — với F2 (48MP) tiết kiệm ~12×.

**H9 kết luận:** không đúng tuyệt đối; ngưỡng hoà vốn nằm quanh **t_decode(nguồn, @2560) ≈ 100–150 ms**. Dưới ngưỡng đó, disk cache PNG là chi phí thừa (và còn tốn CPU/đĩa để encode ~450–900 ms một lần); trên ngưỡng đó, disk cache PNG có lợi rõ rệt.

## 6. Tỷ lệ I/O so với decode và ước lượng lợi ích R-4

Trên cả 4 bộ, tỷ lệ `read / (read + decodeFromMem)` P50 nằm trong khoảng **0–2%** ở mọi width — I/O (đọc file từ ổ đĩa/OS cache) gần như không đáng kể so với decode, kể cả ở lần "cold" trong process (read cold P50 cao nhất quan sát được là 14.4 ms ở F1b, so với decode P50 227–360 ms cùng bộ).

**Ước lượng lợi ích R-4** (tách read khỏi decode / bỏ lần đọc thứ hai, `PHOTOREVIEW_DIAG_PREREAD`): vì read chỉ chiếm 0–2% tổng thời gian, việc loại bỏ một lần đọc file trùng lặp (nếu có, ví dụ giữa `GetOriginalDimensionsAsync` và `DecodeSource`) chỉ tiết kiệm tối đa **vài ms trên mỗi ảnh** trong điều kiện file đã nằm trong OS cache (trường hợp phổ biến khi Next liên tục trong cùng folder). Lợi ích này **không đáng kể** so với decode (chiếm 98–100% thời gian). R-4 có thể mang lại lợi ích lớn hơn trên ổ mạng/USB chậm hoặc khi OS cache bị áp lực (cold thật), nhưng dữ liệu ở đây (máy local, SSD, OS cache ấm) không cho thấy R-4 là ưu tiên cao.

## 7. Khuyến nghị cho R-IO, R-DEC, R-DISK (plan mục 8)

- **R-IO** (`t_read` lớn): dữ liệu này cho thấy trên máy/ổ đĩa hiện tại, `t_read` gần như không bao giờ chiếm ≥ ngưỡng đáng kể của tổng thời gian điều hướng — R-IO khó kích hoạt trong điều kiện đo được. Cần dữ liệu từ ổ chậm (S10, chưa có) trước khi hạ thấp ưu tiên các task liên quan I/O.
- **R-DEC** (`t_decode` ≥ 40% tổng): với mọi bộ đo, decode chiếm 98–100% thời gian giữa read và decode — nếu `t_decode` cũng chiếm phần lớn trong toàn bộ chuỗi điều hướng thực tế (kể cả stat, lookup, assign, render), **R-DEC gần như chắc chắn kích hoạt**, đặc biệt cho F2 (48MP, decode ~1.3–1.5 giây/ảnh không đổi theo width). Ưu tiên WC3 (T82 — chính sách target width) là hợp lý cho ảnh ≤ 30MP; với ảnh ≥ 48MP cần thêm giải pháp ngoài target width (xem mục 4).
- **R-DISK** (`t_disk` ≥ `t_read + t_decode`): kết quả **không đồng nhất theo folder** — sai với ảnh nặng (F1b, F2), đúng với ảnh nhẹ (F1, F3). Khuyến nghị: R-DISK nên được áp dụng **có điều kiện theo t_decode nguồn của từng ảnh**, không phải một quyết định toàn cục bật/tắt disk cache. Ví dụ: chỉ ghi/đọc disk cache PNG cho ảnh có `t_decode` ước tính (hoặc đo thực tế lần đầu) vượt một ngưỡng (~150 ms), thay vì cache mọi ảnh. Ngoài ra, chi phí encode PNG (450–900 ms/ảnh) tự nó là một khoản tốn CPU đáng kể chạy nền — nên cân nhắc định dạng cache rẻ hơn để encode (JPEG q95 hoặc BGRA thô, như plan đã gợi ý) thay vì PNG, đặc biệt cho ảnh có megapixel cao nơi cache có lợi nhất nhưng cũng tốn CPU nhất để tạo.

## 8. Ghi chú thực thi

- Lệnh đã chạy (đường dẫn thư mục ảnh chỉ ghi trong nhật ký local, không commit): `--io-decode-split <F1|F1b|F2> <outDir> "0,1920,2560,3840" 40` và `--io-decode-split <F3> <outDir> "0,1920" 30` (F3 chỉ có 22 file PNG nên toàn bộ được đo).
- Không chạy app GUI, không gửi input hệ điều hành, không đụng clipboard trong quá trình đo (D05 chỉ dùng CLI console `PhotoReview.Benchmark.Cli.exe`).
