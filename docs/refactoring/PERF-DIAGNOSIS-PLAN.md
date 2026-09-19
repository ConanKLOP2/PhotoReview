# PhotoReview — Plan chẩn đoán hiệu năng (tìm nút thắt)

- **Ngày lập:** 2026-09-16 · **Nhánh plan:** `refactor/plan-b-c3`
- **Danh sách task:** [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md) (D00–D13)
- **Liên quan:** [`REFACTOR-PLAN.md`](REFACTOR-PLAN.md) mục 3.2, 6 và các task T01, T65, T66, T71, WC3
- **Trạng thái:** `CHỜ XÁC NHẬN`. Các task chỉ đo, không sửa code (D00, D01, D02, D07–D09, D11–D12), được làm ngay sau khi người dùng duyệt plan. Task thêm instrumentation (D03–D06, D10) phải theo quy trình plan/commit của `AGENTS.md`.

---

## 1. Mục tiêu

Trả lời bằng **số liệu**: trong mỗi kịch bản review, thời gian từ lúc nhấn phím đến lúc ảnh hiện trên màn hình (key→present) được dùng vào đâu. Từ đó chọn hướng xử lý theo bảng dưới:

| Nếu phần lớn thời gian nằm ở… | Nên làm | Task refactor liên quan |
|---|---|---|
| Đọc đĩa / đọc lặp một file nhiều lần | R-4 (tầng RAM giữ byte nguồn), R-3/R-6 | T63, T64, T65 |
| Giải mã JPEG | C3 (thay decoder) | T82, T85, T86, T87 |
| Gán ảnh vào UI, render ảnh lớn, zoom giật | Chất lượng scale, kích thước bitmap, cân nhắc C1 | T88, T71 |
| Ảnh đã cache thì nhanh, chỉ chậm khi chưa có cache | Tối ưu preload, không cần đổi framework | T64, T65 (+ task mới D12) |

Ngoài 4 nhóm trên, đợt đo này cũng kiểm tra các nguyên nhân phụ: UI thread bị chặn, tranh chấp CPU/I/O giữa preload và ảnh đang xem, GC, preload bị dừng vì áp lực bộ nhớ, disk cache PNG chậm, và Explorer COM làm chậm ảnh đầu tiên.

**Không phải mục tiêu:** sửa hiệu năng. Đợt này chỉ đo, kết luận và sắp lại thứ tự ưu tiên.

---

## 2. Hiện trạng công cụ đo

| Công cụ có sẵn | Đo được | Thiếu |
|---|---|---|
| `ReviewMetrics` + Diagnostics window | Tổng: cache hit/miss, source reads/bytes, decode ms, present ms, preload hit, inflight join, disk hit, queue wait, UI assign | Chỉ là tổng dồn, không theo từng lần điều hướng. Không tách đọc với decode. Không có render và độ trễ input |
| `AppLog` (khi bật `LoggingEnabled`) | Timestamp ms cho `ShowImage start / cache-state / thumbnail-presented / preview-presented`, Explorer, preload progress/paused | Không có mốc decode xong hay render. Bản thân việc ghi log cũng tốn thời gian |
| CLI `--benchmark*` | Profile workload qua `BenchmarkImageExecutor` (không có UI thật) | Không có WPF render hay input |
| CLI `--ui-next-probe` | `ShowImageAsync` trên cache nóng (30 ảnh, cửa sổ thu nhỏ) | Không tính render. Chỉ đo trường hợp warm. Gọi method qua reflection |
| CLI `--preload-bench` | Decode song song và lookup LRU | Không có UI |

**Kết luận:** cần thêm (a) trace theo từng lần điều hướng, gắn token, chia theo giai đoạn, (b) mốc render, (c) chế độ tách đọc với decode, (d) công cụ tự động chạy kịch bản bằng input thật, (e) công cụ ngoài (ETW) để xác nhận.

---

## 3. Mô hình thời gian của một lần điều hướng

