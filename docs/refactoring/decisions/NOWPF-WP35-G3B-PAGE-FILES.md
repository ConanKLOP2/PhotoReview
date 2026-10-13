---
id: NOWPF-WP35-G3B-FILES
order: 1150
summary: |-
  WP-35 GĐ3b trang Files (phương án B của spike S3, CHƯA XÁC NHẬN): trang "Files & safety" của Settings Win32 gốc, 11 thuộc tính (so sánh bản trùng, RAW, WebP/HEIC + trạng thái codec, xoá vĩnh viễn opt-in, xác nhận xoá, trình soạn thảo ngoài + Browse, nhật ký thao tác bằng 2 radio, dùng lại thư mục lần trước). Thêm `Radio` vào `SettingsPageBuilder` (nhóm WS_GROUP/WS_TABSTOP, cắt nhóm sau radio, dáng cổ điển như GĐ2) và hook `FocusExternalEditor` (lệnh "Open in external editor" mở Settings ở trang Files, focus ô đường dẫn như bản WPF). Không chuỗi mới; không nạp WPF; __MUT__ đột biến, 0 sống thật.
---

# NOWPF-WP35-G3B-FILES - trang Files của Settings Win32 (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G3A-SETTINGS-SHELL](NOWPF-WP35-G3A-SETTINGS-SHELL.md) (khung, hợp đồng trang,
mục "Cách thêm một trang"). Presenter/FieldMap KHÔNG đổi ([NOWPF-WP35-G0-PRESENTERS](NOWPF-WP35-G0-PRESENTERS.md)); bản WPF và cầu WPF giữ nguyên.

## 1. Đã làm

| Phần | File (`src/PhotoReview.Shell.Win32/Dialogs/SettingsUi/`) | Nội dung |
|---|---|---|
| Trang | `Pages/FilesSettingsPage.cs` | 5 nhóm theo thứ tự trang WPF: So sánh bản trùng (CompareHash/Size); Camera RAW (RawSupportEnabled, RawFullDecode, RawPairMode); WebP/HEIC (WebpHeicSupportEnabled + dòng trạng thái codec của máy); xoá (AllowPermanentDeleteWithoutRecycleBin, ConfirmBeforeDelete); trình soạn thảo ngoài (ExternalEditorPath + Browse); nhật ký thao tác (JournalDurability, 2 radio); nhóm Actions (MoveCopyReuseLastFolder) |
| Builder | `SettingsPageBuilder.cs` | `Radio(texts, hints?, indentDlu)` + `SettingsRadioElement`; CỘNG THÊM, các phương thức cũ không đổi |
| Ngữ cảnh | `SettingsPageContext.cs` | `RadioIndex(ids)`, `SetRadioIndex(ids, index)` (BM_SETCHECK không tự bỏ chọn bạn bè nên chọn một radio = đặt lại cả nhóm) |
| Hợp đồng | `ISettingsPageView.cs` | `bool FocusExternalEditor()` (mặc định false; trang Files focus ô đường dẫn, trả true) + bản ảo trong `SettingsPageBase` |
| Khung | `SettingsDialog.cs` | `FocusExternalEditor()` (tạo trang Files nếu cần, kích hoạt không đổi trang nhớ, gọi trang) + `SettingsTarget.ExternalEditor` đặt hàng đợi focus sau khi khung trả focus về danh sách điều hướng |
| Danh mục | `SettingsPageCatalog.cs` | MỘT dòng `["Files"]` |
| Test | `tests/PhotoReview.Shell.Tests/Dialogs/SettingsUi/SettingsFilesPageTests.cs` (+ `SettingsFilesSnapshotTests.cs`, thủ công) | xem mục 3 |
| Chuỗi | (không đổi) | mọi chữ đã có trong catalog Localizer (`settings.group.*`, `settings.externalEditorPath.*`, `settings.journalDurability.*`...): KHÔNG thêm khoá, i18n-check không đổi |

## 2. Thiết kế và các quyết định

1. **Radio.** `BS_AUTORADIOBUTTON`; radio đầu `WS_GROUP | WS_TABSTOP`, các radio sau không (dialog manager tự đặt tab stop lên radio đang chọn khi `BM_SETCHECK`).
   Điều khiển khai báo NGAY SAU nhóm (hint đầu tiên, hoặc điều khiển kế tiếp) nhận `WS_GROUP` nên mũi tên không đi ra ngoài cặp radio. Hint của radio được tạo SAU mọi
   radio (z-order) để `GetNextDlgGroupItem` không rơi vào chữ. Dáng cổ điển (không dark) của radio/group box do `NativeDialogChrome` lo từ GĐ2 (đã nhìn ảnh chụp: chữ sáng,
   chấm radio đọc được).
2. **An toàn dữ liệu.** Xoá vĩnh viễn giữ ngữ nghĩa y hệt WPF: mặc định TẮT, đúng chữ cảnh báo ("không khôi phục được, kể cả Undo"), Defaults/Reset trang đưa về TẮT, không bao giờ
   tự bật khi sửa thuộc tính khác (ca `PermanentDelete_*`). Việc thật sự xoá và hỏi xác nhận nằm ở lõi/presenter, không ở view.
3. **Browse.** `IShellFileDialog.PickOpenFile(owner = HWND Settings, tiêu đề = nhãn ô, bộ lọc `dialog.fileFilter.executable`, thư mục đầu)`: thư mục đầu = thư mục của đường dẫn
   hiện tại CHỈ KHI tệp đó tồn tại (như WPF), còn lại null (mặc định hệ thống; đường dẫn có ký tự cấm/chỉ dấu cách đều null, không ném). Huỷ hoặc chọn rỗng giữ nguyên chữ đã gõ;
   chọn xong chỉ điền ô (Save mới ghi). Hộp thoại ném lỗi (COM) hiện cảnh báo "không mở được" và giữ ô; không có `IShellFileDialog` (test) thì nút không làm gì.
