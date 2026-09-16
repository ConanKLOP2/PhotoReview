# D10/D07 — Tranh chấp preload worker và disk cache

- **Trạng thái:** khung (D10). **Số liệu điền ở D07** — bảng dưới đây chỉ có tiêu đề cột/hàng, chưa
  chạy. Đừng suy diễn kết luận từ một bảng rỗng.
- **Liên quan:** `PERF-DIAGNOSIS-PLAN.md` mục 8 (R-CONT, R-DISK), mục 5 (kịch bản S1–S9).
- **Cơ chế đo:** D10 đã thêm hai override chỉ có hiệu lực khi đặt biến môi trường (mặc định tắt,
  hành vi app không đổi — xem `PhotoReview.App/Diagnostics/DiagOptions.cs`):
  - `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0..16`: ghi đè số worker preload đồng thời
    (`PreloadScheduler._workerCount`, dùng cho cả `SemaphoreSlim` lẫn kích thước batch trong vòng
    lặp lịch preload). `0` nghĩa là tắt hẳn preload (`PreloadAroundAsync` trả về ngay, không tạo
    task nền).
  - `PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1`: `PreviewImageService` bỏ qua đọc và ghi disk cache
    PNG (coi như file cache không tồn tại), không xoá cache có sẵn.

## 1. Cách chạy

1. Đặt biến môi trường trước khi khởi động phiên đo (PowerShell):
   ```powershell
   $env:PHOTOREVIEW_DIAG_PRELOAD_WORKERS = "2"      # 0, 2, 4, 8 (mặc định khi không đặt), 12
   $env:PHOTOREVIEW_DIAG_DISABLE_DISKCACHE = "1"     # bỏ khi muốn disk cache bật (giá trị mặc định)
   ```
   Bỏ biến (`Remove-Item Env:\PHOTOREVIEW_DIAG_PRELOAD_WORKERS` v.v.) để quay lại hành vi mặc định
   (8 worker, disk cache bật) giữa các lượt đo.
2. Chạy kịch bản qua driver D06:
   ```powershell
   dotnet run --project PhotoReview.Tests -c Release -- --perf-session tools\diag\scenarios\S2.json <F1_ROOT> <outDir> --mode Preview
   dotnet run --project PhotoReview.Tests -c Release -- --perf-session tools\diag\scenarios\S3.json <F1_ROOT> <outDir> --mode Preview
   ```
   hoặc chạy cả ma trận bằng `tools\diag\run-matrix.ps1` (D06), truyền danh sách kịch bản × điều
   kiện; script không tự đặt các biến `PHOTOREVIEW_DIAG_*` — đặt chúng trong shell trước khi gọi
   script, hoặc lặp lại lời gọi script cho mỗi tổ hợp worker/disk-cache dưới đây.
3. Phân tích CSV bằng D11:
   ```powershell
   dotnet run --project PhotoReview.Tests -c Release -- --perf-analyze <outDir> <summaryOutDir>
   ```
   `summary.md`/`summary.json` cho `t_decode`, phần trăm thời gian preload bị tạm dừng (`PreloadPaused`),
   và các cột cần cho R-CONT/R-DISK bên dưới.
4. Ghi rõ trong nhật ký D07: giá trị `PHOTOREVIEW_DIAG_*` dùng cho mỗi lượt, và xác nhận đã bỏ biến
   (hoặc đặt về mặc định) trước khi kết thúc phiên, để không rò rỉ sang lượt đo khác hay phiên làm
   việc thường ngày.

## 2. Ma trận cần chạy (D07 điền số liệu)

Mỗi ô là `t_decode` (ms, P50/P95) của ảnh **đang xem** (không phải preload) trong kịch bản đó, đo
bằng `--perf-analyze` trên nhóm điều hướng không phải preload.

### S2 (Next liên tục, tốc độ vừa) — disk cache **bật** (mặc định)

| Worker | P50 t_decode | P95 t_decode | Ghi chú |
|---:|---:|---:|---|
| 0  | — | — | |
| 2  | — | — | |
| 4  | — | — | |
| 8 (mặc định, không đặt biến) | — | — | |
| 12 | — | — | |

### S2 — disk cache **tắt** (`PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1`)

| Worker | P50 t_decode | P95 t_decode | Ghi chú |
|---:|---:|---:|---|
| 0  | — | — | |
| 2  | — | — | |
| 4  | — | — | |
| 8  | — | — | |
| 12 | — | — | |

### S3 (giữ phím ~10s, tốc độ cao) — disk cache **bật**

| Worker | P50 t_decode | P95 t_decode | Ghi chú |
|---:|---:|---:|---|
| 0  | — | — | |
| 2  | — | — | |
| 4  | — | — | |
| 8 (mặc định) | — | — | |
| 12 | — | — | |

### S3 — disk cache **tắt**

| Worker | P50 t_decode | P95 t_decode | Ghi chú |
|---:|---:|---:|---|
| 0  | — | — | |
| 2  | — | — | |
| 4  | — | — | |
| 8  | — | — | |
| 12 | — | — | |

Cột phụ nên thu thập cùng lúc (không bắt buộc có bảng riêng, ghi kèm trong nhật ký D07):
- Số lần `PreloadPaused` (tạm dừng vì thiếu RAM) mỗi lượt — cho biết worker cao có gây áp lực bộ
  nhớ hay không, độc lập với tranh chấp CPU/đĩa.
- `t_disk`, `t_read`, `t_decode` trung bình mỗi ảnh (cho R-DISK) khi disk cache bật.
- GC gen0/1/2 count và % time in GC mỗi lượt (chéo tham chiếu R-GC, D09).

