---
id: NOWPF-WP35-FIX-COMBO-FOCUS
order: 1370
summary: |-
  WP-35 sửa lỗi (CHƯA XÁC NHẬN): combobox của Settings Win32 có HAI kiểu vẽ - ngay sau Tab/chuột (focus, đổi chọn, bật/tắt, thả xuống) hiện nền xanh hệ thống + viền cam, còn sau RedrawWindow ép vẽ lại là kiểu tối của bộ vẽ tay. Gốc: combobox hệ thống tự vẽ vùng "mục đã chọn" bằng GetDC (COLOR_HIGHLIGHT + khung focus) NGOÀI WM_PAINT; bộ vẽ tay chỉ chạy ở WM_PAINT nên bản vẽ hệ thống ở lại tới khi có gì đó làm vô hiệu vùng (tab bàn phím thì UPDATEUISTATE vô tình sửa, focus bằng chuột thì không bao giờ). Sửa ở NativeDialogChromePainter.ControlProc: sau DefSubclassProc của các thông điệp làm hệ thống tự vẽ thì vẽ lại ngay bằng bộ vẽ tay (đồng bộ, GetDC); cùng cơ chế cho nút/checkbox/radio (focus). Test BitBlt mới 4 ca x 9 trạng thái; test Tab của #517 bỏ loại trừ điều khiển focus (trừ ô nhập: caret).
---

# NOWPF-WP35-FIX-COMBO-FOCUS - combobox Settings Win32 chỉ còn một kiểu vẽ (2026-10-14)

## 1. Tái hiện và nguyên nhân gốc

- Tái hiện bằng BitBlt từ DC cửa sổ (không PrintWindow; con trỏ đỗ lên tiêu đề hộp thoại): sau focus combobox (Tab bàn phím, hoặc `SetFocus` kiểu chuột)
  vùng chữ chọn có nền `COLOR_HIGHLIGHT` xanh + khung focus; sau `RedrawWindow` cùng combobox là nền `Dark.Input`, chữ sáng, khung chấm
  (focus bàn phím) hoặc viền nhấn (focus chuột). Focus chuột: ảnh xanh ở lại VÔ HẠN tới khi có vô hiệu vùng khác (trước sửa: 16 000 điểm ảnh lệch ở 144 DPI).
- Gốc: combobox `CBS_DROPDOWNLIST` hệ thống cập nhật vùng "mục đã chọn" bằng `GetDC` + vẽ trực tiếp (highlight + `DrawFocusRect`) khi nhận/mất focus,
  đổi chọn (CB_SETCURSEL, mũi tên, con lăn), bật/tắt, thả/đóng danh sách - KHÔNG qua `WM_PAINT`. `NativeDialogChromePainter` chỉ chặn `WM_PAINT`/`WM_PRINTCLIENT`,
  nên bản vẽ hệ thống không bị đè. Tab bàn phím "tự khỏi" vì `WM_UPDATEUISTATE` (cờ UISF_HIDEFOCUS đổi) làm vô hiệu vùng; focus chuột thì không.
  Nút/checkbox/radio cùng họ (vẽ focus bằng GetDC ở `WM_SETFOCUS`), chỉ thoáng qua tới lần xử lý hàng đợi (test Tab của #517 phải loại trừ điều khiển focus vì thế).

## 2. Sửa (một kiểu vẽ duy nhất)

- `NativeDialogChromePainter.ControlProc`: với combobox (WM_SETFOCUS/KILLFOCUS, WM_ENABLE, WM_SETFONT, WM_CANCELMODE, WM_KEYDOWN/CHAR, WM_LBUTTON*,
  WM_MOUSEWHEEL, CB_RESETCONTENT/SELECTSTRING/SETCURSEL/SHOWDROPDOWN) và nút (WM_SETFOCUS/KILLFOCUS, WM_ENABLE, BM_SETCHECK/SETSTATE/SETSTYLE):
  gọi `DefSubclassProc` rồi `RepaintNow` (GetDC + `PaintTo` đệm bộ nhớ, cùng hàm `DrawComboBox`/`DrawPushButton`...) TRƯỚC khi trả về;
  khi trở về hàng đợi thông điệp ảnh đã là kiểu tối. Không đổi kiểu cửa sổ, không owner-draw (danh sách thả và hành vi giữ nguyên).
- Màu/cue không đổi: focus bàn phím = viền `Dark.Accent` + khung chấm; focus chuột = viền `Dark.Accent` không khung chấm; hover = viền `Dark.TextMuted`;
  vô hiệu = viền `Dark.Border` + chữ `Dark.TextMuted`; không bao giờ `COLOR_HIGHLIGHT`.

## 3. Test

- `SettingsComboFocusPaintTests` (mới, 4 ca = 96/144 DPI x en/vi, mỗi ca quét mọi combobox hiện được của 7 trang x 9 trạng thái: focus bàn phím,
  Tab thật qua `IsDialogMessage`, focus chuột, hover, vô hiệu, rỗi, đổi chọn, mũi tên, thả xuống): ảnh combobox NGAY sau hành động và sau drain bằng ảnh
  sau `RedrawWindow` (dung sai 0 điểm ảnh), không có điểm ảnh `COLOR_HIGHLIGHT`, không đồng màu (>= 3 màu), chữ chọn còn (có mực).
  Trước sửa 4/4 đỏ; sau sửa 4/4 xanh. Ảnh trước/sau: đặt `PHOTOREVIEW_COMBO_DUMP`.
- `SettingsTabAndEditCenterTests.FirstTabFromTheNavigationList_DoesNotBlankThePage`: bỏ loại trừ vùng điều khiển focus khi so với ảnh sau `RedrawWindow`
  (chỉ giữ cho ô nhập: caret là lớp phủ hợp lệ). Bỏ loại trừ mà không sửa thì 10/14 đỏ (nút/checkbox "immediately" + caret); sau sửa nút/checkbox 14/14 xanh.
- Đột biến: (A) tắt `RepaintNow` hoàn toàn: 16/18 đỏ (4 combo + 12 Tab); (B) combobox chỉ repaint ở focus (bỏ ENABLE/KEYDOWN/CHAR/LBUTTONDOWN): 4/4 test combo đỏ.

## 4. Chưa xác nhận / theo dõi

- CHƯA XÁC NHẬN: người dùng nhìn lại combobox trên máy thật (DPI 100 %/150 %, vi/en): Tab vào, bấm chuột, mũi tên đổi chọn, thả xuống, vô hiệu.
- Hoàn tác: bỏ `case var m when RepaintsAfterSystemPaint(...)` và hai hàm phụ ở `NativeDialogChromePainter` (ảnh xanh trở lại; test combo đỏ).
- Phạm vi: ô nhập (caret) và trackbar/progress không đụng; các hộp thoại Native khác (Benchmark, ActionProfiles...) dùng chung bộ vẽ nên nhận cùng sửa.
