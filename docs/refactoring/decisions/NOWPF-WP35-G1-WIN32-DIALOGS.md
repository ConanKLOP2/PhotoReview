---
id: NOWPF-WP35-G1
order: 1100
summary: |-
  WP-35 GĐ1 (phương án B của spike S3, CHƯA XÁC NHẬN): khung hộp thoại Win32 gốc AOT-sạch trong Shell.Win32 (template trong bộ nhớ + dialog manager, DPI từng màn hình, dark qua DWM/uxtheme/WM_CTLCOLOR*, modal không nuốt WM_QUIT, vòng lồng của shell) + 4 cửa sổ nhỏ (ZoomPrompt, Diagnostics + Sao chép, SkippedFiles, BatchReview) dùng presenter G0 + IFileOpenDialog/IFileSaveDialog + `Win32SecondaryWindowPort` và `SecondaryWindowRouter` (Win32 -> cầu WPF -> cổng tạm theo từng cửa sổ). @@SUMMARY@@
---

# NOWPF-WP35-G1 - hộp thoại Win32 gốc, giai đoạn 1 (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); lộ trình GĐ1 của [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md) mục 4 (khuyến nghị B,
CHƯA XÁC NHẬN). Nền: [NOWPF-WP35-G0-PRESENTERS](NOWPF-WP35-G0-PRESENTERS.md) (presenter không WPF), [NOWPF-SHELL-WIRE-WPF-BRIDGE](NOWPF-SHELL-WIRE-WPF-BRIDGE.md)
(cầu WPF đã nối), [NOWPF-AOT-SETTINGS-FALLBACK](NOWPF-AOT-SETTINGS-FALLBACK.md) (cổng tạm), [NOWPF-WP19B](NOWPF-WP19B-WPF-BRIDGE.md),
[NOWPF-WP23](NOWPF-WP23-OVERLAY-HUD.md) (`NativeShellFolderDialog`). Không đổi presenter, hành vi hay bản WPF (`App.WpfWindows` giữ nguyên).

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| Interop | `Shell.Interop/Win32/DialogNative.cs` | `[LibraryImport]` user32 (CreateDialogIndirectParam, IsDialogMessage, EnableWindow, GetDlgItem, DrawText, SystemParametersInfoForDpi, GetGuiResources...), gdi32 (brush, font, màu), uxtheme `SetWindowTheme`, hằng `DialogConstants`. Không DllImport. |
| Interop | `Shell.Interop/Shell/IFileOpenDialog.cs` | `IFileSaveDialog` (`[GeneratedComInterface]`, IID 84BCCD23, CLSID C0B4E2F3) + `ShellNative.CreateFileSaveDialog`. |
| Khung | `Shell.Win32/Dialogs/Native/DialogTemplate.cs` | DLGTEMPLATE (không Ex) dựng trong bộ nhớ: header + DLGITEMTEMPLATE căn DWORD, toạ độ 0 (bố cục tính khi chạy). |
| Khung | `.../NativeDialog.cs` | Lớp cơ sở: tạo bằng `CreateDialogIndirectParam`, modal tự quản, DPI, dark, đơn vị dialog, đo chữ, bố cục lại khi `WM_SIZE`/`WM_DPICHANGED`. |
| Khung | `.../NativeDialogChrome.cs` | `INativeDialogChrome` + `DarkDialogChrome` (title bar `TitleBar.Apply`, uxtheme 135/133 qua `DarkModeMenus`, `DarkMode_Explorer`/`DarkMode_CFD`) + `DialogColors` (COLORREF lấy từ `DarkPalette`). |
| Cửa sổ | `.../ZoomPromptDialog.cs` | Ô số + Huỷ/Apply; nhập sai: thông báo `ClickZoomCustomPresenter` hiện dưới ô, hộp thoại giãn ra, giữ mở. |
| Cửa sổ | `.../DiagnosticsDialog.cs` | 15 cặp nhãn/giá trị (`DiagnosticsPresenter`), chữ xuống dòng, thêm nút **Sao chép** (bảng "nhãn TAB giá trị" qua `IClipboardService`; khoá mới `diagnostics.copy`). |
| Cửa sổ | `.../ListDialog.cs` | SkippedFiles (chỉ Đóng) và BatchReview (Huỷ + xác nhận), listbox cuộn dọc/ngang, kéo giãn được, kích thước tối thiểu. |
| Tệp | `Dialogs/NativeShellFileDialog.cs` | `IShellFileDialog` (`PickOpenFile`/`PickSaveFile`): bộ lọc, đuôi mặc định, tên gợi ý, thư mục đầu; dành cho Import/Export Settings và JSON action ở GĐ sau (chưa có chỗ gọi). |
| Cổng | `.../Win32SecondaryWindowPort.cs` | `Win32SecondaryWindowPort : ISecondaryWindowPort` (Diagnostics, SkippedFiles, BatchReview, ZoomPrompt; Settings/Recovery/Benchmark = chưa có) và `SecondaryWindowRouter`. |
| Vòng lặp | `Hosting/MessageLoop.cs` | `RunNested(isDone, claim)`: vòng lồng cùng cấu trúc lượt với `Run` (việc dispatcher, đồng hồ khung, bộ lọc phím WPF modeless, việc nền). |
| Đăng ký | `Startup/ShellCompositionRoot.cs` | `SecondaryWindowFallback` thành singleton, host = router, `IShellFileDialog`. |

