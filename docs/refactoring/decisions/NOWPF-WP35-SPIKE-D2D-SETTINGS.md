---
id: NOWPF-WP35-SPIKE-D2D-SETTINGS
order: 1260
summary: |-
  Spike (CHƯA XÁC NHẬN): trang Display của Settings dựng bằng MỘT bề mặt Direct2D (tự vẽ + lai với EDIT/COMBOBOX Win32 thật). Đo: cuộn đúng pixel 0/15 bước lệch (100 %/150 %, GPU + WARP, so ảnh mới và so ảnh cắt từ khung cao), khung P95 <= 2,5 ms tới GPU xong, mở trang 45-83 ms (bản hiện tại 76 ms trang + 66 ms cửa sổ). Bản hiện tại vá WS_CLIPSIBLINGS xanh ở chrome tối, còn đỏ ở chrome cổ điển. Công sức chuyển 7 trang + khung ước 85-110 giờ-agent. Khuyến nghị: GIỮ BẢN VÁ cho chrome tối, ghi D2D lai làm đường lùi.
---

# NOWPF-WP35-SPIKE-D2D-SETTINGS - Settings vẽ bằng một bề mặt Direct2D (2026-10-13)

Mọi kết luận ở đây là **CHƯA XÁC NHẬN** (chờ người dùng quyết). Nền: [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md) (chọn hộp thoại Win32
gốc), [NOWPF-WP15-D2D-RENDERER](NOWPF-WP15-D2D-RENDERER.md) (renderer tái dùng). Nhánh `spike/settings-d2d-display`, PR nháp.

## 1. Đã dựng (chỉ thêm file; không sửa `SettingsDialog.cs`, `Pages/*`)

`src/PhotoReview.Shell.Win32/Dialogs/SettingsD2D/` (~3 200 dòng kể cả dòng trống, trang Display riêng 184 dòng so với 248 của bản cửa sổ con):

| File | Việc |
|---|---|
| `D2DSettingsView.cs` | không HWND: bố cục px nguyên, vẽ cả khung vào `D2DDrawContext`, cuộn (offset px + thanh cuộn tự vẽ: thumb/track/lăn/PageUp/Down/Home/End), chuột, bàn phím (Tab, Shift+Tab, mũi tên, Space, Esc, focus ring), combobox tự vẽ (danh sách lật lên khi hết chỗ), slider, nút chân, cây trợ năng (`AccessibleNodes`) |
| `D2DTextBox.cs` | c2: ô nhập tự vẽ: caret, chọn, hoàn tác/làm lại, chép/cắt/dán (seam clipboard), lọc số kiểu `ES_NUMBER`, một dấu thập phân cho cỡ chữ, cuộn ngang |
| `D2DSettingsWindow.cs` | HWND + `D2DRenderSurface` + WndProc `UnmanagedCallersOnly`: WM_PAINT/SIZE/DPICHANGED/chuột/phím/CHAR/VSCROLL/TIMER (caret), IME (đặt cửa sổ composition), clipboard thật; chế độ lai c1: EDIT/COMBOBOX thật đặt chồng, cắt vùng theo viewport (`SetWindowRgn`), chủ đề tối của `DarkDialogChrome.ApplyControls` + `WM_CTLCOLOR*` |
| `D2DDisplayPage.cs` | trang Display: cùng chuỗi `Tr.*`, cùng thứ tự combobox, cùng `SettingsFormState` (Load/Collect), cùng phụ thuộc cha-con; KHÔNG đổi `SettingsPresenter` |
| `D2DSettingsPalette.cs` | bảng màu tối = khoá `Dark.*` của `DarkPalette` (cùng bảng với `DarkDialogChrome`); cổ điển = màu hệ thống |
| `D2DSettingsSpike.cs` + 1 dòng ở `Program.cs` | lối vào thử: `PHOTOREVIEW_SETTINGS_D2D=hybrid|light` hoặc `--settings-d2d` |

