---
id: NOWPF-WP35-G4
order: 1140
summary: |-
  WP-35 GĐ4 (phương án B của spike S3, CHƯA XÁC NHẬN): cửa sổ Recovery là hộp thoại Win32 gốc MODAL, chỉ là VIEW của RecoveryWindowPresenter (không đổi presenter, RecoveryFileCheck, RecoveryRetryService): danh sách đa chọn owner-draw có huy hiệu theo RecoveryTone, bộ lọc, chi tiết (sao chép/mở thư mục), kiểm tra nền có trạng thái/huỷ, Retry/Clear với xác nhận như WPF, từ chối đóng khi đang Retry. Cổng Win32 ưu tiên Recovery, cầu WPF chỉ còn Settings; bản AOT mở Recovery thật. Kiểm trên bản sao journal thật (Failed/Prepared/Superseded/Delete-failed): file nguồn/đích/journal đúng sau từng hành động. Mở <= 108 ms (R2R, máy yên), 0 cảnh báo ILC, không nạp WPF.
---

# NOWPF-WP35-G4 - Recovery bằng hộp thoại Win32 gốc (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G2-WIN32-DIALOGS](NOWPF-WP35-G2-WIN32-DIALOGS.md) theo lộ trình
GĐ0-5 của [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md). Logic không đổi: [NOWPF-WP35-G0-RECOVERY](NOWPF-WP35-G0-RECOVERY.md)
(`RecoveryWindowPresenter`, `RecoveryPresentation`/`RecoveryTone`; ADR 0003/0005/0007). Đây là gói AN TOÀN DỮ LIỆU (Retry = di chuyển/sao
chép tệp thật, Clear = ghi journal), nên chỉ có VIEW mới; bản WPF (`App.WpfWindows/RecoveryWindow`) và cầu WPF giữ nguyên làm lưới an toàn.

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| View | `Shell.Win32/Dialogs/Native/RecoveryDialog.cs` | `NativeDialog` modal kéo giãn + `IRecoveryCheckView`. Chép giá trị của presenter vào điều khiển và gọi presenter đúng chỗ WPF gọi; không tự quyết thứ tự/điều kiện nào. |
| Khung | `.../NativeDialog.cs` | `ListItemSubtitle`, `ListItemBadgeColor` (dòng phụ + màu huy hiệu theo tone), `CanCloseByUser` (từ chối X/Alt+F4/Esc khi Retry đang chạy; `Close()` từ ngoài luôn đóng), `WaitCursor` (WM_SETCURSOR), ô chỉ đọc vẽ như ô nhập. |
| Điều khiển | `.../NativeControls.cs` | `ListBoxControl.DrawItem` thêm dòng phụ + màu huy hiệu (mặc định không đổi). |
| Cổng | `.../Win32SecondaryWindowPort.cs` | `Supports(Recovery) = true`; `ShowRecovery` đọc journal (`TryReadRecoveryEntries`: IOException/UnauthorizedAccess -> thông báo, KHÔNG mở), `RecoveryRetryService.RetryMoveOrCopyAsync(entry, confirmedFinishCancelled: true)` (cửa sổ tự hỏi mục bị huỷ trước), `OperationJournal.Dismiss`, cài đặt xoá vĩnh viễn đọc LÚC HỎI, clipboard của shell; `Dispose` đóng Recovery đang mở. |

Chuỗi: không khoá catalog mới; mọi chữ là `Tr.Recovery*`, `Tr.Dialog*`, `Tr.Common*` có sẵn (`i18n-check` PASS).

## 2. Thiết kế

1. **Một nguồn sự thật**: `RecoveryDialog` giữ `RecoveryWindowPresenter` (hàng, bộ lọc, `Shown`, `ButtonStates`, `Details`, `RunChecksAsync`, `DismissPrompt`,
   `Dismiss`, `CanStartRetry`, `RetryConfirmText`, `ExecuteRetryAsync`, `DescribeRetry`, `CanClose`). View không so sánh trạng thái journal/tệp.
2. **Thứ tự của Retry giữ nguyên**: điều kiện bắt đầu -> xác nhận (TaskDialog Yes/No, văn bản của presenter) -> MỘT lần gọi delegate -> `SetBusy` (khoá
   Retry/Clear, con trỏ chờ) -> thông báo theo `DescribeRetry` -> đóng (thành công) / kiểm tra lại (bị từ chối) / chỉ thông tin (Superseded).
