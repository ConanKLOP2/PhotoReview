---
id: NOWPF-WP35-FIX-SETTINGS-BLANK
order: 1250
summary: |-
  WP-35 hotfix (CHƯA XÁC NHẬN): #503 làm mọi trang Settings Win32 TRỐNG (chỉ còn khung nhóm). Nguyên nhân: WS_CLIPSIBLINGS trên mọi điều khiển + khung nhóm (tạo trước) nằm TRÊN các điều khiển con trong thứ tự Z, nên mỗi con bị trừ vùng của khung. Sửa: đưa BS_GROUPBOX xuống đáy Z (HWND_BOTTOM) khi tạo trang, giữ WS_CLIPSIBLINGS; WS_GROUP của radio chuyển sang điều khiển thật đầu tiên sau khung. Combobox tự thu về chiều cao đóng ngay trong SetWindowPos nên mất lần vẽ đầu: InvalidateRect sau khi đặt chỗ. Test điểm ảnh mới: điều khiển hiện không được toàn màu nền (14/14 đỏ trên master, xanh sau sửa).
---

# NOWPF-WP35-FIX-SETTINGS-BLANK - trang Settings Win32 hiện đủ nội dung và cuộn không hình ma (2026-10-14)

## 1. Lỗi và nguyên nhân gốc

- Sau #503 (commit 0a15b79d) mọi trang Settings chỉ còn tiêu đề, danh sách trang, khung nhóm rỗng và nút dưới.
- `CreateWindowEx` đặt anh em tạo sau nằm DƯỚI anh em tạo trước. `SettingsPageBuilder.Group` thêm khung nhóm TRƯỚC các điều khiển con
  nên khung nằm TRÊN con của nó. #503 đặt `WS_CLIPSIBLINGS` cho mọi điều khiển => mỗi con bị trừ vùng của khung nhóm ở trên => bị cắt hết.
- Trước #503 (không có cờ) thì nội dung hiện nhưng khung tô kín hình chữ nhật và phép chép bit của `SetWindowPos` lấy cả điểm ảnh của
  điều khiển nằm trên => hình ma lệch một nấc cuộn (~46 px). Cả hai lỗi cùng một gốc: khung nhóm sai chỗ trong thứ tự Z.

## 2. Vì sao #503 lọt

`SettingsScrollRepaintFixTests`/`SettingsScrollPaintTests` so ảnh sau cuộn với ảnh sau `RedrawWindow` ép vẽ lại. Trang TRỐNG sau cuộn
bằng trang TRỐNG sau vẽ lại nên xanh: test so "trống với trống", không có khẳng định về nội dung.

## 3. Sửa

- `SettingsDialog.EnsurePageCreated`: mỗi `BS_GROUPBOX` được `SetWindowPos(HWND_BOTTOM, NOMOVE|NOSIZE|NOACTIVATE)` ngay sau khi tạo; con của nhóm
  nằm trên khung và vẫn có `WS_CLIPSIBLINGS` (không còn ma, không còn bị cắt). Khung nhóm không có WS_TABSTOP nên thứ tự Tab không đổi.
- `SettingsPageBuilder.Add`: cờ `WS_GROUP` sau nhóm radio không còn đặt lên khung nhóm mà lên điều khiển thật đầu tiên khai báo sau đó
  (khung đã xuống đáy Z; nhóm hộp thoại của radio vẫn kết thúc đúng chỗ).
- `PageSurface.Place`: sau khi đặt chỗ một combobox thì `InvalidateRect` nó (combobox kiểu danh sách thả tự co về chiều cao đóng trong chính `SetWindowPos`,
  lần vẽ đã xếp hàng mất theo; trang Performance hiện ô Decoder backend trống tới khi có gì đó vẽ lại. Chỉ lộ ra sau khi hết lỗi trống).
- Test (`SettingsScrollRepaintFixTests`): thêm khẳng định mỗi điều khiển hiện của trang (đã cắt theo vùng nhìn, bỏ khung nhóm và nhãn/nút rỗng)
  KHÔNG toàn màu nền, ở đầu/giữa/cuối của 7 trang x 100%/150%. Trên master: 14/14 ca đỏ; sau sửa xanh. Đột biến: bỏ cả hai sửa = trạng thái master (14/14 đỏ, chạy thật); bỏ riêng `InvalidateRect` combobox: trang Performance đỏ (chạy thật ở bước trung gian: 2/14 đỏ).

## 4. Chưa xác nhận / theo dõi

- CHƯA XÁC NHẬN: người dùng nhìn lại các trang Settings trên máy thật (nhiều màn hình, DPI khác).
- Hoàn tác: bỏ khối `HWND_BOTTOM` ở `EnsurePageCreated` và điều kiện `!isGroupBox` ở `SettingsPageBuilder.Add` (khi đó lại trống nếu giữ WS_CLIPSIBLINGS).