Test (`tests/PhotoReview.Shell.Tests/Dialogs/SettingsD2D/`): `SettingsD2DScrollPixelTests` (15 bước, 100/150 %, tối/cổ điển, GPU/WARP, ảnh
cắt, cửa sổ thật), `SettingsD2DInputTests` (c1 + c2, bàn phím, trợ năng, vòng presenter), `SettingsD2DPerfTests` (Manual).
51 test xanh trên desktop ẩn (30 pixel/nhập liệu + 12 UIA + 9 đột biến); Architecture.Tests 158 xanh.

**Phát hiện thiết kế (quan trọng):** vẽ chữ trên bề mặt DPI 150 % (DPI thật + `PushTransform(1/scale)`) bị D2D bắt chữ vào lưới DIP nguyên
(bước 1,5 px), nên khi cuộn từng px chữ nhảy khác hình chữ nhật (15 000-19 000 pixel lệch so với ảnh cắt). Khắc phục: luôn tạo bề mặt ở
DPI 96 (1 DIP = 1 px) và tự nhân DPI trong bố cục (cỡ chữ = 12 x scale px). Test ảnh cắt `ScrolledFrame_EqualsTheCropOf...` canh lỗi này.

## 2. Số đo (CHƯA XÁC NHẬN)

Máy dev i7-9750H, Intel UHD 630 (GPU thật) và WARP, CPU nền 15-31 % (agent khác chạy song song: số có nhiễu nhẹ). Cửa sổ client 640x560 DIP.
Chạy trong testhost JIT (không phải exe R2R/AOT: "lạnh" là cận trên).

### 2.1 Đúng pixel khi cuộn (T1)

| Kịch bản | Bước lệch / số pixel lệch |
|---|---|
| D2D, ảnh thực (cùng bề mặt, có lịch sử) vs ảnh chuẩn (host mới, đặt thẳng offset), 15 bước, 96 và 144 dpi, tối + cổ điển, GPU + WARP | **0/15 bước, 0 pixel** (8 tổ hợp) |
| D2D, khung cuộn vs cắt từ khung cao không cuộn, 7 offset (gồm 1, max-1), 96/144 dpi | **0 pixel** |
| D2D cửa sổ thật (thông điệp thật tới HWND, swap chain flip-model), c2 và c1, 96/144 dpi | offset từng bước = bản phát lại; Present không lỗi (desktop ẩn báo Occluded: hợp lệ); c1: vị trí/hiển thị 5 control thật khớp từng bước |
| Bản hiện tại (master, `SettingsLegacyBaselineTests`), Display, chrome tối | 96 dpi: 14/15 bước lệch, 79 898 pixel; 144 dpi: 13/15 bước, 103 124 pixel |
| Bản hiện tại + vá `fix/settings-win32-scroll-repaint` (WS_CLIPSIBLINGS), `SettingsScrollPaintTests` | tối: 14/14 ca xanh; cổ điển (không chủ đề): 10/14 ca ĐỎ (Display 96: 14/15 bước, tới 105 239 pixel) |

Giới hạn: chụp pixel của cửa sổ thật không đọc được swap chain trên desktop ẩn (PrintWindow); tính đúng pixel được chứng minh trên bề mặt
offscreen cùng mã vẽ và cùng GPU. Dung sai 0 giữa hai lần vẽ cùng thiết bị; GPU khác nhau có thể khác nhau về khử răng cưa chữ (không đo).

### 2.2 Hiệu năng (T2, T3)

Mỗi khung khi cuộn (kịch bản 11 vị trí thumb + 20 nấc lăn, x20 lượt; offscreen 380 khung; ms):