3. **Clear**: xác nhận TRƯỚC khi gọi delegate (`DismissPrompt` = null thì không làm gì); lỗi từ journal = báo lỗi, KHÔNG xoá hàng nào; hàng cũ bị bỏ qua được nói rõ.
   Clear selected dùng đúng hàng đang chọn; Clear all dùng mọi hàng kể cả hàng bị bộ lọc ẩn (như WPF).
4. **Kiểm tra nền**: bắt đầu khi mở (như `Loaded` của WPF) và bởi Re-check; kết quả về UI thread qua bối cảnh đồng bộ của shell; đóng giữa chừng =
   `CancelCheck` + gỡ đăng ký `ResultChanged`, lượt kiểm tra kết thúc bằng đường huỷ bình thường, không ghi gì vào hàng.
5. **Đóng**: Esc/Close/X bị từ chối khi đang Retry (R2-A-01, như `CanClose` của WPF); shell thoát khi Recovery mở (cả khi đang Retry) vẫn đóng cửa sổ, kết quả
   đến muộn chỉ ghi nhật ký, không chạm view đã huỷ.
6. **Cổng ưu tiên**: router chọn cổng Win32 trước; cầu WPF chỉ còn cho Settings. Bản AOT: Recovery mở THẬT (không còn "cổng tạm báo trạng thái").

## 3. Kiểm chứng

| Test (`tests/`) | Ca | Phủ |
|---|---:|---|
| `Shell.Tests/Dialogs/Native/RecoveryDialogTests` | 43 | mở (hàng trước kết quả = huy hiệu "đang kiểm" trung tính), journal rỗng, huy hiệu + màu theo tone, lọc (đúng hàng, rỗng, giữ chọn), đa chọn (Clear selected bật, Retry tắt, không chi tiết), Retry chỉ cho một hàng retry được, không delegate thì nút tắt, chi tiết (verdict/đường dẫn/trạng thái/lỗi journal/nhóm/không đích), cập nhật khi kết quả kiểm đến, Copy path, Show in Explorer (tệp / thư mục gần nhất / lỗi Explorer), Re-check, đóng giữa kiểm = huỷ, lỗi checker không kẹt nút, Retry (từ chối xác nhận, thành công, bị từ chối, Superseded, delegate ném), khoá nút + từ chối đóng + con trỏ chờ khi Retry, đóng SHELL giữa Retry, Clear (huỷ/xác nhận/hàng ẩn/hàng cũ/lỗi journal), modal + Esc, bố cục 100/150 % en/vi, kéo giãn, dark + chrome một lần, không rò GDI/USER, mọi chữ qua Localizer |
| `.../RecoveryDialogDataSafetyTests` | 17 | **bản sao journal thật** trong thư mục tạm `wp35g4-recovery-*` (không `IRecycleBin`): Retry Move/Copy thật (nguồn/đích/journal), từ chối xác nhận = không đổi tệp/journal, nguồn đổi sau lúc kiểm bị `RecoveryRetryService` từ chối và không di chuyển gì, mục đã Dismiss nơi khác = Superseded không chạm tệp, mục bị người dùng huỷ cần xác nhận riêng, Clear selected/all chỉ ghi dòng journal (Failed/Prepared/Delete-failed), Clear bị huỷ giữ nguyên, hàng giải quyết ở nơi khác báo cũ, cảnh báo xoá vĩnh viễn theo cài đặt đọc lúc hỏi; cổng: owner + modal, không journal, journal không đọc được, Dispose, clipboard |
| `.../RecoveryDialogGapTests` | 18 | các ca lấp đột biến sống (mục 5): cuộn, màu đích, IDCANCEL không là Yes, Clear rỗng, Retry đúng hàng, con trỏ chờ, focus, Explorer, cổng: retry/Clear/xoá vĩnh viễn |
| `.../RecoveryRowDrawingTests` | 3 | vẽ dòng vào DC bộ nhớ: tiêu đề + dòng phụ + huy hiệu màu tone, màu chọn, nền viên thuốc |
| `.../SecondaryWindowRoutingTests` | (chỉnh) | Recovery do cổng Win32 nhận, cầu chỉ còn Settings; AOT: Recovery mở thật |
| `Shell.WpfBridge.Tests/LazyLoadTests` | (chỉnh) | tiến trình sạch: Recovery mở/đóng, KHÔNG nạp WPF/cầu; `ShellWiringTests`: cầu vắng vẫn mở Recovery |
| `Shell.Integration.Tests/ShellNativeDialogsIntegrationTests` + `ShellSecondaryWindowFallbackTests` | +1 (+1 chỉnh) | shell thật: owner = HWND shell, modal, kiểm nền qua bối cảnh đồng bộ thật (Retry bật), không đổi tệp/journal, đóng SHELL khi Recovery mở: thoát mã 0, không treo; cầu vắng: Recovery mở, không chạm launcher |

