---
id: NOWPF-WP35-G3B-FILES
order: 1170
summary: |-
  WP-35 GĐ3b trang Files (phương án B của spike S3, CHƯA XÁC NHẬN): trang "Files & safety" của Settings Win32 gốc, 11 thuộc tính (so sánh bản trùng, RAW, WebP/HEIC + trạng thái codec, xoá vĩnh viễn opt-in, xác nhận xoá, trình soạn thảo ngoài + Browse, nhật ký thao tác bằng 2 radio, dùng lại thư mục lần trước). Thêm `Radio` vào `SettingsPageBuilder` (nhóm WS_GROUP/WS_TABSTOP, cắt nhóm sau radio, dáng cổ điển như GĐ2) và hook `FocusExternalEditor` (lệnh "Open in external editor" mở Settings ở trang Files, focus ô đường dẫn như bản WPF). Không chuỗi mới; không nạp WPF; 23 đột biến, 0 sống thật.
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

`SettingsFilesPageTests` (48 ca sau khi nở theory).

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

Chạy 2026-10-13 (desktop ẩn `PRSetFiles`, filter CI `Category!=Manual&Category!=Native`, `--blame-hang-timeout 120s`): `Shell.Tests` 1 354, `Shell.Integration.Tests` 150, `Shell.WpfBridge.Tests` 39, `Architecture.Tests` 158 (lần đầu đỏ 1 ca `NoMojibakeInCSharpFiles` do chú thích của file chụp ảnh bị hỏng mã hoá; đã sửa), `App.Tests` 2 594, `Core.Tests` 3 209 - 0 lỗi. Lặp `SettingsFilesPageTests` (48 ca/lượt): 50 lượt, 50 đạt, 0 lỗi, không lượt nào treo. ILC: `tools/check-aot-warnings.ps1 -RequireToolchain` = **0 cảnh báo** (PhotoReview.exe AOT 15,1 MB; thư mục publish tạm đã dọn). Không nạp WPF: `LazyLoadTests` (đã có) vẫn xanh; trang chỉ dùng `Imaging.Decoding.Wic` (không WPF). Số đo thời gian mở KHÔNG làm lại ở PR này (máy bận chạy song song; trang tạo lười chỉ khi mở trang Files).

## 4. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

| # | Đột biến | Kết quả |
|---|---|---|
| M01 | Load: combobox RawFullDecode lấy chỉ số của RawPairMode | chết bước 1 (1 ca) |
| M02 | Collect: JournalSafe so với chỉ số radio 0 (lệch chỉ số radio) | chết bước 1 (5 ca) |
| M03 | Load: xoá vĩnh viễn luôn nạp ở trạng thái bật (mất opt-in) | chết bước 1 (10 ca) |
| M04 | Collect: xoá vĩnh viễn đọc từ ô xác nhận xoá (lệch trường) | chết bước 1 (3 ca) |
| M05 | Collect: ConfirmBeforeDelete không bao giờ lưu | chết bước 1 (4 ca) |
| M06 | Collect: ExternalEditorPath bị bỏ | chết bước 1 (7 ca) |
| M07 | Browse: chọn rỗng ghi đè ô (huỷ không được tôn trọng) | chết bước 1 (1 ca) |
| M08 | Browse: không có bộ lọc exe | chết bước 1 (1 ca) |
| M09 | Browse: không bao giờ bắt đầu ở thư mục của trình soạn thảo | chết bước 1 (1 ca) |
| M10 | Browse: lấy thư mục đầu cả khi tệp không tồn tại | chết bước 1 (1 ca) |
| M11 | Load: radio nhật ký bị đảo | chết bước 1 (6 ca) |
| M12 | Collect: MoveCopyReuseLastFolder đọc từ ô so sánh kích thước | chết bước 1 (6 ca) |
| M13 | Collect: CompareHash đọc từ ô kích thước | chết bước 1 (2 ca) |
| M14 | Collect: RawPairMode đọc từ combobox RawFullDecode | chết bước 1 (3 ca) |
| M15 | FocusExternalEditor focus nút Browse thay vì ô | chết bước 1 (2 ca) |
| M16 | Đổi ngôn ngữ: dòng trạng thái codec không đổi | chết bước 1 (1 ca) |
| M17 | Dòng trạng thái codec bỏ qua nguồn được tiêm | chết bước 1 (3 ca) |
| M18 | Dialog Reset trang: nạp giá trị hiện tại thay vì mặc định | chết bước 1 (2 ca) |
| M19 | Dialog: ExternalEditor không còn đặt hàng đợi focus (focus bị khung lấy lại) | chết bước 1 (1 ca) |
| M20 | Dialog: nhảy tới Files làm trang được nhớ đổi | chết bước 1 (1 ca) |
| M21 | Builder: radio đầu không mở nhóm | chết bước 1 (3 ca) |
| M22 | Builder: không cắt nhóm sau radio | chết bước 1 (3 ca) |
| M23 | Context: SetRadioIndex không bỏ chọn radio còn lại | chết bước 1 (1 ca) |

Tổng: 23 chạy (build Release + test trên desktop ẩn `PRSetFiles`); bước 1 = `SettingsFilesPageTests`; 23 chết ở bước 1, 0 sống nên không cần bước 2 (toàn `Shell.Tests`). Còn sống thật: 0. Script ở scratchpad; cây làm việc đã hoàn nguyên (`git checkout`) sau từng đột biến.

## 5. Chưa kiểm được / việc còn lại

- Đã kiểm sau khi gộp #495: trang Performance KHÔNG khai báo Compare/RAW/WebP nên không trùng chủ với trang Files.
- Trang Shortcuts cũng có thể khai báo MoveCopyReuseLastFolder (XAML đặt nó ở đó): cần chốt một chủ.
- Chưa kiểm: bàn phím thật (Alt+phím tắt của radio, IME), Narrator, `IFileOpenDialog` thật từ nút Browse (chỉ fake; hộp thoại thật đã kiểm ở GĐ1), màu chữ radio ở chế độ tương phản cao.
- Chưa bật cổng mặc định: `IsComplete` vẫn false tới khi đủ Mouse/Performance/Shortcuts.
- CHƯA XÁC NHẬN: nút Browse lỗi COM hiện cảnh báo (WPF `OpenFileDialog` không ném); hoàn tác: revert PR (bỏ một dòng catalog đưa trang Files về "chưa có", giá trị vẫn giữ).