| Cấu hình | CPU nộp lệnh P50 / P95 | tới GPU xong P50 / P95 / max | từng bước end-to-end cửa sổ P50 / P95 |
|---|---:|---:|---:|
| c2 96 dpi GPU | 0,73 / 1,43 | 1,18 / 2,45 / 21,6 (một cú nhảy) | 0,65 / 1,34 |
| c2 144 dpi GPU | 0,51 / 0,90 | 1,05 / 1,55 / 3,06 | 1,20 / 2,35 |
| c2 96 dpi WARP | 0,51 / 0,96 | 0,70 / 1,33 / 5,41 | 0,71 / 1,46 |
| c2 144 dpi WARP | 0,34 / 0,78 | 0,53 / 1,15 / 2,38 | 0,58 / 1,38 |
| c1 (lai) 96 dpi GPU | 0,30 / 0,61 | 0,64 / 1,18 / 3,71 | **9,9 / 20,9** |
| c1 (lai) 144 dpi GPU | 0,39 / 0,79 | 0,93 / 1,63 / 2,97 | **10,8 / 20,4** |
| c1 (lai) 96/144 dpi WARP | 0,30-0,33 / 0,56-0,58 | 0,46-0,52 / 0,77-0,94 | 10,3-11,2 / 14,7-18,7 |
| **bản hiện tại** (cửa sổ con, 96 dpi) | - | - | **42,8 / 149,5** (một bước kéo thumb 158 / 214) |
| bản hiện tại 144 dpi | - | - | 41,5 / 140,2 |

Chú ý: ở c1 phần vẽ D2D vẫn ~1 ms; 10 ms còn lại là việc di chuyển 5 cửa sổ con + `SetWindowRgn` + chúng vẽ lại (đo bằng `UpdateNow`).
Cuộn không có độ trễ chuỗi (không Present(1) chờ vblank: `ImmediatePresent`; app thật nên chờ waitable object).

Mở trang Display (dựng cửa sổ D2D + trang + nạp + khung đầu tới GPU xong; vòng "ấm" = n=14):

| Cấu hình | P50 / P95 (ms) | lạnh |
|---|---:|---:|
| c2 96 dpi GPU | 45,5 / 61,4 (dựng 40 + khung đầu 4,7) | 37 |
| c2 144 dpi GPU | 63,4 / 86,1 | 170 |
| c1 96 dpi GPU | 83,1 / 181 | 90 |
| c1 144 dpi GPU | 103,7 / 157 | 118 |
| c2 96/144 WARP | 28,2 / 24,5 | 21 |
| **bản hiện tại 96 dpi** (GoTo Display -> vẽ xong; chưa gồm tạo cửa sổ 66 ms) | 76,0 / 137,5 | 99,7 (+ cửa sổ 404 tổng lần đầu) |
| bản hiện tại 144 dpi | 63,0 / 94,4 | 67,5 |

Bộ nhớ/đối tượng: D2D c2 thêm +1 GDI, +4 USER, 5 MB private (+ bộ đệm chữ), 8-12 MB GPU dùng chung; c1 thêm +1 GDI, +11 USER. Bản hiện tại
mở trang Display: +5 GDI, +58 USER, +55 cửa sổ con, ~0,1-0,2 MB private (xem `baseline-legacy`). D2D chỉ 1 + 5 HWND.

### 2.3 Nhập liệu c1 vs c2 (T4)