## 2. Thiết kế

1. **Dialog manager làm Tab/Enter/Esc/IME/UIA**: cửa sổ do `CreateDialogIndirectParam` tạo (không `DialogBoxIndirectParam` vì cần vòng lồng của shell
   và modeless về sau); vòng modal gọi `IsDialogMessage` qua `MessageLoop.RunNested(isDone, claim)`. Nút mặc định đặt bằng `DM_SETDEFID` (Close = IDCANCEL).
2. **Modal đúng**: owner bị `EnableWindow(false)` trước khi hiện, bật lại TRƯỚC khi huỷ hộp thoại (kích hoạt trả về owner); owner không hợp lệ/0 thì không owner.
   **WM_QUIT không bị nuốt** (bài học #486): vòng lồng đăng lại `PostQuitMessage(exitCode)` rồi đóng như Huỷ; `NativeDialog.Close()` (shell thoát giữa chừng,
   `SecondaryWindowPortHost.Dispose`) đánh thức vòng lồng bằng `WM_NULL`. Ngoại lệ trong `DialogProc` được bắt (không thoát qua khung native), hộp thoại đóng
   như Huỷ và ngoại lệ ném lại cho người gọi sau khi dọn sạch.
3. **DPI từng màn hình**: font từ `SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, dpi)` (tôn trọng font hệ thống), dialog-unit tính như
   `GdiGetCharDimensions` (4 dlu = ký tự trung bình, 8 dlu = chiều cao dòng) từ font đó; `WM_DPICHANGED` tạo lại font, đặt lại cỡ và bố cục. Không dựa vào
   việc hệ thống tự co hộp thoại (nên đo được bằng test).
4. **Bố cục dlu + đo chữ thật**: toạ độ chỉ tính ở mã (`LayoutControls`); chữ nhiều dòng đo bằng `DrawText(DT_CALCRECT|DT_WORDBREAK)` với font của điều khiển nên
   chuỗi tiếng Việt dài (nhãn, thông báo lỗi, giá trị Diagnostics) cao thêm thay vì bị cắt; nút rộng tối thiểu 50 dlu hoặc chữ + 16 dlu.
5. **Dark**: title bar `DWMWA_USE_IMMERSIVE_DARK_MODE` + màu caption; `SetPreferredAppMode(ForceDark)` + `AllowDarkModeForWindow` (ordinal 135/133, đã dùng ở menu);
   chủ đề `DarkMode_Explorer` cho nút/listbox, `DarkMode_CFD` cho ô nhập; nền/chữ qua `WM_CTLCOLORDLG/STATIC/EDIT/LISTBOX/BTN` bằng màu `Dark.*`. Cờ dark gọi
   qua seam `INativeDialogChrome` (test ghi lại), thiếu hàm = nền sáng, không ném.
6. **Chuỗi**: mọi chữ qua `Tr` (khoá có sẵn: `dialog.clickZoomCustom.*`, `common.*`, `diagnostics.*`, `batchReview.*`, `main.skipped.window.*`);
   khoá mới duy nhất `diagnostics.copy` (en, vi, notes) thêm ở CUỐI catalog.
