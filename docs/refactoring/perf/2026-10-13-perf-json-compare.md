# Kết quả đo JSON có schema và `compare` (2026-10-13)

Bước 3-4 của kế hoạch "chứng minh bằng số". Không đổi hành vi app: chỉ `tools/PhotoReview.Benchmark.Cli` (thư mục `PerfCompare/`), script `tools/diag/perf-compare.ps1`, `tools/diag/perf-gate.ps1`, cờ `-PerfGate` của `verify-all.ps1`.

## Schema `photoreview.perf-result` v1

| Trường | Ý nghĩa |
|---|---|
| `schema`, `schemaVersion` | `photoreview.perf-result`, 1 (phiên bản khác bị từ chối, mã thoát 2) |
| `scenario` | `startup-probe` hoặc `perf-session:<tên kịch bản>` (khác scenario thì không so sánh) |
| `machine` | `name`, `cpu`, `logicalCores`, `ramGb`, `os`, `gpu` |
| `commit`, `dirty`, `appVersion`, `createdUtc` | git short SHA (12), cây làm việc có sửa, phiên bản CLI, UTC |
| `runs`, `warmupRuns`, `parameters` | số vòng đo/khởi động; `count`, `keys`, `seed`, `apps`... (khác nhau thì kết luận hạ xuống INCONCLUSIVE) |
| `background` | `cpuPercentMedian/Max`, `cpuSamples`, `noiseFlags` (`busy-machine`, `failed-runs`, `forced-kill`) |
| `metrics[]` | `name` (`shell.firstFrameMs`, `wpf.peakWorkingSetMb`, `gcGen2Count`...), `unit` (ms/MB/count/%), `direction` (`lowerIsBetter`/`higherIsBetter`), `n`, `p50`, `p95`, `min`, `max`, `mean`, `mad`, `samples[]` |

startup-probe: `firstFrameMs`, `windowShownMs`, `windowRevealedMs`, `keyToFrameCachedP50Ms/P95Ms` (mỗi mẫu = một lần chạy), `peakWorkingSetMb`, `keysWithoutFrame`, mỗi ứng dụng một tiền tố. perf-session (`--perf-export`): `peakWorkingSetMb`, `workingSetMb`, `gcGen0/1/2Count`, `gcPauseMs`, `cpuMs`, `elapsedMs`, `keySettleP50/P95Ms`, `keyDispatchP95Ms`, `keyUiBusyP95Ms`, `sourceReads`, `sourceBytesReadMb`, `decodeMs`, `cacheMisses`, `presentMs` (mỗi mẫu = một lần lặp; tải CPU máy không có trong đầu ra này nên `cpuPercentMedian` là null).

## Quy tắc so sánh

1. Mỗi chỉ số: bỏ ngoại lai PHÍA XẤU bằng MAD (`median + 3 * 1,4826 * MAD`, cần >= 5 mẫu; `ProbeStatistics` của WP-29).
2. `ngưỡng = max(5 % * median baseline, sàn tuyệt đối)`; sàn 1 ms / 16 MB / 1 đếm / 2 %.
3. `worse = median hiện tại - median baseline` (đã đổi dấu cho chỉ số `higherIsBetter`); sai số chuẩn của hiệu `1,2533 * sqrt(sigmaB^2/nB + sigmaC^2/nC)`, `sigma = 1,4826 * MAD`; khoảng tin cậy 95 % = `worse +- 1,96 * se`.
4. REGRESSION khi cận dưới > ngưỡng; WARN khi `worse` > ngưỡng nhưng cận dưới <= ngưỡng, hoặc trôi (> nửa ngưỡng và cận dưới > 0, không áp dụng khi ngưỡng do sàn tuyệt đối quyết định); còn lại PASS (ghi "improved" khi tốt hơn quá ngưỡng).
5. REGRESSION/WARN bị hạ xuống INCONCLUSIVE (kèm lý do) khi: CPU máy median > 60 % ở một trong hai tệp, cờ `failed-runs`/`forced-kill`, khác máy (tên/CPU/số lõi) hoặc tham số, độ lệch (1,4826 * MAD) > 25 % median. PASS giữ nguyên PASS. Mẫu < 3 hoặc thiếu chỉ số trong bản hiện tại = INCONCLUSIVE; chỉ số mới = PASS.
6. Mã thoát: 0 PASS/WARN, 1 REGRESSION, 2 lỗi dùng/schema/scenario, 3 INCONCLUSIVE.

## Chạy thật (máy bận, đo như tìm thấy)

Bản publish R2R của shell và WPF từ master `c3eb91d5` + nhánh này, `--startup-probe --count 10 --rounds 6 --warmup 1 --keys 12`, fixture F4 (2705 jpg), CPU máy trung vị 100 % (nhiều agent đang build). Ảnh đầu median: shell 1074 ms [853-1409], WPF 1492 ms [1421-1547]; WS đỉnh 154 vs 175 MB (số tuyệt đối chỉ để minh hoạ, máy quá bận để ghi nhận hiệu năng).

| So sánh | Kết quả |
|---|---|
| tệp với chính nó | PASS, mã 0 (14 chỉ số, mọi hiệu = 0) |
| tệp sau khi nhân mọi mẫu x 1,2 (+20 %), ngưỡng mặc định | INCONCLUSIVE, mã 3: "machine busy (median CPU 100 % > 60 %)", thêm "high variance" (độ lệch 36-45 %) cho ảnh đầu/cửa sổ hiện của shell |
| như trên, `--busy-cpu 100 --max-cv 100` (bỏ phát hiện nhiễu) | REGRESSION, mã 1: 6 chỉ số REGRESSION (WS đỉnh +20 %, cửa sổ hiện/thấy được/ảnh đầu của WPF), 6 chỉ số WARN (khoảng tin cậy chạm dưới ngưỡng do phương sai lớn), 2 PASS (`keysWithoutFrame`) |
| gate thật (`perf-gate.ps1 -Rounds 4`) với baseline là lần đo trên | INCONCLUSIVE (máy bận), cảnh báo, mã 0; các chỉ số WPF PASS/"improved" |

Kết luận: bộ so sánh không kết luận hồi quy trên số đo nhiễu (đúng mục đích), và kết luận được khi nhiễu bị bỏ qua có chủ đích. Cần đo lại trên máy rảnh (< 60 % CPU) trước khi chốt baseline `tools/perf-baselines/<máy>.json`.