Chạy 2026-10-13 (desktop ẩn `PRWp35g4`, filter CI): `Shell.Tests` 1 283 (1 265 trước khi lấp đột biến), `Shell.Integration.Tests` 146, `Shell.WpfBridge.Tests` 39, `Architecture.Tests` 158, `Core.Tests` 3 202, `Imaging.Tests` 4 181, `App.Tests` 2 582, `Integration.Tests` 1 343: 0 lỗi thật. Hai ca thời gian đỏ MỘT lần khi bộ chạy song song với đột biến (`App.Tests` "STALE: two files, thumbnail and preview..." và `Core.Tests` `SessionWriterTests.Flush_WriterBusy_ReturnsAfterBoundedWaitAndRequeues`), cả hai xanh khi chạy lại (3/3 và 37/37); không chạm mã của gói. Lặp bộ mới (`RecoveryDialog*`, `RecoveryRow*`, `SecondaryWindowRouting`, 93 ca/lượt sau khi lấp đột biến, desktop ẩn `PRWp35g4loop`; 50 lượt, 50 đạt, 0 lỗi (không lượt nào treo).

## 4. Số đo

Đo trên `PhotoReview.exe` THẬT (probe tạm KHÔNG commit: biến môi trường `PR_WP35G4_PROBE`, `--smoke` + một JPEG, dữ liệu cô lập `PHOTOREVIEW_DATA_ROOT`
với 12 mục journal Failed/Prepared + 12 tệp nguồn, desktop ẩn `PRWp35g4run`, owner = HWND shell). Đo từ lệnh `ShowRecovery` (đọc journal + dựng + hiện) tới
lần chạy đầu của dispatcher trong vòng modal (cửa sổ đã hiện, kiểm tra nền đã khởi động), 8 vòng/tiến trình:

| Recovery modal, 12 mục (ms) | lần đầu: median / max | các lần sau: median / p95 | ghi chú |
|---|---|---|---|
| R2R hybrid, máy yên (6 tiến trình x 8) | 101,9 / 108,5 (n=5) | 58,0 / 76,6 (n=40) | một tiến trình gặp 563 ms rồi 419 ms (một lần chặn của máy, các lần sau 50-70 ms): loại khỏi cột, ghi riêng |
| Native AOT, máy yên (3 tiến trình x 8) | 54,6 / 58,0 (n=3) | 39,1 / 48,3 (n=21) | |
| Cả hai bản khi đột biến/lặp test chạy song song (máy bận) | R2R 95-265 (n=6), AOT 68-628 (n=3) | R2R ~55-185, AOT ~35-140 | số tuyệt đối đội lên; chỉ để thấy cận trên |

Cổng "mở <= 150 ms": ĐẠT khi máy yên (R2R lần đầu 102 ms median, 109 ms tệ nhất khi máy yên; AOT 55 ms; máy bận có lần đầu 265 ms (R2R) và 628 ms (AOT): cổng chỉ chắc chắn khi máy không bị tải, giống GĐ1/GĐ2). Cổng "không nạp WPF": `LazyLoadTests` (tiến trình sạch, DI thật). ILC:
`tools/check-aot-warnings.ps1 -RequireToolchain` = **0 cảnh báo**, `PhotoReview.exe` AOT 12,1 MB (MSVC từ vswhere trên PATH; thư mục publish tạm đã dọn).
Không treo khi đóng shell lúc Recovery mở / đang Retry: `ShellNativeDialogsIntegrationTests` + `RecoveryDialogTests.ClosingTheShellMidRetry_...`.

## 5. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

74 đột biến thủ công trong `RecoveryDialog.cs`, phần Recovery của `Win32SecondaryWindowPort.cs` và 2 dòng `NativeDialog.cs` (worktree riêng, build Release,
hoàn nguyên sau mỗi đột biến; điều kiện không hằng `Environment.ProcessorCount < 0` thay `if (false)` vì cảnh báo là lỗi). Bước 1 = `Shell.Tests` lọc
`Dialogs.Native.Recovery|SecondaryWindowRouting` (75 ca); bước 2 (chỉ kẻ sống) = toàn `Shell.Tests` + toàn `Shell.Integration.Tests`.
Nhóm phủ: lọc, màu (ToColorRef/tone nguồn-đích/huy hiệu), nút bật/tắt, xác nhận (Confirm luôn Yes / bỏ Confirm / IDCANCEL), Retry/Dismiss đúng mục,
Clear phạm vi (selected/all), huỷ khi đóng (CancelCheck, CanCloseByUser, Alive), bảng lỗi/thông báo, Copy/Explorer, cổng (đọc journal, confirmedFinishCancelled,
Dismiss, cài đặt xoá vĩnh viễn, clipboard).

Kết quả: **49 chết ở bước 1**; **18 sống ở cả hai bước** và đều là lỗ hổng test thật, đã thêm 18 ca `RecoveryDialogGapTests` (+ `ConfirmationResult` cho
`FakeMessages`) và chạy lại: cả 18 bị diệt; **7 tương đương/không quan sát được**; còn sống thật: 0.

| # | Đột biến sống bước 1+2 | Test lấp |
|---|---|---|
| 4 | RefreshEntries bỏ khôi phục vị trí cuộn | `Refresh_KeepsTheListScrolledWhereTheUserLeftIt` |
| 8 | màu trạng thái đích dùng tone nguồn | `DestinationStatus_IsColouredByTheDestinationsOwnTone_NotTheSources` |
| 20 | Confirm so `!= IdNo` (đóng hộp = Yes) | `AConfirmationClosedWithoutAnswering_IsNotAYes_ForRetryOrClear` |
| 21 | DismissPrompt null bị bỏ qua | `ClearSelected_WithNothingSelected_AsksNothing_AndDismissesNothing` |
| 22 | Retry dùng hàng đầu thay vì hàng chọn | `Retry_RunsTheSelectedRow_NotTheFirstOne` |
| 42 | SetBusy không đặt con trỏ chờ ngay | `TheWaitCursorIsSetTheMomentTheRetryStarts_AndTheArrowTheMomentItEnds` |
| 47 | CheckCompleted không làm mới danh sách | `WhenTheCheckCompletes_TheListIsFilteredAgainByTheNewVerdicts` |
| 50 | cổng: Retry `confirmedFinishCancelled: false` | `Port_Retry_ConfirmsAUserCancelledEntryExplicitly_SoTheServiceFinishesIt` |
| 51 | cổng: Dismiss không gọi `journal.Dismiss` | `Port_Clear_WritesTheDismissalToTheRealJournal` |
| 52 | cổng: cho phép xoá vĩnh viễn luôn true | `Port_RetryQuestion_WarnsAboutPermanentDeletion_OnlyWhenTheSettingAllowsIt` |
| 58 | kết quả kiểm đến không cập nhật chi tiết hàng chọn | `TheSelectedRowsDetails_UpdateAsSoonAsItsResultArrives_NotOnlyWhenTheWholeCheckEnds` |
| 60 | bỏ chuyển focus khi nút đang giữ focus bị khoá | `WhenTheFocusedButtonGetsLocked_TheFocusMovesToTheList` |
| 68 | catch Explorer chỉ Win32Exception | `ShowInExplorer_WhenExplorerReportsAnInvalidOperation_...` |
| 69 | Copy bỏ guard đường dẫn rỗng | `CopyAndShowInExplorer_WithNoPathShown_DoNothing` |
| 71 | bỏ `!Alive` sau thông báo (kiểm tra lại trên cửa sổ đã đóng) | `Retry_WhenTheWindowCloseWhileTheNoticeIsUp_DoesNotCheckAgainOnADeadWindow` |
| 72 | dòng Action luôn được thêm | `ActionLine_IsLeftOutWhenTheVerdictHasNoActionText` |
| 73 | Dismiss bỏ guard `Hwnd == 0` | `ClearSelected_WhenTheWindowCloseDuringTheQuestion_StaysQuietAboutStaleEntries` |

Tương đương (lý do): #16 gửi `count>0?1:0` (presenter chỉ dùng `> 0`); #29 không gỡ `ResultChanged` khi đóng (handler thoát sớm vì HWND = 0, chỉ là tham chiếu
chu trình); #54 bỏ `_disposed` ở `ShowRecovery` (`Run` có guard riêng); #57 luôn cập nhật chi tiết (idempotent); #61 bỏ chuyển focus sang bộ lọc khi lọc rỗng
(Windows tự chuyển focus khi list bị ẩn; test giữ lại); #67 bỏ guard đường dẫn rỗng của Show in Explorer (`NearestExistingFolder("")` = null); #74 Dismiss lỗi
không `return` (chỉ thêm RefreshEntries vô hại vì SkippedCount = 0).