7. **Định tuyến theo cửa sổ** (`SecondaryWindowRouter`): cổng Win32 cho cửa sổ nó `Supports`; chưa có thì cầu WPF (nạp lười, chỉ khi cần; lỗi nạp = coi như vắng);
   cầu vắng (bản AOT) thì `SecondaryWindowFallback` (Settings mở config.json, Recovery/Benchmark chỉ báo). `ISecondaryWindowPort` đăng ký sẵn (test) vẫn thắng.
   Hệ quả bản AOT: Diagnostics/SkippedFiles/BatchReview/ZoomPrompt MỞ THẬT; Settings/Recovery/Benchmark vẫn cổng tạm (7 khoá `status.secondaryUnavailable.*`
   giữ nguyên; ba khoá diagnostics/skippedFiles/batchReview chỉ còn dùng khi dựng `Win32DialogService` không qua router).

## 3. Kiểm chứng

Bộ mới (HWND THẬT trên thread STA riêng, không `Sleep`; người dùng mô phỏng bằng `SendMessage`/`IsDialogMessage` đồng bộ trong callback `ready` của
`NativeDialog.ShowModal`, chạy trước vòng modal; chi tiết ở [TESTING](../../TESTING.md)):

| Test (`tests/`) | Số ca | Phủ |
|---|---:|---|
| `Shell.Tests/Dialogs/Native/DialogTemplateTests` | 4 | header, căn DWORD, id/lớp atom, chuỗi UTF-16, tiêu đề null |
| `.../ZoomPromptDialogTests` | 29 | owner bị khoá/bật lại, chọn sẵn + focus ô, Apply đúng (10, 800, " 120 ") và sai (9, 801, 0, -5, abc, rỗng, 12.5), thông báo + giãn + focus lại, Enter/Esc qua dialog manager, X/Alt+F4/Huỷ/đóng từ ngoài, WM_QUIT đăng lại, không owner/owner đã chết, dùng một lần, căn giữa owner, chrome gọi đúng một lần, màu WM_CTLCOLOR*, đổi DPI 100 %->150 %->100 %, thứ tự Tab + nút mặc định, tiếng Việt không bị cắt, không rò GDI/USER, ngoại lệ tới người gọi |
| `.../DiagnosticsAndListDialogTests` | 23 | 15 cặp nhãn/giá trị đúng thứ tự, 5 đường đóng, Sao chép (nội dung bảng, cửa sổ vẫn mở), giá trị dài tiếng Việt xuống dòng + không chồng nhau, SkippedFiles (dòng, màu cảnh báo, chỉ Đóng), BatchReview (xác nhận = OK; Huỷ/Esc/X/Alt+F4/đóng từ ngoài = Huỷ; Enter = nút mặc định như WPF), kéo giãn + kích thước tối thiểu, 50 000 dòng + cuộn ngang, tiếng Việt, chrome, không rò handle, đổi DPI gồm font tiêu đề |
| `.../SecondaryWindowRoutingTests` | 11 | router: cửa sổ Win32 không chạm cầu (cầu không bị nạp), cửa sổ chưa có -> cầu, cầu vắng -> cổng tạm (Settings mở config.json, Recovery/Benchmark báo), cầu nạp lỗi = vắng, Dispose đúng; cổng Win32: bảng `Supports`, Zoom OK/Huỷ, BatchReview true chỉ khi xác nhận + owner/modal, Dispose đóng hộp thoại đang mở rồi no-op, sai thread bị từ chối, Sao chép qua `IClipboardService` |
| `.../NativeDialogLoopTests` | 3 | trên UI thread thật: việc dispatcher mức Normal + bộ lọc phím vẫn chạy khi hộp thoại mở, WM_QUIT kết thúc cả vòng ngoài với đúng mã, đóng cổng từ callback |
| `.../NativeShellFileDialogTests` (`Category=Native`) | 4 | cờ mở/lưu (FILEMUSTEXIST/OVERWRITEPROMPT/FORCEFILESYSTEM), bộ lọc, đuôi mặc định, tên gợi ý, thư mục đầu; hiện thật đóng bằng WM_CLOSE -> null |
| `.../NativeDialogSnapshotTests` (`Category=Manual`) | 1 | chụp PNG mọi hộp thoại (en/vi, 100 % và 150 %) để nhìn bằng mắt |
| `WpfBridge.Tests/LazyLoadTests.NativeSecondaryWindows_OpenWithoutLoadingWpfOrTheBridge` | 1 | tiến trình sạch, DI thật: bốn cửa sổ Win32 mở/đóng mà KHÔNG nạp PresentationFramework/PresentationCore/WindowsBase/System.Xaml/App.WpfWindows/Shell.WpfBridge |
| `Shell.Integration.Tests/ShellNativeDialogsIntegrationTests` | 1 | shell thật, cầu vắng: Diagnostics (lớp `#32770`, owner = HWND shell, modal), zoom Huỷ/OK 150, đóng shell khi một hộp thoại còn mở: thoát mã 0 và hộp modal trả về |