| Hạng mục | c2 tự vẽ | c1 control Win32 thật |
|---|---|---|
| Gõ số, từ chối chữ | `D2DTextBox` lọc (đếm `RejectedCount`), test xanh | `ES_NUMBER` hệ thống (chặn chữ); số thập phân (cỡ chữ 12,5) KHÔNG có sẵn: phải subclass |
| Caret, Home/End, Shift+mũi tên, Ctrl+mũi tên | tự làm, test xanh | gốc |
| Kéo chọn, nhấp đôi chọn hết | tự làm (đo tiền tố bằng DirectWrite), test xanh | gốc |
| Chép/cắt/dán/undo/redo | tự làm (undo 100 mức), test xanh | gốc (undo 1 mức của EDIT) |
| Chọn bằng chuột/phím combobox | popup tự vẽ, lật lên khi thiếu chỗ, test xanh | COMBOBOX thật (CBN_SELCHANGE), test xanh |
| Con trỏ I-beam | `WM_SETCURSOR` tự đặt | gốc |
| IME Telex/VNI tiếng Việt | **CHƯA KIỂM** (không có bàn phím Telex trong harness): chỉ đặt cửa sổ composition ở caret; ô số không cần IME. Ô đường dẫn/JSON cần TSF thật | gốc, hoạt động như mọi EDIT |
| Màu tối | palette | `DarkDialogChrome` + `WM_CTLCOLOR*`: viền/nền khớp |
| Narrator | cần provider UIA (mục 2.4) | EDIT/COMBOBOX tự có UIA, nhưng KHÔNG nằm trong cây của bề mặt: phải gộp |
| Rủi ro | caret/IME/chọn/RTL/kéo-thả văn bản là "vô đáy" | lệch pha một khung giữa lớp D2D và control con khi cuộn nhanh (hai lớp, không nguyên tử); z-order/ClipChildren; phải đồng bộ giá trị hai chiều |

### 2.4 Bàn phím + UIA (T5)

Hoạt động (test xanh): Tab/Shift+Tab theo thứ tự khai báo, bỏ ô vô hiệu, vòng; mũi tên Lên/Xuống chuyển focus giữa ô tick/nút; Space bật/tắt ô
tick, mở combobox; Esc đóng danh sách; slider: mũi tên, PageUp/Down, Home/End; focus nhảy tới ô khuất tự cuộn vào; PageUp/Down/Home/End cuộn;
c1: Tab ra/vào ô thật chuyển focus HWND đúng. THIẾU (spike chưa làm): phím tắt Alt+chữ gạch chân (mnemonic), Enter = nút mặc định khi
đang ở ô nhập, tooltip, chữ chọn được/sao chép từ ghi chú, chế độ High Contrast, chạm/trượt, tự đổi ngôn ngữ giữa chừng, DPI hai màn hình khi
kéo cửa sổ qua (WM_DPICHANGED có xử lý nhưng chưa kiểm trên hai màn hình thật).

UIA: `AccessibleNodes()` cho 52 nút (6 nhóm, 31 ô tick, 3 ô nhập, 2 combobox, 1 slider, 4 nút, 5 ghi chú) với tên, vai trò, trạng thái tick, giá trị, bật/tắt,
focus, hình chữ nhật. Provider COM thật (`IRawElementProvider*`, Toggle/Value/ExpandCollapse/RangeValue/Invoke): xem mục 2.6.

### 2.5 Công sức (T6) và rủi ro

Đã viết: ~3 050 dòng nguồn cho khung chung + 184 dòng trang Display + ~800 dòng UIA + ~2 500 dòng test. Ước lượng (CHƯA XÁC NHẬN, tính theo tỷ lệ dòng đã
kiểm chứng/giờ-agent và khối lượng còn lại):

| Hạng mục | Giờ-agent |
|---|---:|
| Khung chung đủ dùng (mnemonic, Enter mặc định, tooltip, đổi ngôn ngữ, DPI hai màn hình, high contrast, hover/pressed, phím tắt nút chân, Save/Apply/Cancel nối `SettingsPresenter`) | 20-26 |
| Widget còn thiếu: radio (9 ở Files), nút trong dòng, ô bắt phím (97 ô ở Shortcuts + Mouse 13), ô đường dẫn + Browse, ô JSON nhiều dòng (giữ EDIT thật), dòng trạng thái/liên kết (cập nhật, shell integration) | 12-16 |
| Chuyển 6 trang còn lại (General 305, Mouse 268, Perf 164, Shortcuts 323, Files 223, Diagnostics 41 dòng bản cũ) | 14-18 |
| UIA đầy đủ cho các loại control đó + sự kiện | 10-16 |
| Chuyển/viết lại test (5 file `SettingsUi/*Tests` đọc cây HWND) + pixel/đột biến cho 7 trang | 18-24 |
| Ổn định hoá (IME/TSF thật, hai màn hình, RDP/WARP, AOT) | 8-12 |
| **Tổng** | **82-112** (giữa ~97) |