4. **Đường dẫn.** Không kiểm hợp lệ lúc Save (như WPF: thiếu trình soạn thảo được báo khi chạy lệnh); `SettingsFieldMap` cắt dấu cách hai đầu; dấu cách giữa và tiếng Việt giữ
   nguyên; đường dẫn 300+ ký tự giữ nguyên.
5. **FocusExternalEditor.** WPF: `FocusExternalEditorOnLoad` + `BringIntoView` + `Focus` ở ContextIdle. Win32: `SettingsTarget.ExternalEditor` (đã mở trang Files mà không đổi trang nhớ ở
   GĐ3a) nay còn focus ô đường dẫn: `OnInitialized` đặt `PostToUi(FocusExternalEditor)` vì khung trả focus về danh sách điều hướng NGAY SAU `OnInitialized`; gọi lại từ trang khác vẫn
   đúng (kích hoạt trang Files mà KHÔNG nhớ nó).
6. **Phạm vi thuộc tính và chỗ lệch giữa ghi chú và XAML.** Bảng ghi chú ở GĐ3a xếp ActionsJson vào Shortcuts và MoveCopyReuseLastFolder vào Files, còn XAML WPF đặt ô JSON
   `ActionsText` ở trang Files và `MoveCopyReuseLastFolderCheck` ở nhóm Actions của trang Shortcuts; trang này theo GHI CHÚ/yêu cầu (không có ActionsJson; có MoveCopyReuseLastFolder
   trong nhóm "Actions" riêng). Bảng ghi chú của Performance cũng liệt kê Compare/RAW/WebP là của nó trong khi XAML đặt chúng ở Files: trang này giữ chúng (đúng XAML); nếu trang
   Performance cũng khai báo thì sẽ trùng thuộc tính: xem mục 5.

## 3. Kiểm chứng

`SettingsFilesPageTests` (__TESTS__ ca sau khi nở theory): __RUNS__.

| Nhóm | Phủ |
|---|---|
| Thuộc tính | 11 thuộc tính thuộc `SettingsFieldMap.CoveredProperties`, mẫu "không mặc định" khác mặc định ở từng thuộc tính; Load -> Collect -> Save; mặc định; mỗi ô tick chỉ ghi thuộc tính của nó (7 ca); thứ tự combobox RawFullDecode (2), RawPairMode (3) ghép theo CHỮ + chỉ số nạp; thuộc tính trang khác giữ nguyên |
| Xoá vĩnh viễn | mặc định tắt, chữ cảnh báo, Reset trang/Defaults về tắt (bản làm việc không đổi tới Save), không tự bật |
| Đường dẫn | rỗng, toàn dấu cách, cắt hai đầu, dấu cách giữa, tiếng Việt, UNC tiếng Việt, ký tự cấm, 300+ ký tự, mở lại hiện đúng |
| Browse | bộ lọc exe + tiêu đề, điền ô nhưng chưa ghi tới Save, huỷ/chọn rỗng, thư mục đầu (có/không tồn tại/ký tự cấm/rỗng), hộp thoại ném lỗi, không có hộp thoại |
| Radio | bấm đổi chọn + Save, cờ WS_GROUP/WS_TABSTOP, điều khiển sau nhóm mở nhóm mới, Tab chỉ dừng ở radio đang chọn, mũi tên Xuống/Lên đổi lựa chọn, hai nhóm độc lập trên một trang (hộp thoại thật), `SetRadioIndex` chọn đúng một, builder: cờ/ cắt nhóm/ hint sau radio/ tham số sai |
| Hook | `SettingsTarget.ExternalEditor` focus ô đường dẫn sau vòng lặp, trang nhớ không đổi; gọi từ trang khác; trang khác trả false |
| Ngôn ngữ, bố cục, handle | đổi en <-> vi (chữ, combobox, dòng trạng thái codec, giữ giá trị, không chồng/cắt), 100 % và 150 % en+vi, 3 trạng thái codec, GDI/USER không rò sau 5 lượt |

Ảnh chụp (`PHOTOREVIEW_DIALOG_SNAPSHOTS=<dir>`, `SettingsFilesSnapshotTests`): `settings-files-{en,vi}[-150][-bottom].png` (8 ảnh); đã xem: nhóm hiển thị đủ, ghi chú
tiếng Việt dài không bị cắt, radio + group box cổ điển đọc được trên nền tối, ô đường dẫn tiếng Việt, 150 % không chồng, thanh cuộn tối bên phải.

__RUNLINE__

## 4. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

__MUTTABLE__

## 5. Chưa kiểm được / việc còn lại

- Trùng thuộc tính với trang Performance nếu agent đó cũng làm Compare/RAW/WebP: hai trang cùng ghi một thuộc tính (trang sau thắng ở `CollectAll`); khi merge cần chốt một chủ.
- Trang Shortcuts cũng có thể khai báo MoveCopyReuseLastFolder (XAML đặt nó ở đó): cần chốt một chủ.
- Chưa kiểm: bàn phím thật (Alt+phím tắt của radio, IME), Narrator, `IFileOpenDialog` thật từ nút Browse (chỉ fake; hộp thoại thật đã kiểm ở GĐ1), màu chữ radio ở chế độ tương phản cao.
- Chưa bật cổng mặc định: `IsComplete` vẫn false tới khi đủ Mouse/Performance/Shortcuts.
- CHƯA XÁC NHẬN: nút Browse lỗi COM hiện cảnh báo (WPF `OpenFileDialog` không ném); hoàn tác: revert PR (bỏ một dòng catalog đưa trang Files về "chưa có", giá trị vẫn giữ).