```text
e.Timestamp (OS nhận phím)
 │ t_input        : chờ UI thread rảnh (Dispatcher đang bận: stat, session save, layout…)
 ▼ KeyDown handler bắt đầu
 │ t_pre          : xử lý phím → gọi ShowImageAsync
 ▼ ShowImage start (token N)
 │ t_stat         : FileInfo + tạo ImageCacheKey (UI thread)
 │ t_lookup       : tra RAM LRU
 ├─[RAM hit]──────────────────────────────────────────────┐
 ├─[Preview + miss] t_thumb : thumbnail (RAM/disk/decode 800px) → assign → render (first visual)
 ├─[in-flight]   t_join    : chờ decode do preload khởi động
 ├─[disk cache]  t_disk    : mở + decode PNG trong %LOCALAPPDATA%\PhotoReview\cache
 └─[source]      t_open + t_read + t_decode (+ fallback full-res) + t_verify (MatchesCurrentSource)
 ▼                                                        │
 │ t_assign       : MainImage.Source = image (UiAssign)   ◄┘
 │ t_render       : assign → CompositionTarget.Rendering kế tiếp (→ PresentMon xác nhận frame)
 ▼ PRESENTED (perceived) ── key→present = e.Timestamp → mốc này
 │ t_post         : preload kick, compare/hash, GetOriginalDimensions (mở file lần nữa), FileInfo, SessionStore.Save (đồng bộ)
 ▼ ShowImage complete (post-work vẫn có thể chặn phím kế tiếp → t_input của lần sau)
```

**Phân loại mỗi lần điều hướng:** `RamHit` · `InflightJoin` · `DiskCacheHit` · `SourceMiss` · `Thumbnail+X`.
**Hai chỉ số cảm nhận:** `first-visual` (khung hình đầu tiên có ảnh, kể cả thumbnail) và `final-visual` (ảnh chất lượng đích đã được render).

---

## 4. Giả thuyết cần kiểm chứng

| ID | Giả thuyết | Dấu hiệu nếu đúng | Cách kiểm |
|---|---|---|---|
| H1 | Preview mode tốn gấp đôi: thumbnail rồi mới đến adaptive decode | `t_thumb` lớn, source bị mở 2 lần | D02, D04, so sánh mode (D07) |
| H2 | `GetOriginalDimensionsAsync` mở lại file sau mỗi lần Next | Số lần mở file mỗi ảnh ≥ 2 ở Fast/Preview | D02 |
| H3 | `SessionStore.Save` đồng bộ trên UI làm tăng `t_input` của phím kế tiếp | Có dispatcher op dài trùng lúc ghi session, `t_input` tăng khi giữ phím | D04, D08 |
| H4 | 8 preload worker tranh CPU/I/O với ảnh đang xem | `t_decode` của ảnh xem tăng khi preload bận | D10 |
| H5 | Preload dừng vì memory load ≥ 80% nên cache miss nhiều | Log `Preload paused for memory`, miss rate tăng theo thời gian | D01, D09 |
| H6 | Ngưỡng full-folder so byte nén với dung lượng decoded nên quyết định preload sai | Folder < 16 GB nén nhưng decoded > capacity dẫn tới evict/thrash | D09 + tính toán |
| H7 | Original mode (bitmap 50 MP, ~200 MB) làm render/assign chậm | `t_assign + t_render` lớn ở Original, frame time khi zoom > 33 ms | D04, D08 (PresentMon) |
| H8 | `DecodePixelWidth` không giảm đáng kể thời gian decode JPEG (decode full rồi mới scale) | Thời gian decode gần như không đổi theo target width | D05 |
| H9 | Đọc PNG disk cache chậm hơn decode lại JPEG nguồn | `t_disk` > `t_read + t_decode` | D05 |
| H10 | LRU evict rồi lại decode (thrash) khi folder lớn | Cùng key bị miss nhiều lần trong một phiên | D04 (đếm miss theo key) |
| H11 | GC gen2 / LOH gây khựng | % time in GC cao, gen2 trùng lúc điều hướng chậm | D09 |
| H12 | `FileInfo` trên UI thread chậm với ổ mạng/USB | `t_stat` lớn trên ổ chậm | D04, D07 (ổ khác) |
| H13 | Explorer snapshot đến muộn gây reindex và restart preload, hoặc chặn T2 khi mở file | Khoảng T0→T2 dài khi mở file, preload bị cancel | D01, D04 |
| H14 | Antivirus quét mỗi lần mở file | `t_open` lớn, stack có `MsMpEng` | D08 (chỉ quan sát, **không** tắt AV) |
| H15 | Thứ tự preload không theo hướng điều hướng nên Prev hay nhảy trang bị miss | Miss rate S4 cao hơn hẳn S2 | D07 |

---

## 5. Kịch bản đo (ma trận)