Số widget phải tái tạo: Group, Check, Note, Field, Combo, Slider, CheckFlow, Button (đã có 8) + Radio, KeyCapture, PathField, MultiLineEdit(host),
Link/Status, ScrollView lồng (Action profiles là hộp thoại riêng). Bản hiện tại dựa vào ~15 lớp điều khiển hệ thống (kể cả hộp thoại ActionProfiles/
ListDialog/BenchmarkDialog dùng chung `NativeDialog`): D2D chỉ thay Settings, nên hai hệ giao diện cùng tồn tại.

### 2.6 Kết quả của sub-agent (UIA, đột biến, AOT)

**UIA (sub-agent, ~3,5-4 giờ-agent thực tế, `D2DUiaProvider.cs` 668 dòng + `D2DUiaInterop.cs` 135 dòng + ~85 dòng `Uia*` trong view):** provider COM thật
(`[GeneratedComInterface]`/`StrategyBasedComWrappers`, không WPF, không reflection-COM, 0 cảnh báo) cho cây cửa sổ -> nhóm -> control (52 node):
IRawElementProviderSimple/Fragment/FragmentRoot, Toggle (ô tick), Value (ô nhập đọc/ghi, combobox đọc), ExpandCollapse (combobox), RangeValue
(slider), Invoke (nút); thuộc tính Name/AutomationId/ControlType/IsEnabled/HasKeyboardFocus/BoundingRectangle; sự kiện PropertyChanged + FocusChanged
xếp hàng; việc chuyển luồng RPC -> UI bằng SendMessage. 12 test UIA xanh (client COM cùng tiến trình, client PowerShell ngoài tiến trình, và
`PhotoReview.exe --settings-d2d` ở tiến trình riêng): tên + ControlType + ToggleState/Value khớp, Toggle/SetValue/Invoke đổi đúng dữ liệu, node bị
vô hiệu từ chối; đột biến 10/10 bị bắt. Trên desktop tương tác dựng cây 319 ms, Toggle 21 ms. Trên desktop ẩn của runner mọi hành động UIA trễ
~2 s (UIA core, trước provider; nghi do cửa sổ không foreground, CHƯA chứng minh) nên test UIA gắn `Slow`. CHƯA có: ScrollPattern/ScrollItem
(Narrator không cuộn theo focus), TextPattern (đọc caret/chọn), mục con combobox, Selection cho radio, LegacyIAccessible, StructureChanged;
một số sự kiện bị nhân đôi (chưa rõ nguyên nhân); chưa chạy Narrator thật. Ước lượng thêm cho 7 trang: 800-1 500 dòng, 2-3 ngày công
(TextPattern +150-250, cuộn +150-250, combobox con +150-200, radio +120-180, lưới ô phím Shortcuts +200-400).

**Đột biến (sub-agent):** 17 đột biến trong view/ô nhập/trang/cửa sổ: 8 bị bắt ngay; 9 sống sót (kẹp offset, EnsureVisible, lật popup, thumb tối thiểu,
tâm thumb slider, phụ thuộc ô trễ, đảo ShowFileInfo/FolderInfo, SetWindowRgn, EnableWindow) -> viết 9 test (`SettingsD2DMutationTests`) rồi bị bắt
hết; 0 tương đương. UIA: 10/10.

