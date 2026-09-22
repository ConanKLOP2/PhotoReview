# PhotoReview — Plan chẩn đoán hiệu năng — ARCHIVED

**Ngày lập:** 2026-09-16 · **Nhánh plan:** `refactor/plan-b-c3`  
**Danh sách task:** [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md) (D00–D13)  
**Liên quan:** [`REFACTOR-PLAN.md`](REFACTOR-PLAN.md) mục 3.2, 6

**Status:** Completed. Tasks D00–D13 run before major refactoring (REFACTOR W1–W7) to identify performance bottlenecks and guide optimization priority.

---

## Mục tiêu

Measure **key→present latency** in each review scenario:

| Nếu thời gian chủ yếu ở… | Hành động | Task liên quan |
|---|---|---|
| Đọc đĩa / file lặp | R-4 (RAM layer `SourceBytesCache`) | T63–T65 |
| Giải mã JPEG | C3 (swap decoder) | T82, T85–T87 |
| Gán UI / render / zoom | Chất lượng scale, kích thước bitmap | T88, T71 |
| Cache đã có → nhanh | Tối ưu preload | T64–T65 + thêm T mới D12 |

Cũng kiểm tra: UI thread chặn, tranh chấp CPU/I/O, GC, preload dừng từ áp lực RAM, PNG disk cache, Explorer COM.

**Không sửa hiệu năng lần này** — chỉ đo, kết luận, sắp lại thứ tự.

---

## Thời gian một lần điều hướng (mô hình)

```text
e.Timestamp (OS key) → t_input (wait dispatcher)
  → KeyDown handler → t_pre (key→ShowImageAsync)
  → ShowImage start (token N) → t_stat (FileInfo + cache key)
  → t_lookup (LRU query) → (cache hit → t_present, cache miss → ...)
  → t_thumbnail (if needed) → t_preview (adaptive) → ...
```

Các công cụ có sẵn: `ReviewMetrics`, `AppLog`, CLI `--benchmark*`, `--ui-next-probe`, `--preload-bench`. Thiếu: trace per-navigation, render mốc, input latency, tách read/decode, công cụ tự động script.

---

**Implementation:** xem [`PERF-DIAGNOSIS-TASKS.md`](PERF-DIAGNOSIS-TASKS.md) (D00–D13, hoàn thành lâu rồi).  
**Results & decision:** see `task-log-2026-09-21.md` and [`../../ACTIVE-TASKS.md`](../../ACTIVE-TASKS.md) (D tasks).
