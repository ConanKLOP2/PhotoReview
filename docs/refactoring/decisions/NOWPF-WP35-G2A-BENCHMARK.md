---
id: NOWPF-WP35-G2A
order: 1100
summary: |-
  WP-35 GĐ0 (PR 3): tách logic cửa sổ Benchmark khỏi WPF (BenchmarkPresenter, BenchmarkProfileSession, BenchmarkReportStore, BenchmarkResultRow/BenchmarkText trong App.Shared); BenchmarkWindow chỉ còn view mỏng. Đóng giữa chừng = huỷ chạy, hai lần Start liên tiếp bị bỏ qua, tiến độ đến muộn bị loại.
---

# NOWPF-WP35-G2A - tách Benchmark khỏi WPF (PR 3, 2026-10-13)

Thẻ: WP-35; tiếp `NOWPF-WP35-G0-PRESENTERS` (PR 1) và `NOWPF-WP35-G0-RECOVERY` (PR 2). Cửa sổ Benchmark là modeless: sự kiện tiến độ đến từ luồng nền,
đóng cửa sổ giữa chừng phải huỷ lượt chạy mà không rò tác vụ nền, nên tách riêng và giữ NGUYÊN thứ tự/điều kiện của code-behind cũ.

## 1. Đã làm

| Phần | Trước | Sau |
|---|---|---|
| `BenchmarkWindow.xaml.cs` (view WPF) | 435 dòng (chạy, huỷ, tiến độ, bảng, lưu báo cáo, mở Explorer, 3 lớp hiển thị) | 132 dòng: chép giá trị vào điều khiển, hộp thoại chọn thư mục, mở Explorer, scheduler của Dispatcher |
| mới `BenchmarkPresenter` (App.Shared, `internal`) | | danh sách profile, `RunSelectedAsync`/`QuickCheckAsync`/`Cancel`/`Dispose`, tiến độ qua `IUiScheduler`, xếp hạng/điểm tốt nhất, quét thư mục (`EnumerateImageFiles`), nút Open report/folder, `ResultsToCsv` |
| mới `IBenchmarkView` (internal) | | `SetStatus`, `SetProgress`, `SetRunning`, `RefreshRows`, `OpenInExplorer` |
| mới `BenchmarkProfileSession` | | executor ảnh thật + phạm vi bật/tắt log của profile (`BenchmarkProfileScope`), dùng chung `BenchmarkWorkloadRunner` với CLI; codec/decoder do view WPF truyền vào |
| mới `BenchmarkReportStore` | | tên tệp báo cáo + ghi thư mục theo thời gian + `summary.json` (source-gen JSON, App.Shared là AOT-compatible) |
| `BenchmarkResultRow`/`BenchmarkProfileItem`/`BenchmarkText` | trong cửa sổ WPF | App.Shared, cùng namespace `PhotoReview.App`, giữ `public` (XAML bind); `BenchmarkResultRow.CellTexts` = 10 ô theo thứ tự cột |

Hợp đồng: kiểu mới `internal`; ba kiểu `public` chỉ đổi assembly (không thuộc C-01..C-18, `Approved/contracts.v1.txt` không đổi); khoá `config.json` không đổi.
`App.Shared` thêm ProjectReference tới `PhotoReview.Benchmarking` (không WPF; luật L-SHARED/L-WPF vẫn xanh). Cột "🏆" trong XAML dùng `BenchmarkWindow.BestSymbol`
(const public, vì `x:Static` không với tới kiểu ở assembly khác nếu không public).

## 2. Thứ tự/điều kiện được giữ

- Start: đọc thư mục/giới hạn ảnh ở luồng UI -> tạo CTS TRƯỚC khi quét (Cancel/Close ngắt được cả lúc quét) -> bắt token một lần -> khoá điều khiển -> xoá hàng, thanh về 0.
- Quét nền: thư mục mất -> "folder missing"; IOException/UnauthorizedAccess -> "cannot read"; 0 ảnh -> "no images"; mọi nhánh trả điều khiển về rảnh.
- Mỗi profile: tạo session NGOÀI xử lý lỗi của profile (lỗi tạo = cả lượt "failed"), chạy, thêm hàng, tính lại điểm tốt nhất; lỗi profile = hàng Fail + trạng thái, profile sau vẫn chạy;
  huỷ = ném tiếp; luôn giải phóng session (tắt phạm vi log rồi dispose executor, lồng try/finally).