## 3. Cách đọc kết quả theo R-CONT và R-DISK

### R-CONT (plan mục 8)

> Điều kiện: `t_decode` của ảnh đang xem với 8 worker ≥ 1,3 × `t_decode` với 0 worker.

- So sánh cột "8 (mặc định)" với cột "0" trong **cùng** bảng (cùng kịch bản, cùng trạng thái disk
  cache) ở trên. Dùng P50 làm số chính; nếu P50 gần nhau nhưng P95 lệch ≥ 1,3× thì ghi rõ là
  "R-CONT chỉ đúng ở đuôi phân phối" thay vì áp dụng ngưỡng nguyên văn cho P50.
  Không so P50 của bảng này với P95 của bảng khác.
- Nếu đúng: preload đang tranh CPU/decode với ảnh người dùng đang xem thật. Dùng thêm các cột 2/4/12
  worker để ước lượng đường cong (tuyến tính hay có điểm gãy ở một mức worker cụ thể) — hữu ích cho
  D12 chọn số worker mặc định mới thay vì chỉ kết luận đúng/sai nhị phân.
- Nếu sai (P95 8-worker < 1,3× P95 0-worker): số worker hiện tại (8) không phải nút thắt chính;
  không hạ ưu tiên việc giảm worker mặc định chỉ dựa trên bảng này — kiểm tra thêm R-GC (áp lực bộ
  nhớ/GC do nhiều bitmap preload cùng lúc) trước khi kết luận "an toàn giữ 8 worker".
- Baseline 0-worker cũng đồng thời là số liệu "preload tắt hoàn toàn" cho R-PRE (ảnh hưởng ngược
  lại: tỷ lệ RamHit giảm) — D07 nên ghi kèm CacheHit/DiskCacheHit/SourceRead rate ở hàng 0-worker để
  D12 cân đối hai quy tắc R-CONT và R-PRE thay vì chỉ tối ưu một phía.

### R-DISK (plan mục 8)

> Điều kiện: `t_disk` (đọc + decode từ PNG cache) ≥ `t_read + t_decode` (đọc + decode từ nguồn) của
> cùng ảnh.

- So sánh **trong cùng lượt disk-cache-bật**: với mỗi ảnh có cả `DiskCacheRead` (t_disk) và ít nhất
  một `SourceRead+Decode` (t_read+t_decode) — ví dụ ảnh vừa bị xoá RAM cache rồi mở lại, hoặc ảnh
  decode lần đầu (source) so với lần preload lại từ disk cache sau khi RAM cache bị đầy. Nếu
  `--perf-analyze` (D11) không tự ghép được cặp này cho cùng ảnh, dùng số liệu tổng hợp D05
  (`docs/refactoring/diagnosis/io-decode-split.md` mục 5, kết luận H9) làm tham chiếu chéo thay vì
  đo lại từ đầu — H9 đã cho thấy ngưỡng hoà vốn phụ thuộc bộ ảnh (t_decode nguồn @2560 ≈ 100–150 ms).
- Nếu đúng (disk cache PNG chậm hơn hoặc bằng decode nguồn): so sánh thêm cột "disk cache tắt" ở
  cùng worker — nếu `t_decode` (không có disk cache) ở mức tương đương hoặc thấp hơn tổng
  `t_disk` (có disk cache) tại cùng worker/kịch bản, đó là bằng chứng trực tiếp trên máy đo cho việc
  tắt hoặc đổi định dạng disk cache (theo R-DISK, hành động đề xuất: JPEG q95/BGRA thô).
- Nếu sai: giữ disk cache PNG, nhưng vẫn đối chiếu với H9 theo từng bộ ảnh (F1–F4) — R-DISK có thể
  đúng cho bộ ảnh nhẹ (t_decode nguồn thấp) và sai cho bộ ảnh nặng cùng lúc; không kết luận toàn cục
  chỉ từ một bộ fixture.
- Số worker (0/2/4/8/12) ảnh hưởng gián tiếp tới R-DISK qua tranh chấp đĩa: nhiều worker cùng ghi
  PNG cache (nền, `PersistWorkerCount = 2` cố định, không đổi theo `PHOTOREVIEW_DIAG_PRELOAD_WORKERS`)
  có thể làm `t_disk` của ảnh đang xem tăng do tranh I/O — nếu quan sát thấy `t_disk` tăng theo số
  worker preload dù số worker ghi đĩa không đổi, ghi nhận đây là bằng chứng bổ sung cho R-CONT (tranh
  chấp tài nguyên nói chung), không chỉ R-DISK.

## 4. Giới hạn đã biết

- Giống D05/D06: máy đo là máy dùng chung, không đảm bảo cold-OS-cache; không có bước làm trống
  standby list (cần RAMMap + admin, D07/D08 tự làm nếu người dùng đồng ý).
- `PHOTOREVIEW_DIAG_PRELOAD_WORKERS` chỉ đổi `PreloadScheduler`; nó không đổi `PersistWorkerCount`
  (số worker ghi disk cache nền, cố định = 2 trong `PreviewImageService`) — xem mục 3 ở trên.
- Ảnh hưởng của việc bật `PHOTOREVIEW_PERF_TRACE` (ghi CSV) lên chính số đo `t_*` chưa được cô lập
  (giống hạn chế đã ghi ở D01 với `AppLog`); coi số tuyệt đối là gần đúng, ưu tiên so sánh tương đối
  giữa các cột trong cùng bảng.
