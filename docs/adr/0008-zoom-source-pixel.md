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

## Bổ sung (R4): mỗi bitmap đang hiển thị có một kích thước Original riêng

- **Vấn đề:** với RAW, preview báo Original = kích thước camera nhìn thấy (Exif / JPEG nhúng), còn LibRaw giải mã vùng active của cảm biến, thường lớn hơn vài pixel (+0,2..0,8 % mỗi trục; Canon R6 nhỏ hơn 1 px). Nếu vẫn dùng kích thước preview cho bitmap đã giải mã thì bitmap bị kéo giãn (tỉ lệ nội dung lệch đến 0,8 %, viền lộ ra) và 100 % không còn là đúng 1 pixel của bitmap.
- **Mô hình:** một Original cho mỗi bitmap. Preview hiển thị với Original của preview (kích thước reader); original đã giải mã hiển thị với **`PixelWidth x PixelHeight` của chính nó** (`ZoomDetailLoader`). Fit luôn dùng preview nên bố cục Fit và `FitZoom` không đổi; về Fit thì trả lại Original của preview, zoom lại thì dùng lại original đang giữ (kích thước của nó). Ảnh không phải RAW (kích thước giải mã == Original) không đổi gì.
- **Giữ nguyên phần trăm zoom:** `ViewerState.SwapSourceSize` chỉ đổi kích thước nguồn, `Zoom` giữ nguyên. Ngoại lệ: zoom do Fit width/Fit height thì tính lại theo kích thước mới để chiều đã fit vẫn lấp đầy viewport (không lộ thanh cuộn thừa).
- **Giữ nguyên vùng đang xem:** trước khi đổi kích thước (ngoài Fit) `ViewerState.SourceSizeSwapping` được phát; `PointerInputController` lấy điểm ảnh ở tâm viewport (tọa độ chuẩn hóa) rồi cuộn lại sau lượt layout, nên nội dung lệch tối đa 1-2 px thay vì (offset x độ đổi kích thước), có thể hàng chục px khi zoom sâu. Bỏ qua nếu có thao tác viewport mới hơn hoặc một zoom đang chờ layout (zoom đó tự neo).
- **Phân biệt swap và ảnh mới:** `ImagePresenter.IsSameSourceSwap` chỉ bật trong lúc báo swap của `ZoomDetailLoader`; ảnh mới luôn đi qua `SetSourceSize` (không neo, không tính lại).
- **Cache:** `_originalDimensions` khóa theo `ImageSourceKind`; giải mã RAW đầy đủ dùng khóa `RawFullDecode` riêng nên kích thước LibRaw không ghi đè kích thước của preview (có test). PhotoInfo và dòng "RAW preview WxH" giữ nguyên (mô tả tệp/preview, không phải bố cục).
- **Không thể nhất quán tuyệt đối:** lúc swap phần tử ảnh đổi 1-2 px (chấp nhận); nội dung viền active-area thêm vào không được căn theo camera crop (LibRaw không cho biết crop).