## 6. Hành vi so với WPF và phần CHƯA kiểm

**Tương đương** (kiểm bằng 55 + 28 ca presenter còn nguyên và test view ở trên): thứ tự/điều kiện kiểm nền, Clear, Retry; văn bản xác nhận/thông báo;
bảng nút; từ chối đóng khi Retry; Clear all gồm hàng bị lọc ẩn; Retry/Clear bị khoá khi bận.

**Khác (chỉ view, CHƯA XÁC NHẬN)**: chi tiết = nhãn verdict + dòng lỗi + hai khối đường dẫn (ô chỉ đọc, xuống dòng tối đa 6 dòng) + MỘT ô cuộn chỉ đọc
cho giải thích/hành động/thành viên nhóm (WPF xếp các TextBlock/ItemsControl riêng; cùng chữ, khác dáng); hộp xác nhận/thông báo là `TaskDialog`
(Yes/No không biểu tượng, Information, cảnh báo) thay `MessageBox` (cảnh báo giữ biểu tượng Warning như WPF); Esc/X/Close bị từ chối khi đang Retry
(WPF: `OnClosing` huỷ; cùng kết quả: cửa sổ không đóng). Muốn bỏ phần khác: sửa `BuildDetailsText`/`LayoutDetails`.

**Quan sát (KHÔNG đổi, CHƯA XÁC NHẬN)**: `RecoveryWindowPresenter.CanStartRetry` (đóng băng, như WPF) không kiểm phán quyết; chỉ nút bị khoá chặn Retry trên hàng AlreadyDone/Lost. View giữ nguyên hành vi đó; nếu muốn chặn thêm ở presenter thì đó là đổi hợp đồng GĐ0, ngoài phạm vi.