Test cũ đã chỉnh vì cửa sổ nay là Win32 (không đổi kỳ vọng hành vi): `ShellWiringTests` (zoom OK đặt ô số/IDOK; "sai thread" dùng Recovery vì Diagnostics không còn
nạp cầu; cầu vắng vẫn mở hộp zoom), `ShellSecondaryWindowBridgeTests` (zoom OK), `ShellSecondaryWindowFallbackTests` (thông báo trạng thái dùng Recovery).

Chạy 2026-10-13 (desktop ẩn `tools/run-tests-hidden.ps1`, filter CI): `Shell.Tests` 1108, `Shell.Integration.Tests` 144, `Shell.WpfBridge.Tests` 39 (+2 thủ công `Report_*`),
`Architecture.Tests` 158, `App.Tests` 2555, `Integration.Tests` 1410 - 0 lỗi. Lặp bộ mới (`Dialogs.Native`, 70 ca/lượt): 50 lượt, 50 đạt, 0 lỗi (không lượt nào treo).
`i18n-check` PASS, `check-doc-links` PASS, `docs-budget -Check` PASS, `check-open-decisions` PASS.

## 4. Số đo

Đo trên `PhotoReview.exe` THẬT (bản probe tạm không commit: biến môi trường `PR_WP35_PROBE` ở `ShellStartup`, `--smoke` + một JPEG, owner = HWND shell, desktop ẩn),
2026-10-13, máy bận (đột biến/lặp test chạy song song nên số tuyệt đối đội lên). **Hai bản xen kẽ** R2R hybrid (publish mặc định) và Native AOT, 6 lượt mỗi bản
xen kẽ (R2R, AOT, R2R, AOT...), mỗi tiến trình mở 4 hộp thoại x 8 vòng. Đo từ lệnh mở tới khi cửa sổ đã tạo + bố cục + hiện (callback `ready`, trước khung vẽ đầu):

| Hộp thoại (ms) | R2R lần đầu: median / max (n=6) | R2R các lần sau: median / p95 (n=42) | AOT lần đầu: median / max | AOT các lần sau: median / p95 |
|---|---|---|---|---|
| ZoomPrompt | 22,7 / 27,7 | 6,1 / 7,3 | 18,2 / 24,5 | 5,8 / 7,3 |
| Diagnostics (15 hàng) | 45,7 / 65,2 | 30,4 / 34,0 | 32,8 / 33,9 | 29,9 / 32,8 |
| SkippedFiles (20 dòng) | 10,1 / 15,4 | 8,6 / 10,7 | 9,8 / 12,0 | 8,5 / 10,6 |
| BatchReview (20 dòng) | 14,4 / 18,0 | 10,2 / 11,7 | 10,2 / 12,5 | 9,9 / 12,3 |

Cổng chấp nhận "mở <= 150 ms": ĐẠT với dư rất lớn (tệ nhất 65 ms, Diagnostics lần đầu R2R). So với WPF: 170-215 ms (nóng), 470-690 ms (lần đầu) theo spike S3.
Diagnostics chậm nhất vì đo chữ 30 điều khiển hai lần (kích thước + bố cục); chấp nhận được, chưa tối ưu. Lưu ý đo tới lúc hiện, chưa tới khung vẽ đầu của chính
các điều khiển (do hệ thống vẽ ngay sau đó). Cổng "không nạp WPF": mục 3 (tiến trình sạch). ILC: `tools/check-aot-warnings.ps1 -RequireToolchain` =
**0 cảnh báo**, `PhotoReview.exe` AOT 11,1 MB (MSVC đã cài; thư mục publish tạm đã dọn).

## 5. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

@@MUTATION@@

## 6. Nhìn bằng mắt và phần CHƯA kiểm được bằng máy

