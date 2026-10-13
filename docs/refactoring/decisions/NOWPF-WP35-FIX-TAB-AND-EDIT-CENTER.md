---
id: NOWPF-WP35-FIX-TAB-AND-EDIT-CENTER
order: 1330
summary: |-
  WP-35 sửa lỗi (CHƯA XÁC NHẬN): (A) "màn trống sau Tab đầu tiên" trên Settings Win32 KHÔNG tái hiện trên master sau #511 (đã có test hồi quy xanh, 14 ca, và đột biến bỏ HWND_BOTTOM làm 14/14 đỏ). (B) chữ trong ô nhập một dòng cao (ô số / ô bắt phím) dính mép trên: EM_SETRECT bị ô một dòng bỏ qua, nên EditTextCentering dịch đỉnh vùng client xuống (WM_NCCALCSIZE) + tô dải bù bằng nền ô (WM_NCPAINT) để chữ số/chữ hoa nằm giữa; test điểm ảnh 4 ca (96/144 DPI x vi/en) đỏ trước sửa, xanh sau.
---

# NOWPF-WP35-FIX-TAB-AND-EDIT-CENTER - Tab không làm trống trang, chữ trong ô nhập căn giữa dọc (2026-10-14)

## 1. A - màn trống thoáng qua sau Tab đầu tiên

- Tái kiểm trên master sau #511 bằng kịch bản thật: đặt trạng thái UI "ẩn focus/phím tắt" (WM_CHANGEUISTATE UIS_SET), focus danh sách điều hướng,
  gửi WM_KEYDOWN VK_TAB qua `IsDialogMessage` (cùng bước vòng lặp thật), chụp BẰNG BitBlt từ DC cửa sổ (không PrintWindow) NGAY sau Tab,
  sau khi xử lý hàng đợi, và sau khi bơm 600 ms; so với ảnh sau `RedrawWindow` (ngoại trừ điều khiển vừa nhận focus) và với ảnh trước Tab,
  cộng khẳng định không điều khiển nào toàn màu nền. 7 trang x 96/144 DPI = 14 ca: xanh. Tab xoá UISF_HIDEFOCUS (đã khẳng định bằng WM_QUERYUISTATE).
- Nguyên nhân khả dĩ của hiện tượng đã thấy: trước #511 khung nhóm nằm TRÊN điều khiển con; WM_UPDATEUISTATE làm khung nhóm (bộ vẽ tay,
  tô kín `Dark.Window`) vẽ lại và đè nhãn/ghi chú tĩnh tới khi chúng được vẽ lại. Đột biến bỏ khối `HWND_BOTTOM` của #511: 14/14 đỏ (điều khiển trống).
- Kết luận: KHÔNG có thay đổi code cho A; chỉ thêm test hồi quy (`SettingsTabAndEditCenterTests.FirstTabFromTheNavigationList_DoesNotBlankThePage`).
- Ghi chú phát hiện kèm (chưa sửa, không phải lỗi người dùng báo): ngay sau Tab, combobox đang có focus vẽ chữ chọn nền xanh + viền cam; sau
  `RedrawWindow` ép vẽ lại nó hiện khác (khung chấm, không nền xanh). Hai đường vẽ của combobox có focus không nhất quán; test loại điều khiển đang focus khỏi phép so.

## 2. B - chữ trong ô nhập cao dính mép trên

- Ô nhập một dòng vẽ chữ ở ĐỈNH vùng client và bỏ qua `EM_SETRECT`/`EM_SETRECTNP` (chỉ nhiều dòng; đo: đặt rect, đọc lại không đổi).
  Ô số/ô bắt phím cao `FieldHeight` (22 px @96, 40 px @144 sau viền) nên chữ sát trên: khoảng cách trên/dưới 4/9 (96) và 8/19 (144).
- Sửa: `Dialogs/Native/EditTextCentering` (subclass cho ô nhập một dòng của trang Settings): `WM_NCCALCSIZE` dịch đỉnh vùng client xuống
  `(clientH - capHeight)/2 - 1 - internalLeading` (capHeight = ascent - internalLeading của font ô), `WM_NCPAINT` tô dải bù bằng nền ô
  (hỏi `WM_CTLCOLOREDIT`/`WM_CTLCOLORSTATIC` của cha). Ô giữ nguyên kích thước ngoài và viền; caret, vùng chọn, hit-test theo vùng client mới.
  Gắn trong `SettingsDialog.EnsurePageCreated` cho điều khiển Edit không có ES_MULTILINE (ô JSON nhiều dòng không đụng).
- Test (`TextOfSingleLineEditBoxes_IsVerticallyCentred`, 4 ca): mọi ô nhập một dòng của 7 trang, chữ "HE08" (chữ hoa + số: không có phần thòng),
  đo hàng điểm ảnh có mực trong vùng trong của ô (cửa sổ trừ viền): |trên - dưới| <= 1 và không chạm mép. Trước sửa 4/4 đỏ; sau sửa 4/4 xanh.

## 3. Chưa xác nhận / theo dõi

- CHƯA XÁC NHẬN: người dùng nhìn lại trang Mouse/Performance/Shortcuts trên máy thật (DPI 100 %/150 %, vi/en), gõ vào ô số thấy caret/vùng chọn đúng chỗ.
- Hoàn tác B: bỏ dòng `EditTextCentering.Attach(control)` ở `EnsurePageCreated` (chữ lại dính mép trên; test B đỏ).
- Phạm vi: chỉ ô nhập của Settings; ô nhập của Benchmark/ActionProfiles/ZoomPrompt chưa gắn (cùng cơ chế nếu cần).
