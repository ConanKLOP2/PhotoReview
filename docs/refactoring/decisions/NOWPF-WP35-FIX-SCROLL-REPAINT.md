---
id: NOWPF-WP35-FIX-SCROLL-REPAINT
order: 1220
summary: |-
  WP-35 sửa lỗi (CHƯA XÁC NHẬN): cuộn trang Settings Win32 (tối) để lại hình cũ / vẽ chồng. Nguyên nhân gốc: điều khiển của trang tạo bằng CreateWindowEx thiếu WS_CLIPSIBLINGS (hộp thoại dựng từ template tự có), nên vùng vẽ + phép chép bit của SetWindowPos lấy cả điểm ảnh của điều khiển anh em nằm trên. Sửa: thêm WS_CLIPSIBLINGS cho mọi điều khiển trang (một dòng, áp cho mọi trang). Test điểm ảnh 14 ca (7 trang x 100%/150%) xanh sau sửa, 10/14 đỏ trước sửa.
---

# NOWPF-WP35-FIX-SCROLL-REPAINT - cuộn trang Settings Win32 không còn hình ma (2026-10-13)

Báo lỗi: Settings Win32 bản 2.0.487, trang Display (tối), cuộn thì nhãn/checkbox/khung nhóm bị vẽ chồng, "Auto-hide the info over the photo"
hiện 2 lần, nhóm XIF đè lên nhóm khác. Chồng lệch đúng một nấc cuộn (3 dòng ~ 46 px).

## 1. Nguyên nhân gốc (có bằng chứng)

- `EnsurePageCreated` tạo điều khiển trang bằng `CreateWindowEx` với kiểu từ builder (`WS_CHILD|WS_VISIBLE|...`), KHÔNG có
  `WS_CLIPSIBLINGS` (hộp thoại dựng từ template thì tự có, nên nút/danh sách điều hướng không bị).
- Các điều khiển của một trang là anh em chồng lên nhau: khung nhóm (bộ vẽ tay `NativeDialogChromePainter`, tô kín cả hình chữ nhật bằng
  `Dark.Window` rồi vẽ khung) nằm DƯỚI các điều khiển của nhóm. Mỗi bước cuộn dời từng điều khiển bằng `SetWindowPos` (hệ thống chép bit
  điểm ảnh cũ sang chỗ mới và coi là đã hợp lệ). Không có `WS_CLIPSIBLINGS`, vùng vẽ và nguồn chép bit của cửa sổ gồm cả điểm ảnh của anh em
  nằm trên: khung nhóm tô đè/chép lại hình của checkbox, nhãn khác -> điểm ảnh cũ nằm ở chỗ "hợp lệ", không vẽ lại.
- Bằng chứng: test `SettingsScrollRepaintFixTests` chụp bề mặt thật của hộp thoại (BitBlt từ DC cửa sổ; PrintWindow tự vẽ lại hết nên không
  thấy lỗi) sau mỗi bước cuộn rồi so với ảnh sau `RedrawWindow(ALLCHILDREN|ERASE|UPDATENOW)`. Trước sửa: 10/14 ca đỏ (trang 0,1,2,4,5 ở 100% và 150%),
  ảnh `actual` giống hệt ảnh người dùng gửi (chữ nhân đôi, tiêu đề nhóm mất). Ma trận thử: chỉ thêm `WS_CLIPSIBLINGS` -> 14/14 xanh; chỉ thêm
  `SWP_NOCOPYBITS` -> 8/14 vẫn đỏ; cả hai -> 14/14 xanh (không cần).

## 2. Sửa

`SettingsDialog.EnsurePageCreated`: `item.Style | WS_CLIPSIBLINGS` cho mọi điều khiển tạo từ builder. Một cơ chế duy nhất nên áp cho mọi trang
(kể cả trang tạo lười sau này). Không đổi `SetWindowPos`/bố cục; chi phí cuộn không tăng (đo trang Display ~80 điều khiển, trung vị 90-120 ms
trong test JIT trên desktop ẩn, tương đương/nhanh hơn bản cũ 150-200 ms trong cùng điều kiện - số nhiễu vì máy dùng chung).

## 3. Chưa xác nhận / theo dõi

- CHƯA XÁC NHẬN: người dùng nhìn lại trang Display trên máy thật (đa màn hình, DPI khác) sau bản mới.
- Hoàn tác: bỏ `| ScrollConstants.WsClipSiblings` ở `EnsurePageCreated` (test điểm ảnh sẽ đỏ lại).
