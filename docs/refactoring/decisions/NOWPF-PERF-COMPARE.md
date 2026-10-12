---
id: NOWPF-PERF-COMPARE
order: 1070
summary: |-
  Kết quả đo JSON có schema versioned + verb `compare` (PASS/WARN/REGRESSION/INCONCLUSIVE, không báo hồi quy giả khi máy bận) + cổng tùy chọn `verify-all.ps1 -PerfGate`; baseline `tools/perf-baselines/<máy>.json` chỉ commit khi chủ dự án chốt (CHƯA XÁC NHẬN).
---

# NOWPF-PERF-COMPARE - "chứng minh bằng số" bước 3-4: kết quả đo có cấu trúc và so sánh (2026-10-13)

Nền: [NOWPF-WP29](NOWPF-WP29-PERF-PROBE.md) (probe xen kẽ, MAD), [2026-10-06 noise floor](../perf/2026-10-06-s0-noise-floor.md), [PERF-STATUS](../PERF-STATUS.md).
Chi tiết schema, quy tắc và lần chạy thật: [perf note](../perf/2026-10-13-perf-json-compare.md); cách chạy: [TESTING.md](../../TESTING.md).

## 1. Vấn đề

Mỗi lần đo ra CSV/Markdown riêng, so sánh bằng mắt; cùng một số liệu máy bận kết luận được "hồi quy" hay "nhanh hơn" tùy hứng. Cần: (a) một định dạng ổn định
để lưu/so sánh, (b) bộ so sánh biết nhiễu, (c) một cổng cục bộ tùy chọn.

## 2. Hành vi mới (không đổi hành vi app)

- `--startup-probe` ghi thêm `perf-result.json` (schema `photoreview.perf-result` v1) vào `--out` và `work/perf/*.json` (git-ignored) hoặc `--perf-json <file>`;
  `--perf-export <thư mục perf-session> [--out f]` gom kết quả `--perf-session` (GC, WS đỉnh, đọc tệp, decode, key settle).
- `compare <baseline> <current>` (hoặc `tools/diag/perf-compare.ps1`): MAD loại ngoại lai phía xấu, median so với `max(5 %, sàn tuyệt đối)` + khoảng tin cậy 95 %;
  mã thoát 0 PASS/WARN, 1 REGRESSION, 2 lỗi schema/dùng, 3 INCONCLUSIVE (CPU máy > 60 %, độ lệch > 25 %, mẫu < 3, thiếu chỉ số, khác máy/tham số).
- `verify-all.ps1 -PerfGate` -> `tools/diag/perf-gate.ps1`: đo bản publish bằng startup-probe và so với `tools/perf-baselines/<COMPUTERNAME>.json`;
  không có baseline thì bỏ qua (mã 0), INCONCLUSIVE là cảnh báo, chỉ REGRESSION/lỗi schema làm gate đỏ. Mặc định tắt, không có trong CI.

## 3. Kiểm chứng

- 51 test `PerfCompareTests` (Integration.Tests): bảng kịch bản (không đổi, cải thiện, hồi quy nhỏ trong nhiễu, hồi quy rõ, thiếu chỉ số, schema cũ/mới,
  máy bận, phương sai cao, higherIsBetter, sàn tuyệt đối, ngoại lai hai phía), CLI đầu-cuối với số liệu giả (mã thoát 0/1/2/3, `--json`, `--markdown`,
  tham số sai), builder từ probe/perf-session.
- Chạy thật startup-probe 6 vòng (máy 100 % CPU): tự so = PASS (mã 0); +20 % = INCONCLUSIVE (mã 3); +20 % với `--busy-cpu 100 --max-cv 100` = REGRESSION (mã 1).
- Mutation hai bước (26 đột biến thủ công trên `PerfComparer`/`PerfResult`/`PerfCompareCommand`/`PerfResultBuilder`): dấu ngưỡng, dấu hiệu `worse`, ranh giới
  `>`/`>=` của REGRESSION/WARN/bận CPU/số mẫu, Max->Min của ngưỡng, bỏ hướng `higherIsBetter`, tắt loại ngoại lai (baseline/current), tắt hạ xuống INCONCLUSIVE,
  đảo kiểm phương sai, mã thoát INCONCLUSIVE/`compare` luôn 0, Max->Min của kết quả tổng, cận dưới khoảng tin cậy `+`, thiếu chỉ số = PASS, trôi bỏ qua sàn,
  kiểm phiên bản schema, cờ `failed-runs`, builder giữ lần lỗi/cờ bận đảo, scenario/tham số khác không bị phát hiện, cờ `improved` đảo, sai số chuẩn = 0.
  Bước 1 (lọc `PerfCompareTests`): 25/26 bị diệt. Sống sót: ranh giới `worse > allowed` (M02, lần đầu tưởng bị diệt ở bước 2 nhưng đó là test Manual hỏng vì môi trường).
  Test lấp `DifferenceExactlyAtTheThreshold_IsNotARegression_ButIsFlaggedAsDrift` diệt M02 và M25 (`lower >= allowed`) ngay ở bước 1. Hai đột biến đầu
  (M06/M07) lỗi build do analyzer CA1859 và được chạy lại bằng `RemoveHighOutliers(x, 1e9)`: bị diệt. Tổng 26/26.
- Bước 2 lọc rộng: `PerfCompare|ShellHost|BenchmarkCli` loại `Category=Manual`/`Native`.

## 4. Quyết định (đề xuất, CHƯA XÁC NHẬN)

- CHƯA XÁC NHẬN: dùng bộ so sánh này làm cách duy nhất "chứng minh bằng số" cho PR perf; mặc định ngưỡng 5 %, CPU bận 60 %, độ lệch 25 %, mẫu >= 3.
- CHƯA XÁC NHẬN: chưa commit baseline nào (`tools/perf-baselines/<máy>.json`); đo lúc máy rảnh (< 60 % CPU) rồi `perf-gate.ps1 -UpdateBaseline` khi chủ dự án chốt.
  Phương án thay thế: (a) commit baseline ngay (số đo hôm nay là lúc máy 100 % CPU, không đáng tin); (b) baseline không commit, chỉ giữ `work/perf` cục bộ.
- Phương án thay thế cho chống nhiễu: INCONCLUSIVE làm gate đỏ (nghiêm hơn, dễ chặn nhầm trên máy chung) - không chọn.
- Chưa có: adapter cho `--decoder-bench`/`--benchmark` (chỉ có thống kê tóm tắt, không có mẫu từng lần), tải CPU máy cho `--perf-session`.
- Hoàn tác: xóa thư mục `tools/PhotoReview.Benchmark.Cli/PerfCompare`, hai script `tools/diag/perf-*.ps1`, cờ `-PerfGate`; không ảnh hưởng app/dữ liệu.