**Chưa kiểm được bằng máy / mắt**: title bar tối + DPI hai màn hình thật (như GĐ1/GĐ2); Narrator/UIA (nhãn các ô đường dẫn đến từ tooltip + nhãn đứng trước);
dòng trạng thái kiểm không phải live region; chạy tay trên desktop tương tác; hành vi Explorer thật (test dùng seam, `explorer.exe` không được khởi động).
Lưu ý môi trường: `ZoomPromptDialogTests.RepeatedOpenAndClose_LeaksNoGdiOrUserObjects` (GĐ1) đỏ khi chạy trên desktop TƯƠNG TÁC của máy này, kể cả trên mã nền
#490 (đã đối chiếu bằng worktree riêng tại 26668912, nên không do gói này), nhưng xanh trên desktop ẩn (`tools/run-tests-hidden.ps1`); chưa điều tra nguyên nhân.

## 7. Công sức và điều kiện dừng

Điều kiện dừng (hành vi an toàn dữ liệu KHÔNG tương đương, hoặc công > 1,5 lần ước): **không kích hoạt**. Quy mô: ~1 000 dòng `RecoveryDialog.cs` + ~90 dòng khung,
~2 150 dòng test. Không đụng presenter/checker/service, không đụng Thùng rác thật.

## 8. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: tiếp tục phương án B sau GĐ4; GĐ3 (Settings theo trang) do agent khác; GĐ5 (gỡ cầu) chưa làm.
- Hoàn tác: bỏ `SecondaryWindowKind.Recovery` khỏi `Win32SecondaryWindowPort.Supports` (Recovery về cầu WPF, không đổi code khác), hoặc revert PR.