- Cuối: có báo cáo -> lưu, trạng thái "done"; huỷ -> "canceled"; finally luôn nhả điều khiển rồi huỷ/dispose CTS.
- Đóng cửa sổ (`OnClosed` -> `Dispose`): huỷ token; lượt chạy kết thúc bằng đường huỷ bình thường (không có hàng Fail giả).

Thay đổi hành vi có chủ đích (cả hai là sửa lỗi hẹp, ghi để rà): (1) Start lần hai khi đang chạy bị bỏ qua (trước: một `RaiseEvent` lập trình vào nút đã tắt có thể khởi động lượt chạy
song song và làm rò lượt đầu); (2) tiến độ đến muộn sau khi lượt chạy đã xong/bị huỷ hoặc thuộc lượt cũ bị loại (trước: `Progress<T>` có thể ghi đè dòng "canceled").

## 3. Test

`BenchmarkPresenterTests` (App.Tests, 64 ca, không cửa sổ, không Sleep/Delay, không so ThreadId): bảng kịch bản chạy bình thường, tiến độ qua scheduler (hàng đợi, test tự bơm), tiến độ Total=0/chỉ số
profile/dịch thông điệp, Cancel giữa profile/giữa quét/khi rảnh, đóng giữa hai profile/giữa profile/giữa quét, một profile lỗi, mọi profile lỗi, lỗi tạo session, hai lần Start
(Run, Quick check, không chọn), tiến độ muộn sau huỷ và của lượt cũ, báo cáo hoàn tất dù đã yêu cầu huỷ, các lỗi thư mục, giới hạn ảnh, Quick check, điểm tốt nhất theo nhóm workload, 10 ô cột,
bảng trạng thái, CSV, Open report/folder, lưu báo cáo, đổi ngôn ngữ, mặc định. Test Integration `BenchmarkWindow*` giữ nguyên hành vi (chỉ đổi chỗ gọi hàm tĩnh sang `BenchmarkPresenter`/`BenchmarkReportStore`).
Lưu ý khi viết test: một Assert ném bên trong session giả bị presenter nuốt thành hàng Fail, nên giá trị được ghi lại rồi khẳng định bên ngoài.

## 4. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

28 đột biến thủ công (huỷ, dispose, bộ lọc tiến độ theo lượt, phân số tiến độ, chỉ số profile, dịch thông điệp, guard Start lần hai, điểm tốt nhất, thứ tự ô, nhả điều khiển, huỷ ném tiếp,
dispose session, xoá hàng, ngưỡng lưu, giới hạn ảnh, số lượt Quick check, định dạng ms, mở báo cáo, tên báo cáo rỗng, thứ tự catch thư mục, lỗi truy cập, ô điểm tốt nhất, mặc định 64, thư mục kết quả):
bước 1 (App.Tests Benchmark*) diệt 26/28; 4 đột biến không biên dịch (analyzer) làm lại dạng khác. 2 sống thật (`M05` chỉ số profile luôn 1, `M20` không dịch thông điệp) -> thêm
`Progress_ZeroTotal_AndProfileIndex` (viết lại để khẳng định bên ngoài session) và `Progress_MessageIsTranslated`; chạy lại: bị diệt. Còn sống: 0, không có đột biến tương đương.
Lặp 100 lượt bộ `BenchmarkPresenterTests`: 100 đạt, 0 lỗi (lượt đầu lộ một điều kiện đua của chính test: fake quét đọc `CancellationToken.WaitHandle` sau khi presenter dispose CTS -> đổi sang `IsCancellationRequested`).
Chạy rộng: App.Tests 2 593, Integration 1 343 (gồm UI trên desktop ẩn `PRWp35Bench`), Architecture 158, Shell.WpfBridge 38 - xanh.

## 5. Hoàn tác

Thuần tái cấu trúc (+ hai sửa hẹp ở mục 2): `git revert` PR này; không đổi định dạng dữ liệu hay khoá cấu hình.