**AOT (sub-agent):** `-p:ShellAot=true` publish win-x64: BASE (master) và SPIKE đều 0 cảnh báo `IL*`/ILC/MSBuild (spike nằm trên đường truy cập qua lối
vào thử, nên không bị cắt). `PhotoReview.exe` +134 656 B (+0,83 %: 16 238 080 -> 16 372 736); cả hai lối vào (`PHOTOREVIEW_SETTINGS_D2D=hybrid`,
`--settings-d2d`) khởi động và sống 5 s dưới AOT. (Bản có UIA chưa đo lại AOT: cần một lượt publish nữa.)

## 3. Lỗi vẽ: D2D loại bỏ tận gốc và còn sót (mục 6 của yêu cầu)

| Lớp lỗi | Bản cửa sổ con (hiện tại) | D2D tự vẽ | Lai c1 |
|---|---|---|---|
| Hình cũ/chồng control khi cuộn (ScrollWindowEx + control con, sibling clip, ERASE) | có (79 898 / 103 124 pixel lệch ở Display); vá WS_CLIPSIBLINGS xanh chrome tối, đỏ chrome cổ điển | **loại tận gốc** (xoá + vẽ lại cả khung, không có bản copy bit) | loại ở phần D2D; 5 control thật vẫn cắt bằng `SetWindowRgn` (đã kiểm hình học, không kiểm pixel) |
| Chữ cắt/chồng khi đổi ngôn ngữ, DPI | phụ thuộc đo `GetTextExtent` từng control | loại (bố cục một nơi) | còn ở 5 control thật (font) |
| Nháy/lóe khi đổi cỡ (WM_ERASEBKGND, vẽ từng con) | có thể | loại (flip-model, một Present) | còn ở control thật |
| Màu tối lệch (WM_CTLCOLOR, nền trắng như lỗi menu #492) | vá bằng painter riêng | loại cho phần tự vẽ | còn ở EDIT/COMBO (cần `DarkDialogChrome`) |
| Trợ năng/tab/focus tự động của hệ thống | có sẵn | **mất, phải tự làm** (UIA, mnemonic, High Contrast, thanh cuộn chuẩn) | một phần |
| IME/TSF, chọn văn bản, kéo-thả | có sẵn | mất | có sẵn cho ô thật |
| Mất thiết bị GPU/RDP | không có | có (WARP, DeviceRecreated: đã có sẵn ở renderer) | có |
| Chữ nhảy ở DPI lẻ | không | **có nếu dựng sai** (đã gặp; tránh bằng bề mặt DPI 96) | như D2D |

## 4. Bảng tiêu chí quyết định

| # | Tiêu chí | Kết quả | Số |
|---|---|---|---|
| T1 | pixel lệch khi cuộn = 0 ở trang Display, 100 %/150 % | **ĐẠT** (D2D) | 0 pixel / 15 bước x 8 tổ hợp + 0 pixel ảnh cắt; (bản hiện tại: 79 898 và 103 124) |
| T2 | vẽ mỗi khung khi cuộn P95 <= 4 ms trên GPU thật | **ĐẠT** cho khung vẽ (c2: P95 1,4-2,5 ms tới GPU xong; c1: 1,2-1,6). Lưu ý: end-to-end c1 P95 ~ 21 ms do di chuyển control con (vẫn tốt hơn 150 ms của bản hiện tại) | Intel UHD 630 |
| T3 | mở trang Display không chậm quá 30 % so với hiện tại | **ĐẠT** (c2 45 ms, c1 83 ms so với 76 ms + 66 ms cửa sổ; ở 144 dpi c1 104 so 63+62 ms) | so cùng điều kiện, JIT testhost |
| T4 | nhập số + chọn combobox dùng được bằng bàn phím + chuột không lỗi, phương án lai c1 | **ĐẠT** (4 test c1 xanh: ES_NUMBER, CBN_SELCHANGE, Tab vào/ra) ; IME Telex/VNI CHƯA KIỂM | |
| T5 | UIA: mọi control có tên + vai trò đúng, hoặc có đường làm <= 16 giờ-agent | **ĐẠT** có điều kiện: 52/52 node có tên + vai trò đúng + trạng thái (provider UIA thật, 12 test xanh, đột biến 10/10); còn thiếu TextPattern/ScrollPattern/mục con combobox: đường làm rõ 800-1 500 dòng ~ 16-24 giờ-agent (sát ngưỡng 16 giờ) | Narrator thật CHƯA kiểm |
| T6 | ước tổng chuyển 7 trang + khung <= 80 giờ-agent | **KHÔNG ĐẠT** (ước 82-112, giữa ~97) | ước lượng, không đo |
| T7 | 0 cảnh báo ILC/AOT mới | **ĐẠT** (bản dựng trước provider UIA): 0 cảnh báo IL/ILC mới, +131 KiB exe | BASE 0, SPIKE 0; provider UIA (GeneratedComInterface) thêm sau, build thường 0 cảnh báo; AOT publish lại CHƯA đo |

## 5. Khuyến nghị (CHƯA XÁC NHẬN)

**GIỮ BẢN VÁ** cho đợt WP-35 hiện tại (chrome tối là cấu hình chạy thật: 14/14 ca xanh sau vá WS_CLIPSIBLINGS), và nâng test chrome
cổ điển khi người dùng cần. Spike chứng minh D2D (nhất là LAI: lớp D2D + EDIT/COMBO thật) thắng rõ về đúng pixel và tốc độ cuộn (1-2 ms so
với 43-150 ms), nhưng chi phí chuyển ~82-112 giờ-agent, phải tự làm lại trợ năng/IME/High Contrast, và hai hệ giao diện tồn tại song
song (hộp thoại Recovery/Benchmark/Diagnostics vẫn là `NativeDialog`). Nếu sau này lỗi vẽ lặp lại ở nhiều trang hoặc ở chrome cổ điển, đường lùi
rẻ nhất là LAI chỉ cho trang dài nhất (Shortcuts/Mouse) dùng lại `D2DSettingsView` đã có.

Quyết định cần người dùng (mỗi lựa chọn: ưu/nhược):
1. GIỮ BẢN VÁ (khuyến nghị): 0-2 giờ; còn 10/14 ca chrome cổ điển đỏ (không phải cấu hình chạy thật theo `DarkDialogChrome`; CHƯA XÁC NHẬN rằng mọi đường mở Settings đều có chrome tối).
2. LAI toàn bộ: sửa tận gốc cả hai chrome, khung 1-2 ms; 82-112 giờ-agent, rủi ro IME/UIA/đồng bộ hai lớp.
3. TOÀN BỘ D2D (ô nhập tự vẽ): đơn giản bố cục hơn LAI nhưng IME/TSF/Narrator tự làm hết; không khuyến nghị cho ô đường dẫn/JSON.

## Quyết định của lead (CHƯA XÁC NHẬN)

- Áp luật đã đặt trước khi chạy spike: T6 không đạt (ước 82-112 giờ-agent, vượt ngưỡng 80) => **GIỮ BẢN VÁ** (cửa sổ con + WS_CLIPSIBLINGS), không chuyển Settings sang bề mặt Direct2D.
- Mã spike giữ nguyên ở nhánh `spike/settings-d2d-display` (PR #509 đóng, không merge) làm đường lùi cho trang dài nhất (Shortcuts/Mouse) nếu lỗi vẽ tái diễn. Nhánh phụ `spike/settings-d2d-{baseline,mut,uia}` còn trên remote.
- Điều kiện mở lại: bản hotfix `fix/settings-blank-pages-clipsiblings` không giải quyết đồng thời (a) trang đủ nội dung và (b) cuộn không ghost, hoặc lỗi vẽ mới tái diễn ở nhiều trang.
- Lưu ý: kết luận "bản vá đủ" của agent spike dựa trên test cuộn 14/14 của #503 là test mù (so trống với trống, xem NOWPF-WP35-FIX-SETTINGS-BLANK); chỉ có giá trị sau hotfix.