| ID | Kịch bản | Mô tả | Chỉ số chính |
|---|---|---|---|
| S1 | Mở folder | App lạnh, mở folder và mở file (2 biến thể) | T0→T1→T2→T3 |
| S2 | Next nhịp người | 100 lần Next, cách nhau 1,5 s | key→present P50/P95, hit rate |
| S3 | Next dồn dập | Giữ phím, khoảng 30 phím/s, 200 lần | Như S2 + `t_input`, số frame bị bỏ |
| S4 | Nhảy | Prev 20 lần sau S2; Home; nhảy ngẫu nhiên 30 lần | Miss rate, key→present |
| S5 | Mode | S2 lặp cho Fast / Preview / Original | Tách theo mode |
| S6 | Zoom | Ảnh 50 MP: Fit → 100% → 200% → 400% → pan | Frame time (PresentMon), `t_render` |
| S7 | Compare | 20 cặp `a.jpg` / `a (1).jpg`, hash bật | `t_post`, UI block |
| S8 | File action | 50 lần Move (Enter) liên tiếp | key→present của ảnh kế tiếp, `t_input` |
| S9 | Folder lớn | 5.000 ảnh, và folder > 16 GB (nén) | RAM, memory pause, miss rate theo thời gian |
| S10 | Ổ chậm (nếu có) | S2 trên USB/HDD/ổ mạng | `t_open`, `t_read`, `t_stat` |

**Điều kiện mỗi lượt chạy** (ghi lại đầy đủ):
- **Cold app:** process mới.
- **Cold disk cache:** xóa `%LOCALAPPDATA%\PhotoReview\cache` và `thumbnails`.
- **Cold OS cache:** reboot, hoặc người dùng tự chạy RAMMap → Empty Standby List (cần admin; agent **không** tự làm).
- **Warm:** chạy lại ngay sau lượt trước.

**Mỗi ô ma trận chạy 3 lần.** Cửa sổ 1920×1080 ở trạng thái Normal, cùng màn hình và cùng DPI. Power plan được ghi lại, không tự đổi. Ghi trạng thái Defender, không thay đổi.

**Fixture** (không commit ảnh thật; đường dẫn ghi trong `docs/refactoring/diagnosis/env.md`):
- F1: 300 JPEG 20–24 MP
- F2: 100 JPEG 45–50 MP
- F3: 50 PNG/TIFF
- F4: 5.000 JPEG nhỏ
- F5: folder > 16 GB
- F6: 20 cặp compare
- F7: ảnh chụp dọc có EXIF orientation

---

## 6. Instrumentation (chỉ bật khi cần)

- **`PhotoReviewPerf` EventSource** (tên provider `PhotoReview-Perf`):
  - Chi phí gần như bằng 0 khi không có listener.
  - Thu được bằng `dotnet-trace`, PerfView, WPR (`+PhotoReview-Perf`) hoặc listener CSV trong app.
- **Listener CSV trong app:** chỉ bật khi đặt biến môi trường `PHOTOREVIEW_PERF_TRACE=<thư mục>`. **Không** đổi schema `config.json`.
  - Ghi file `perf-<pid>-<ts>.csv` trên writer thread riêng, qua hàng đợi có giới hạn.
- **Biến môi trường chẩn đoán** (chỉ đọc lúc khởi động; mặc định không đổi hành vi):
  - `PHOTOREVIEW_DIAG_PREREAD=1`: đọc cả file vào `MemoryStream` (có đo giờ) trước khi decode, để tách `t_read` và `t_decode`. Chế độ này **thay đổi pattern I/O**, nên chỉ dùng để đo, và cũng dùng để ước lượng lợi ích của R-4.
  - `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=<0..16>`: ghi đè số preload worker.
  - `PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1`: bỏ qua đọc và ghi disk cache preview.
- **Danh sách event** đều mang `nav` (token navigation), `path` (hash ngắn, không ghi đường dẫn đầy đủ khi xuất báo cáo) và `ms` hoặc `ticks`:
  - `KeyInput(key, inputDelayMs)`
  - `ShowStart(index, mode)`, `Stat(ms)`, `Lookup(result)`
  - `ThumbStart/End(source=ram|disk|decode)`
  - `JoinStart/End`
  - `DiskCacheRead(ms, bytes)`, `SourceOpen(ms)`, `SourceRead(ms, bytes)`, `Decode(ms, targetWidth, downscaled, fallback)`, `Verify(ms)`
  - `Assign(ms, pixelW, pixelH)`, `Rendered(msSinceAssign)`, `Presented(kind)`
  - `PostStart/End(part=compare|hash|dims|session|preloadKick)`
  - `PreloadItem(worker, queueWaitMs, kind, ms)`, `PreloadPaused(loadPct, availMB)`, `PreloadCancel(reason)`
  - `DispatcherLongOp(ms, priority, name)` (qua `Dispatcher.Hooks`, ngưỡng 16 ms)
  - `Folder(T0..T3 phase, ms)`
  - `Gc(gen)`: lấy từ runtime events, không tự phát
