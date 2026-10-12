---
id: NOWPF-WP35-G0R
order: 1060
summary: |-
  WP-35 GĐ0 (PR 2): tách logic cửa sổ Recovery khỏi WPF (RecoveryWindowPresenter, RecoveryPresentation, RecoveryRowModel trong App.Shared); không đổi thứ tự/điều kiện của kiểm tra nền và thao tác Retry/Clear.
---

# NOWPF-WP35-G0R - tách Recovery khỏi WPF (PR 2, 2026-10-13)

Thẻ: WP-35; tiếp [NOWPF-WP35-G0](NOWPF-WP35-G0-PRESENTERS.md) (PR 1) và [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md). An toàn dữ liệu: cửa sổ này gọi thao tác tệp thật
(Retry = di chuyển/sao chép, Clear = ghi journal), nên tách riêng và giữ NGUYÊN thứ tự/điều kiện; ADR 0003/0005/0007 không đổi.

## 1. Đã làm

| Phần | Trước | Sau |
|---|---|---|
| `RecoveryWindow.xaml.cs` (view WPF) | 363 dòng (hàng, bộ lọc, chi tiết, kiểm tra nền, Clear, Retry) | ~230 dòng: chép giá trị vào điều khiển, MessageBox, con trỏ chờ |
| `RecoveryPresenter.cs` (WPF, dùng `Brush`) | 249 dòng | xoá; thành `RecoveryPresentation` (App.Shared, thuần: văn bản + `RecoveryTone`) |
| mới `RecoveryWindowPresenter` (App.Shared) | | hàng, bộ lọc, `Details`, `RunChecksAsync`, `Dismiss`, `ExecuteRetryAsync`, `DescribeRetry`, `CanClose` |
| mới `RecoveryRowModel` | | tiêu đề/huy hiệu/kết quả kiểm tra không có kiểu UI; `RecoveryRow` (WPF) chỉ thêm thông báo đổi + brush |
| mới `RecoveryBrushes` (WPF) | | `RecoveryTone` -> brush đóng băng (màu lấy từ `RecoveryPresentation.Rgb`) |

Không đổi: bộ kiểm tra `RecoveryFileCheck` (chỉ đọc), `RecoveryRetryService`, `OperationJournal`, hợp đồng đóng băng (kiểu mới `internal`; `RecoveryFilter` giữ nguyên
namespace `PhotoReview.App`, chuyển assembly), khoá cấu hình. KHÔNG chạm Thùng rác thật: test dùng thư mục tạm + delegate giả.

## 2. Thứ tự/điều kiện được giữ (so với code-behind cũ)

- Kiểm tra nền: huỷ lượt trước -> đặt mọi hàng về "đang kiểm tra" -> 0 hàng thì chỉ xoá dòng trạng thái -> tắt Re-check -> mỗi kết quả báo về luồng UI
  (`Progress<T>` dựng trên luồng UI) -> hủy/lỗi chỉ chạm nút nếu lượt còn hiện hành -> xong thì bật Re-check, ghi "xong", làm mới danh sách.
- Clear: hỏi xác nhận (chỉ khi có delegate và có hàng) -> gọi delegate -> ném lỗi thì KHÔNG xoá hàng nào -> xoá hàng đã dismiss hoặc bị bỏ qua vì cũ -> làm mới -> báo số bị bỏ qua.
- Retry: điều kiện bắt đầu (có delegate, có hàng, không đang retry) -> xác nhận (đọc cài đặt xoá vĩnh viễn LÚC HỎI) -> `_retrying` bật TRƯỚC khi bật con trỏ chờ/khoá nút
  -> chỉ một lần gọi delegate -> luôn nhả trạng thái bận (finally) -> `Superseded` thì bỏ hàng và làm mới -> thông báo theo bảng (`DescribeRetry`): thành công = đóng cửa sổ,
  cảnh báo journal = vẫn đóng, bị từ chối = kiểm tra lại, cũ = chỉ thông tin. Cửa sổ từ chối đóng khi đang retry (R2-A-01).

## 3. Test

`RecoveryWindowPresenterTests` (24 ca, không cửa sổ): bộ lọc, bảng nút, chi tiết (chưa kiểm tra / thường / nhóm / không đích), kiểm tra nền thật trên thư mục tạm và khẳng định
không tệp nào đổi, Clear (xoá/cũ/ném lỗi), Retry (bận, ném lỗi, superseded, bảng thông báo, cài đặt đọc muộn). Test cũ (RecoveryPresenter*Tests ~83 ca, RecoveryWindow* 15 ca,
RecoveryPathPanelTests, WpfDialogHostSeamTests) trỏ vào `RecoveryPresentation` / `RecoveryBrushes` / `_presenter` và còn xanh.

## 4. Đột biến

Xem cuối tệp (điền sau khi chạy).

## 5. Hoàn tác

Thuần tái cấu trúc: `git revert` PR này.
