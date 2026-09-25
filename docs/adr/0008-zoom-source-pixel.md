# ADR 0008: Zoom theo pixel nguồn — 100 % = 1 pixel nguồn, original giải mã theo yêu cầu (Q-Z1)

- **Trạng thái:** Chấp nhận (Accepted), 2026-09-24 — quyết định của người dùng (Q-Z1, phương án A).
- **Triển khai:** PR #43 (`ccaa909`, giải mã preview theo viewport box) và PR #47 (`7721a75`, zoom theo pixel gốc + `ZoomDetailLoader`).

## Bối cảnh

Preview được giải mã đến kích thước viewport (#43) để giảm pixel giải mã và RAM (ảnh dọc −85 % pixel, peak working set −70 %). Khi đó "100 %" tính theo bitmap đang hiển thị sẽ không còn nghĩa là kích thước thật của ảnh, và zoom sẽ mờ. Câu hỏi Q-Z1: 100 % là pixel của preview hay pixel nguồn, và có giải mã original khi zoom không.

## Quyết định (phương án A)

- **100 % = 1 pixel nguồn trên 1 pixel thiết bị.** `ViewerState.Zoom` tính theo pixel GỐC; phần tử ảnh có kích thước `OriginalWidth × Zoom / DpiScale` DIP, không phụ thuộc bitmap đang hiển thị.
- Preview vẫn được giải mã theo viewport box và hiển thị ngay ở đúng kích thước đó.
- Original được giải mã **theo yêu cầu, chỉ cho ảnh hiện tại** (`ZoomDetailLoader` → `PreviewImageService.DecodeOriginalAsync`, thread riêng), rồi thay `Source` mà không đổi layout/scroll.
- Original **không** vào RAM LRU và **không** vào disk cache.
- Điều hướng hủy decode chưa chạy, bỏ kết quả trễ (token navigation) và thả original. Về Fit thì dùng lại preview.

## Hệ quả

- Bộ nhớ cho zoom bị chặn ở **một original** (của ảnh hiện tại), phù hợp nguyên tắc RAM/tốc độ trong `AGENTS.md`.
- Zoom vào ảnh chưa có original có độ trễ giải mã (đo với ảnh 24 MP trong #47); preview hiển thị trước nên không có khung trống.
- Mỗi lần quay lại zoom trên cùng ảnh sau khi điều hướng đi sẽ giải mã lại original (chấp nhận, đổi lấy RAM giới hạn).

## Tham chiếu

- [`docs/architecture.md`](../architecture.md) — mục "Luồng decoder và cache", đoạn Zoom.
- [`docs/refactoring/PERF-STATUS.md`](../refactoring/PERF-STATUS.md) — số đo perf night và zoom-to-sharp.
- [`docs/refactoring/OPEN-DECISIONS.md`](../refactoring/OPEN-DECISIONS.md) — Q-Z1.
