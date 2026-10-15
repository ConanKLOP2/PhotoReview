---
id: FEAT-RENAME-UI
order: 1430
summary: |-
  PR3 "UI" của FEAT-RENAME-DESIGN (CHƯA XÁC NHẬN, Q2 theo khuyến nghị): người dùng đổi tên ảnh đang xem bằng menu chuột phải ("Rename...") và phím F2 trong shell Win32 (chỉ Win32): ContextMenuItemId.RenameFile (cuối enum, Win32-only), ShortcutMappings.RenameFile (tuỳ chọn, mặc định F2), RenamePromptDialog (ô tên không đuôi, chọn sẵn, đuôi cố định, kiểm hợp lệ ngay khi gõ), MainViewModel.PromptRenameCurrentAsync. WPF không sửa XAML. Bảng đột biến hai bước ở mục 5.
---

# FEAT-RENAME-UI — Giao diện đổi tên tệp (PR3, 2026-10-15, CHƯA XÁC NHẬN)

PR3 của [`FEAT-RENAME-DESIGN.md`](FEAT-RENAME-DESIGN.md), dựa trên lõi [`FEAT-RENAME-CORE.md`](FEAT-RENAME-CORE.md) (#527) và
trạng thái [`FEAT-RENAME-STATE.md`](FEAT-RENAME-STATE.md) (#529). Yêu cầu gốc của người dùng: "Rename bằng menu chuột phải."
Chỉ shell Win32 (Q5): **không** sửa XAML/`PhotoReview.App`; mã dùng chung vẫn biên dịch, test WPF/App.Tests xanh.
Chưa có: dò sidecar nâng cao (PR4), mục "Rename" ở thanh công cụ/Tools (không có trong thiết kế).

## 1. Quyết định (CHƯA XÁC NHẬN, theo khuyến nghị) và cách hoàn tác

| ID | Quyết định | Phương án khác | Hoàn tác |
|---|---|---|---|
| RU-1 (Q2) | F2 là phím mặc định của "Rename file" (`ShortcutMappings.RenameFile = "F2"`, tuỳ chọn, xoá được ở Settings > Shortcuts). F2 trước đó chưa gán cho gì | Để trống mặc định (an toàn cho người cũ, khó tìm) | Đặt mặc định `""` trong `ShortcutMappings` (hoặc người dùng xoá phím); mục menu vẫn dùng được |
| RU-2 | Không hộp xác nhận: hộp nhập tên là bước xác nhận, đã có Undo (Ctrl+Z) | Hỏi "Đổi tên?" | không có gì để gỡ |
| RU-3 | Ảnh ở nguyên chỗ (đã làm ở PR2, RS-1) | - | - |
| RU-4 | Mục menu `RenameFile` là **Win32-only**: cờ `ContextMenuItemInfo.ShellOnly` + `ContextMenuContext.IncludeShellOnly` (mặc định false = bố cục WPF, không đếm mục này, không để lại dấu phân cách thừa). Cửa sổ Settings WPF không có ô tick cho nó và mang giữ lựa chọn khi lưu | Hiện cả ở WPF (cần sửa XAML) | Bỏ dòng `RenameFile` khỏi `ContextMenuItems.Items`/builder; tên cũ trong `HiddenContextMenuItems` bị bỏ qua khi đọc |
| RU-5 | Kiểm "trùng tên" khi gõ chỉ hỏi danh sách ảnh (catalog, không I/O); lần xác nhận cuối mới hỏi thêm đĩa (một `FileExists`). Tệp ngoài danh sách (xmp, định dạng khác) chỉ lộ ra ở lần xác nhận | Hỏi đĩa mỗi lần gõ (I/O trên UI thread, sai nguyên tắc INV-1) | đổi lambda `nameTaken` ở `MainViewModel.PromptRenameCurrentAsync` |
| RU-6 | Tên giống tên cũ hoặc chỉ đổi hoa-thường **không** là lỗi: nút "Rename" bật; tên giống hệt trả `NoChange` (thanh trạng thái, không journal) | Tắt nút khi không đổi | đổi nhánh `IsUnchanged` ở `RenamePromptPresenter` |
| RU-7 | Thất bại (tên bị chiếm, tệp bị khoá...) chỉ hiện ở thanh trạng thái (`status.renameFailed`, đã có từ PR2), không thêm hộp lỗi: không có mẫu "hộp lỗi" cho thao tác tệp ở Win32 và không làm gián đoạn | Hộp lỗi modal | thêm `ShowError` ở `PromptRenameCurrentAsync` |

## 2. Thay đổi theo lớp

| Lớp | Thay đổi |
|---|---|
| Core | `ContextMenuItemId.RenameFile` (giá trị 18, cuối enum), dòng `Items` (nhóm 5, `NeedsPhoto`, `ShellOnly`), `ShortcutMappings.RenameFile` (tuỳ chọn, F2) + `OptionalNames` + trim ở `SettingsNormalizer`, `RenamePromptRequest`, `IDialogService.PromptRename` (mặc định null: WPF không có) |
| App.Shared | `ReviewCommandType.RenameFile` (bỏ lặp phím), `ShortcutRouter` (sau cổng `hasImage`, **chỉ khi không modifier**; Action trùng phím thắng), `ContextMenuModelBuilder` (Command + chữ phím), `SettingsFieldMap` (hàng phím), `SettingsPresenter` (nhãn, `ContextMenuRows(includeShellOnly)`), `RenamePromptPresenter` (ánh xạ mã lỗi `RenameNameProblem` sang chuỗi i18n; "trùng tên" là câu hỏi tiêm vào), `MainViewModel.PromptRenameCurrentAsync` (mở hộp, bỏ nếu ảnh đã đổi trong lúc hộp mở, rồi `RenameCurrentAsync`) |
| Shell.Win32 | `RenamePromptDialog` (mẫu `ZoomPromptDialog`: ô đơn dòng được `EditTextCentering` canh giữa, chọn sẵn tên không đuôi, đuôi cố định bên phải, lỗi tại chỗ dưới ô + nút bị tắt, EN_CHANGE kiểm ngay; OK kiểm lại với `isCommit`), `ISecondaryWindowPort.PromptRename` + `SecondaryWindowKind.RenamePrompt` (+ `Supports`), `Win32DialogService`, `ShellMainWindow.Input` (nhánh `RenameFile`, dùng chung F2 và menu), `ContextMenuController` (`RenameFile` -> `ExecuteAsync`), trang Shortcuts (hàng mới), trang Mouse (ô tick tự có) |
| WpfWindows | `SettingsWindow.ReadFields`: mang giữ phím `RenameFile` và lựa chọn các mục Win32-only (cửa sổ WPF không có ô nhập/ô tick cho chúng; thiếu thì lưu từ WPF sẽ ẩn mục trong menu Win32). Không XAML |
| i18n | 16 khoá thêm cuối en/vi/notes: `main.menu.renameFile` (+automation), `settings.contextMenu.item.renameFile`, `settings.shortcut.renameFile` (+clear), `dialog.rename.title/label/ok`, `dialog.rename.error.*` (8) |

Luồng: F2 / "Rename..." -> `ReviewCommand.RenameFile` -> `ShellMainWindow.ExecuteAsync` -> `MainViewModel.PromptRenameCurrentAsync` ->
`IDialogService.PromptRename` (modal trên luồng UI, ADR 0005) -> `RenameCurrentAsync(newName)` (PR2) -> thanh trạng thái `status.renamed`
(`NoChange` = `status.renameUnchanged`; thất bại = `status.renameFailed`). Chế độ không có ảnh: mục menu bị ẩn (NeedsPhoto), F2 không làm gì.
So sánh (compare) đang hiện: đổi tên ảnh đang chọn (hành vi PR2: bỏ compare và present lại).

## 3. Phát hiện khi làm

- Cổng `Win32SecondaryWindowPort.Supports` phải liệt kê loại cửa sổ mới, nếu không router im lặng trả null (hộp không bao giờ mở): chỉ test tích hợp
  đầu-cuối (F2 -> hộp -> tệp thật) bắt được; test đơn vị của hộp thoại vẫn xanh.
- Mục menu trong bảng Core dùng chung với bản WPF (đã đóng băng): không có cờ `ShellOnly`, WPF sẽ thêm dấu phân cách thừa ở nhóm 5 khi tệp copy bị ẩn
  (golden `ContextMenu_CommittedGolden_MatchesWpf` bắt được). Golden WPF giữ nguyên; test Win32 so với golden với mục này bị ẩn và có test riêng.
- F2 mặc định va chạm với test cũ gán F2 cho phím khác (Settings WPF `OpenFolder`, trang Shortcuts `Compare`, fuzz save/load): sửa test, không đổi hành vi.
- WPF `MainWindow` vẫn nhận lệnh `RenameFile` từ router dùng chung và bỏ qua (nuốt phím F2, như `OpenInDefaultEditor`).

## 4. Kiểm thử

Core: `RenameFileSettingsTests` (enum cuối, NeedsPhoto/ShellOnly, bố cục WPF không đếm mục, F2 mặc định, cấu hình cũ, xung đột với Action, validator).
App: `RenameFileShortcutRouterTests`, `RenamePromptPresenterTests`, `MainViewModelRenameTests` (+6 ca `PromptRenameCurrentAsync`: hộp -> đổi tên -> Undo,
huỷ, cùng tên, bảng hợp lệ + không I/O khi gõ, không có thư mục, ảnh đã đổi khi hộp mở). Shell: `RenamePromptDialogTests` (bảng hợp lệ, chọn sẵn tên,
đuôi cố định, Enter/Esc, trùng tên, bố cục lỗi, và ảnh BitBlt của hộp thoại + menu ở 96/144 DPI, en/vi, khẳng định không phẳng một màu),
`RenameFileMenuModelTests`, `DialogEditCenteringTests` (+hộp rename), `ContextMenuControllerTests`/`ModelBuilderTests`/golden. Integration:
`SettingsWindowContextMenuTests` (WPF giữ lựa chọn mục Win32-only). Shell.Integration: `ShellRenameEndToEndTests` (shell thật trên desktop ẩn: F2 mở
hộp, gõ tên sai/đúng, OK -> tệp thật đổi tên, catalog/thông tin/trạng thái cập nhật, Undo trả tên cũ, mục menu mở cùng hộp, Huỷ không đổi gì).
Không dùng SendInput/SetCursorPos/SetForegroundWindow: tin nhắn cửa sổ trong tiến trình test, chỉ chạy trên desktop ẩn (`tools/run-tests-hidden.ps1`).

## 5. Đột biến thủ công (hai bước, `docs/MUTATION-TESTING.md`)

33 mutant (U1-U33), mọi mutant biên dịch: **31 killed bước 1, 2 sống ở lượt đầu (U16, U32), 0 tương đương đã xác nhận**. Sau đó thêm test cho U16
(`MainViewModelRenameTests.PromptRenameCurrentAsync_ACaptureGroup_IsValidatedWithTheGroupsPlan_NotTheJpegAlone`, sidecar không đặt tên theo ảnh) và U32
(`RenameFileSettingsTests.TheShortcut_IsTrimmed_AndANullBecomesCleared`; U32 sống vì `ShortcutKeyCanonical` cũng cắt khoảng trắng nên dòng trim ở
`SettingsNormalizer` gần như thừa: giữ làm phòng thủ). Hai test mới chưa chạy lại dưới đột biến (chỉ chạy xanh trên mã thật).

| Id | Vị trí | Đột biến | Kết quả | Test bắt được |
|---|---|---|---|---|
| U1 | ContextMenuLayout.Items | bỏ `ShellOnly` | killed 1 | RenameFileSettingsTests.TheWpfLayout_NeverCountsTheWin32OnlyItem... |
| U2 | ContextMenuLayout.Items | `NeedsPhoto: false` | killed 1 | RenameFileSettingsTests (NeedsPhoto, WithAndWithoutAPhoto) |
| U3 | ContextMenuItems.ItemShows | bỏ kiểm `IncludeShellOnly` | killed 1 | RenameFileSettingsTests.TheWpfLayout... |
| U4 | ContextMenuModelBuilder.Build | `IncludeShellOnly: false` | killed 1 | ContextMenuModelBuilderTests (10 đỏ) |
| U5 | ContextMenuModelBuilder | không chữ phím | killed 1 | RenameFileMenuModelTests.WithAPhoto_..., ARebind_... |
| U6 | ContextMenuController | RenameFile không nối | killed 1 | ContextMenuControllerTests.ChoosingAnItem_... |
| U7 | ShellMainWindow.Input | bỏ nhánh RenameFile (F2/menu không nối) | killed 1 | ShellRenameEndToEndTests |
| U8 | ShortcutRouter | bỏ khối RenameFile | killed 1 | RenameFileShortcutRouterTests |
| U9 | ShortcutRouter | bỏ `noModifier` | killed 1 | RenameFileShortcutRouterTests.WithAModifier_DoesNotRename |
| U10 | ShortcutMappings | mặc định `""` | killed 1 | RenameFileSettingsTests, router tests |
| U11 | ShortcutMappings.OptionalNames | bỏ RenameFile | killed 1 | RenameFileSettingsTests |
| U12 | ReviewCommand.IgnoresAutoRepeat | bỏ RenameFile | killed 1 | RenameFileShortcutRouterTests.HoldingTheKey... |
| U13 | MainViewModel.PromptRenameCurrentAsync | bỏ chốt "ảnh đã đổi" | killed 1 | ..._TheViewerMovedOnWhileTheBoxWasOpen_RenamesNothing |
| U14 | MainViewModel nameTaken | chỉ hỏi đĩa | killed 1 | ..._ValidateFollowsThePolicy_AndTypingNeverTouchesTheDisk |
| U15 | MainViewModel nameTaken | chỉ hỏi catalog | killed 1 | cùng test |
| U16 | MainViewModel | bỏ kế hoạch nhóm | SỐNG -> thêm test | ..._ACaptureGroup_IsValidatedWithTheGroupsPlan... |
| U17 | RenamePromptPresenter | bỏ miễn trừ cùng tên/hoa-thường | killed 1 | RenamePromptPresenterTests.NotRefused_... |
| U18 | RenamePromptPresenter | đổi ánh xạ lỗi | killed 1 | RenamePromptPresenterTests.EveryPolicyProblem... |
| U19 | RenamePromptPresenter | tên có đuôi | killed 1 | RenamePromptPresenterTests.Initial_... |
| U20 | RenamePromptDialog | không chọn sẵn tên | killed 1 | RenamePromptDialogTests.Opens_WithTheStemSelected... |
| U21 | RenamePromptDialog | bỏ kiểm khi gõ (EN_CHANGE) | killed 1 | RenamePromptDialogTests (23 đỏ) |
| U22 | RenamePromptDialog | không tắt OK khi lỗi | killed 1 | RenamePromptDialogTests.InvalidNames_... (18 đỏ) |
| U23 | RenamePromptDialog.OnOk | bỏ kiểm lúc xác nhận | killed 1 | ..._ANameThatIsTaken_... |
| U24 | RenamePromptDialog.OnOk | `Value = null` | killed 1 | Enter_InTheBox_..., ValidNames_... |
| U25 | RenamePromptDialog | không hiện đuôi | killed 1 | Dialog_PaintsTheBox... (BitBlt) |
| U26 | Win32SecondaryWindowPort.Supports | bỏ RenamePrompt | killed 1 | SecondaryWindowRoutingTests |
| U27 | Win32DialogService.PromptRename | `=> null` | killed 1 | ShellRenameEndToEndTests |
| U28 | WPF SettingsWindow | bỏ mang giữ mục Win32-only | killed 1 | SettingsWindowContextMenuTests |
| U29 | SettingsPresenter.ContextMenuRows | bỏ lọc ShellOnly | killed 1 | SettingsPresenterTests |
| U30 | ShortcutsSettingsPage | bỏ hàng | killed 1 | SettingsShortcutsPageTests |
| U31 | SettingsFieldMap | bỏ ánh xạ | killed 1 | SettingsFieldMapTests |
| U32 | SettingsNormalizer | bỏ trim | SỐNG (gần tương đương) -> thêm test | RenameFileSettingsTests.TheShortcut_IsTrimmed... |
| U33 | MouseSettingsPage | không liệt kê mục Win32-only | killed 1 | MouseSettingsPageTests |

Ghi chú: một lần chạy bước 2 của U32 đỏ bởi `ZoomDetailTests.NavigatingAway_...` (chập chờn, không liên quan; cũng thấy `RandomNavigationSequence`
seed 1 đỏ một lần ở lượt đầu rồi xanh).

## 6. Chưa kiểm

- Ảnh chụp và thao tác bằng tay trên desktop thật (chuột phải, F2, gõ tên có dấu, IME) và phối hợp với Explorer đang mở thư mục.
- Tên có nhóm RAW+JPEG+XMP với hộp thoại thật (nhóm đã có test ở PR1/PR2 qua `RenameCurrentAsync`; hộp thoại dùng `CaptureGroupRenamePlan.Check`).
- Tệp bị khoá bởi trình sửa ngoài (thông điệp thất bại chung, không có gợi ý "đóng ứng dụng").
- Đường dẫn dài hơn MAX_PATH khi tiến trình long-path-aware (chính sách hiện coi như không).
