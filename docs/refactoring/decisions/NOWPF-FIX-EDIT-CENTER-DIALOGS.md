---
id: NOWPF-FIX-EDIT-CENTER-DIALOGS
order: 1370
summary: |-
  Ô nhập một dòng của Benchmark/ActionProfiles/ZoomPrompt cũng có lỗi "chữ dính mép trên" (CHƯA XÁC NHẬN): đo BitBlt 96/144 DPI x vi/en, khoảng cách mực trên/dưới của "HE08" lệch khi ô cao (vd. ActionProfiles @144: 8 trên / 19 dưới); sửa bằng cách gắn EditTextCentering ở một chỗ dùng chung (NativeDialog.InitializeDialog, mọi ô Edit không ES_MULTILINE của mọi hộp thoại phụ). 12 ca xanh sau sửa, 12/12 đỏ khi bỏ gắn; caret/EM_POSFROMCHAR nằm trong hàng chữ.
---

# NOWPF-FIX-EDIT-CENTER-DIALOGS - căn giữa dọc chữ ô nhập ở hộp thoại phụ (2026-10-14)

## 1. Kết quả đo

- Hộp thoại có ô nhập: Benchmark (thư mục, giới hạn ảnh), ActionProfiles (tên, phím, đích), ZoomPrompt (phần trăm). Mọi ô cao
  `FieldHeight`/`EditHeight` (22 px @96, 40 px @144) nên cùng lỗi với trang Settings (#517). RecoveryDialog chỉ có ô ES_MULTILINE
  (xử lý riêng, không đụng); Diagnostics/List không có ô EDIT.
- Trước sửa (bỏ gắn): khoảng cách trên/dưới của "HE08" lệch (vd. ActionProfiles @144: 8 / 19), 12/12 ca đỏ.

## 2. Sửa

- `NativeDialog.InitializeDialog` gọi `CenterSingleLineEdits` sau `ApplyFonts` + chrome: mọi `DialogItem` lớp Edit không có ES_MULTILINE
  được `EditTextCentering.Attach` (một chỗ gắn chung, không sao chép mã; ô nhiều dòng giữ nguyên EM_SETRECT riêng). Không đụng
  SettingsDialog (vẫn gắn riêng ở EnsurePageCreated vì ô tạo bằng CreateWindowEx).
- Test `DialogEditCenteringTests` (12 ca = 3 hộp thoại x 96/144 DPI x vi/en): |trên - dưới| <= 1, không chạm mép, hộp không đồng nền
  (có mực); tiêu điểm đưa ra khỏi ô (vùng chọn/caret nhấp nháy không tính là mực); `EM_POSFROMCHAR` và `GetCaretPos` (sau `EM_SETSEL`)
  nằm trong hàng chữ sau khi vùng client dịch xuống.

## 3. Chưa xác nhận / theo dõi

- CHƯA XÁC NHẬN: nhìn lại Benchmark/ActionProfiles/ZoomPrompt trên máy thật (100 %/150 %, vi/en), gõ thấy caret/vùng chọn đúng chỗ.
- Hoàn tác: bỏ lời gọi `CenterSingleLineEdits(hwnd)` (12 test đỏ).