**Đã nhìn bằng mắt** (ảnh PNG do `NativeDialogSnapshotTests` ghi bằng `PrintWindow` trên desktop ẩn, có kích hoạt comctl32 v6 như exe thật; en + vi; 100 % và
150 % mô phỏng bằng `WM_DPICHANGED`): nền cửa sổ `#171717`, chữ sáng, ô nhập `#252525` chữ trắng, nút `DarkMode_Explorer` tối viền sáng khi mặc định/focus, listbox tối,
dòng lỗi màu vàng `Dark.Warning`, bố cục không bị cắt với chuỗi tiếng Việt dài, nút không chồng nhau ở 150 %. Thấy được: dark của nút, ô nhập, listbox, static.

**Chưa kiểm được bằng máy / mắt** (ghi để người dùng kiểm tay):
- Title bar tối thật: ảnh chụp trên desktop ẩn không có DWM tổng hợp nên `PrintWindow` vẽ khung cổ điển (một lần chụp với `PW_RENDERFULLCONTENT` cho thấy thanh tiêu đề
  tối). Cần nhìn trên desktop tương tác (Windows 10 < 1809 dùng cờ 19: đã gọi cả hai).
- DPI hai màn hình thật (kéo hộp thoại giữa 100 % và 150-200 %): chỉ mô phỏng `WM_DPICHANGED` ở một màn hình; chưa kiểm font tiêu đề cửa sổ/khung theo hệ thống
  ở màn hình thứ hai và việc Windows tự gửi `WM_DPICHANGED` khi kéo.
- Combobox, trackbar, listview header ở chế độ tối: CHƯA gặp vì GĐ1 không dùng (spike S3 mục 4 "việc chưa kiểm"); cần ở GĐ2+ (Settings, Benchmark).
- Trợ năng thật (Narrator/UIA): nhãn là STATIC đứng ngay trước ô nhập nên UIA lấy làm tên, nhưng chưa chạy Narrator; dòng lỗi zoom không phát "live region"
  (WPF có `LiveSetting=Assertive`): người dùng Narrator chỉ nghe lỗi khi tự di chuyển tới dòng.
- IME/Telex trong ô số: ô số là EDIT chuẩn nên IME miễn phí, không có test.
- Bản Native AOT chạy thật: đã chạy `PhotoReview.exe` AOT (bản probe tạm, xem mục 4) mở bốn cửa sổ trên desktop ẩn; chưa thử bằng tay trên desktop tương tác.
- Diagnostics rất cao (nhiều dòng lịch sử dài) vượt vùng làm việc màn hình: cửa sổ không cuộn (WPF cũng không); chưa gặp thực tế.

## 7. Công sức so với ước và điều kiện dừng của spike

Điều kiện dừng của spike (công thực > 1,5 lần ước 35-50 giờ, hoặc dark không chấp nhận được): **chưa kích hoạt**. Quy mô thực: khung + 4 cửa sổ + cổng
+ tệp ~1 200 dòng `src/` (`NativeDialog` 790 dòng gồm chú thích), 2 400 dòng test; prototype ZoomPrompt của spike là 138 dòng nên khung đầy đủ lớn hơn ~8 lần
(DPI, dark, modal chuẩn, đo chữ, kéo giãn). Dark của nút/ô nhập/listbox/static chấp nhận được khi nhìn ảnh (mục 6); combobox/listview chưa dùng nên chưa
có số liệu để kích hoạt phương án "owner-draw riêng" hay đề xuất D. Đề xuất: tiếp GĐ2 (ActionProfiles + Benchmark modeless) vì khung đã có `RunNested`
cho cửa sổ modeless cạnh cầu WPF.

## 8. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: tiếp tục phương án B (hộp thoại Win32 gốc) sau GĐ1; cửa sổ chưa có (Settings, Recovery, Benchmark, ActionProfiles) giữ cầu WPF/cổng tạm.
- Thêm so với bản WPF: nút **Sao chép** ở Diagnostics (khoá `diagnostics.copy`); Enter đóng Diagnostics (nút Đóng là mặc định). Muốn bỏ: xoá `CopyId` và khoá.
- Phương án thay: (D) `PhotoReview.Settings.exe` đồng hành nếu GĐ2+ vượt ngân sách; (C) giữ cầu WPF cho mọi cửa sổ (đổi `Supports` của `Win32SecondaryWindowPort` về false).
- Hoàn tác: revert PR; hoặc chỉ tắt cổng Win32 bằng cách bỏ `new Win32SecondaryWindowPort(...)` trong `ShellCompositionRoot` (cầu WPF/cổng tạm trả lời như trước).