- **Mốc render:** sau khi gán `Source`, đăng ký `CompositionTarget.Rendering` một lần và ghi `Rendered`. Đây là mốc xấp xỉ lúc WPF compose frame. Dùng PresentMon (D08) để hiệu chỉnh độ lệch so với lúc DWM thực sự present.
- **Độ trễ input:** `Environment.TickCount - e.Timestamp` ngay đầu `Window_KeyDown` (cả hai đều là ms tick của hệ thống, cần xác minh trong D04).

**Ràng buộc:**
- Không đổi thứ tự lệnh hay hành vi khi biến môi trường không được đặt.
- `Stopwatch` và việc dựng chuỗi chỉ chạy khi `PhotoReviewPerf.Log.IsEnabled()`.
- Mọi event này phải được giữ qua đợt refactor (ràng buộc **K-4** trong `REFACTOR-PLAN.md`).

---

## 7. Công cụ ngoài

| Công cụ | Dùng cho | Ghi chú |
|---|---|---|
| Process Monitor (Sysinternals) | Đếm open/read theo file (H1, H2, H9) | Không cần sửa code |
| `dotnet-counters` | GC, working set, thread pool, % time in GC (H11, H5) | Cài bằng `dotnet tool install -g` |
| `dotnet-trace` / PerfView | CPU sampling và event `PhotoReview-Perf` | |
| WPR/WPA (Windows ADK) | CPU + File I/O + Disk I/O + GPU + stack WIC/milcore (H7, H8, H14) | Cần cài ADK |
| PresentMon | Frame time và độ trễ present (S3, S6) | |
| RAMMap | Làm trống standby list để đo cold OS cache | Cần admin; **người dùng tự thao tác** |

> **Quy tắc:** trước khi tải hoặc cài bất kỳ công cụ nào chưa có trên máy, agent phải hỏi người dùng (nêu tên, nguồn, dung lượng). Không thay đổi cài đặt bảo mật hay hệ thống (Defender, power plan, page file).

---

## 8. Phân tích và quy tắc quyết định

`--perf-analyze` (D11) gom CSV thành bảng: mỗi dòng là một lần điều hướng, cột là các `t_*`, loại điều hướng và kịch bản. Sau đó tính P50/P95/max và **tỷ trọng từng giai đoạn** tại P95 (lấy trung bình tỷ trọng trên nhóm điều hướng thuộc 10% chậm nhất).

| Quy tắc | Điều kiện (ngưỡng tạm, D12 được hiệu chỉnh) | Kết luận | Hành động |
|---|---|---|---|
| **R-IO** | Ở nhóm `SourceMiss`: `(t_open + t_read)` ≥ 40% tổng, **hoặc** trung bình ≥ 2 lần mở file nguồn mỗi ảnh | Nút thắt là đĩa hoặc đọc lặp | Ưu tiên T65 (R-4) và T63. Q5 → bật mặc định |
| **R-DEC** | Ở nhóm `SourceMiss`/`Preload`: `t_decode` ≥ 40% tổng | Nút thắt là decode | Ưu tiên WC3 (T82 trước). Nếu H8 đúng, thêm task chính sách target width |
| **R-UI** | Ở nhóm `RamHit`: `t_input + t_assign + t_render` ≥ 40% **hoặc** RamHit P95 > 50 ms, **hoặc** frame time P95 > 33 ms ở S6 | Nút thắt là UI/render | T88 trước, sau đó giảm kích thước bitmap hiển thị (không vượt viewport×DPI ở Fit), rồi ADR C1 (T71) có dữ liệu |
| **R-PRE** | RamHit P95 ≤ 50 ms **và** tỷ lệ không phải RamHit ở S2 ≥ 10% (hoặc ở S3/S4 ≥ 30%) | Cache nhanh nhưng phủ không đủ | T64, thêm task preload theo hướng và tốc độ điều hướng. Sửa H5 nếu có |
| **R-CONT** | `t_decode` của ảnh đang xem với 8 worker ≥ 1,3 × với 0 worker | Preload tranh tài nguyên với ảnh đang xem | Task mới: ưu tiên decode ảnh đang xem, tạm dừng preload khi giữ phím |
| **R-THREAD** | Có `DispatcherLongOp` > 16 ms lặp lại trong S2/S3, hoặc `t_input` P95 > 16 ms | UI thread bị chặn | Đẩy T61 (R-1) và T63 (R-3) lên trước |
| **R-GC** | % time in GC ≥ 10% trong S3/S9, hoặc gen2 trùng với 10% điều hướng chậm nhất | Áp lực GC | Task mới: pooling buffer, giảm LOH (liên quan T65) |
| **R-DISK** | `t_disk` ≥ `t_read + t_decode` của cùng ảnh | Disk cache PNG không có lợi | Task mới: đổi định dạng disk cache (JPEG q95 / BGRA thô) hoặc tắt |
| **R-FOLDER** | S1: T0→T2 > 1 s và phần lớn là Explorer/scan/stat | Mở folder chậm | Ưu tiên T44/T63, xem lại timeout Explorer |

Nhiều quy tắc có thể cùng đúng. Khi đó xếp hạng theo **mức đóng góp vào P95 của kịch bản S2+S3** (hai kịch bản sát trải nghiệm review nhất).

**Mục tiêu hiệu năng đề xuất** (người dùng xác nhận ở D12):

| Chỉ số | Mục tiêu |
|---|---|
| RamHit key→final-visual | P95 ≤ 50 ms |
| SourceMiss key→final-visual (24 MP, viewport 1920) | P95 ≤ 300 ms |
| first-visual khi miss | P95 ≤ 120 ms |
| S1 T0→T2 (mở folder) | ≤ 500 ms |
| Frame time khi zoom/pan | P95 ≤ 33 ms |

---

## 9. Đầu ra

```text
docs/refactoring/diagnosis/
  env.md                 máy, ổ đĩa, OS, driver GPU, màn hình/DPI, power plan, AV, fixture
  log-baseline.md        D01
  file-access.md         D02
  io-decode-split.md     D05
  runs/<date>/<scenario>-<mode>-<cond>-<n>/   CSV + metrics.json + counters.csv (không commit nếu > 5 MB; chỉ commit summary)
  etw-findings.md        D08
  gc-memory.md           D09
  contention.md          D10
  REPORT.md              D12: bảng tỷ trọng, quy tắc nào đúng, xếp hạng, đề xuất thay đổi thứ tự task
```

Không commit đường dẫn ảnh hay thông tin cá nhân. Trong báo cáo, path được thay bằng hash hoặc số thứ tự.

---

## 10. Rủi ro và rollback

| Rủi ro | Giảm thiểu | Rollback |
|---|---|---|
| Instrumentation làm sai lệch số đo (observer effect) | Chỉ tính toán khi `IsEnabled`, listener ghi bất đồng bộ; so key→present khi có và không có trace (D07 chạy 1 lượt không trace) | Tắt biến môi trường |
| Instrumentation vô tình đổi hành vi | Test hiện có phải đạt; thêm test "không đặt biến môi trường thì không có event hay file"; review diff | Revert commit D03–D06 |
| Chế độ `PREREAD` bị bật nhầm trong production | Chỉ đọc biến môi trường; log cảnh báo khi bật; Diagnostics hiển thị "DIAG MODE" | Xóa biến môi trường |
| Kết quả không lặp lại được | 3 lần mỗi ô, ghi đủ điều kiện, báo phương sai | Chạy lại |
| Cần công cụ hoặc thao tác quyền admin | Hỏi người dùng; người dùng tự thao tác | Bỏ bước đó, ghi rõ trong báo cáo |
| Xung đột với refactor (cùng sửa `MainWindow` và `PreviewImageService`) | Nhánh D chạy **trước** W1 code (T14a phụ thuộc D06, D10) | Rebase |

---

## 11. Thứ tự

```text
T00 ─► D00 ─┬─► D01 ∥ D02                      (không sửa code)
            └─► D03 ─► D04 ─┬─► D05 ─┐
                            ├─► D10 ─┤
                            └─► D06 ─┴─► D07 ─┬─► D08 ∥ D09 ─┐
                                              └─► D11 ───────┴─► D12 ─► D13
T14a (refactor) chờ D05, D06, D10 merge vào refactor/integration.
T01 được thay bằng D07 (lượt baseline) + D12.
```
