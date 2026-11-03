---
id: RP-JOURNAL-NOTICE-01
order: 1460
summary: |-
  Open (from the Rust port review): when a file action succeeds but its journal line could not be saved (JournalPersisted == false) the file-action path logs nothing and tells the user nothing; the recommended fix is one error log line plus one new status-bar catalog key, which the Rust port needs to sync (P-72 option C, blocked on P-84).
---

# RP-JOURNAL-NOTICE-01 - thao tác tệp thành công nhưng không ghi được nhật ký thì im lặng

Nguồn: rà soát khi port sang Rust (P-72, bị chặn bởi P-84). Chưa có thay đổi mã ở C#.

## Hiện trạng trong C#

- `FileActionResult.JournalPersisted` (mặc định `true`, `FileActionResult.cs:17`) và `CaptureGroupActionResult.JournalPersisted` (`CaptureGroupActionResult.cs:24`)
  được đặt `false` khi dòng Committed/Failed không ghi được: `FileActionService.cs:269` (nhóm, thành công), `:303` (nhóm, thất bại), `:717`
  (Copy/Move đơn), `:779` (Recycle đơn), `:837` (đơn, thất bại). Lỗi đi kèm nằm ở `JournalError`.
- Nơi duy nhất đọc cờ này ngoài tầng Core: `src/PhotoReview.App.Shared/Presenters/RecoveryWindowPresenter.cs:325`
  (`journalWarning = result.Succeeded && !result.JournalPersisted`, chọn tiêu đề `Tr.DialogRetryDoneJournalFailedTitle`). Đó là đường **Retry** trong cửa sổ Recovery;
  `RecoveryRetryService.cs:261,269,330-340` trả `Tr.CoreRecoverySucceededJournalFailed` / `Tr.CoreRecoveryCompletedFailureNotJournaled`.
- Đường thao tác tệp thường: `FileActionController.cs:326-328` gọi `ExecuteAsync`/`ExecuteGroupAsync`, rồi chỉ đọc `Succeeded`/`SourceRemoved`; grep
  `JournalPersisted|JournalError` trong `src` ngoài `FileActions/` và `RecoveryWindowPresenter` không có kết quả nào khác. `FileActionService` không có logger.
  Kết quả: thao tác báo thành công bình thường, không có dòng log, không có thông báo.

## Vì sao quan trọng

Khi dòng nhật ký không ghi được (đĩa đầy, chặn bởi phần mềm diệt virus), Undo và Recovery không thấy thao tác đó: người dùng vừa di chuyển/xóa ảnh mà
Ctrl+Z hoặc Recovery có thể không khôi phục được, và không ai biết lý do. Rủi ro dữ liệu là bị mất khả năng hoàn tác một cách âm thầm; chi phí thêm thông báo
cho tốc độ duyệt ảnh gần như bằng không (chỉ chạy ở nhánh lỗi hiếm).

## Các phương án

| | Thay đổi | Ưu | Nhược |
|---|---|---|---|
| A | Giữ nguyên | Không đụng mã | Mất hoàn tác âm thầm, khó chẩn đoán |
| B | Chỉ ghi một dòng `AppLog.Error` ở `FileActionController` khi `Succeeded && !JournalPersisted` | Nhỏ, không đổi catalog, không cần dịch | Người dùng không biết; chỉ hữu ích khi đọc log |
| C | B + một thông báo ở thanh trạng thái bằng khóa catalog mới | Người dùng biết ngay và nên kiểm tra thư mục/đĩa; log có chi tiết | Thêm một khóa vào `en.json`/`vi.json`/`en.notes.json`; cần test controller; Rust phải đồng bộ lại catalog |
| D | C + hộp thoại chặn | Khó bỏ sót | Làm chậm luồng duyệt (nhiều ảnh liên tiếp); thanh trạng thái đã đủ |

## Khuyến nghị

Phương án C (khớp quyết định của chủ dự án cho bản Rust, P-72 option C): log lỗi và thông báo thanh trạng thái. Dùng `SetStatusText` như
`Tr.StatusMoveUnverified` (`FileActionController.cs:372,426`) để không chặn người dùng. Khi nhóm có cả `Completed` và `Failed`, thông báo này chỉ bổ sung,
không thay thế thông báo hiện có.

## Khóa catalog đề xuất (cách thêm khóa ở repo này)

Catalog nằm ở `src/PhotoReview.Core/Localization/Languages/`: `en.json` (nguồn, luôn đủ), `vi.json`, `en.notes.json` (ngữ cảnh cho người dịch).
`Tr` là mã sinh tự động bởi `src/PhotoReview.Localization.Generator/TrGenerator.cs` từ `en.json`, nên không sửa class `Tr` bằng tay; tên thuộc tính suy ra từ khóa
(`core.recovery.succeededJournalFailed` -> `Tr.CoreRecoverySucceededJournalFailed`, `status.moveUnverified` -> `Tr.StatusMoveUnverified`). Quy tắc từ `docs/TRANSLATING.md`:
giữ nguyên placeholder, khóa đổi nghĩa thì dùng khóa mới. Cần sửa đúng 3 file:

- `en.json`: `"status.actionJournalNotSaved": "{fileName}: the action succeeded, but its journal entry could not be saved, so Undo and Recovery may not see it."`
- `vi.json`: `"status.actionJournalNotSaved": "{fileName}: thao tác đã thực hiện xong nhưng không ghi được vào nhật ký, nên Hoàn tác và Khôi phục có thể không thấy thao tác này."`
- `en.notes.json`: `"status.actionJournalNotSaved": "Status bar (warning) after a Move/Copy/Recycle that succeeded while FileActionResult.JournalPersisted is false (journal append failed). {fileName} = file name the action started on. Detail is in the log."`

Đặt cạnh `status.moveUnverified` (`en.json:663`, `vi.json:656`, `en.notes.json:566`), cùng tiền tố `status.` như các thông báo tương tự; khóa `core.recovery.*`
dành cho thông điệp trả từ Core, không phải cho thanh trạng thái. Chạy `tools/i18n-check.ps1` sau khi thêm. Phía Rust sau đó đồng bộ bằng `tools/sync-from-csharp.ps1`.

## Cần kiểm thử

- Controller: kết quả giả `Succeeded=true, JournalPersisted=false` (đơn và nhóm) -> `SetStatusText` nhận đúng chuỗi và có một dòng log lỗi; `JournalPersisted=true` -> không có.
- Không đổi hành vi: tệp vẫn được đăng ký cho Undo như cũ; hai nhánh "folder đã đổi" (`ShowLateActionStatus`) cũng phải được xét.
- `tools/i18n-check.ps1` và test catalog: khóa có trong `en.json`, `vi.json`, notes; đột biến (bỏ nhánh `!JournalPersisted`) làm test đỏ.

## Bản Rust đã quyết định (chủ dự án xác nhận)

P-72, phương án C: một dòng log mức `error` ở `on_executed` cộng thông báo cho người dùng. Bị chặn bởi P-84: chuỗi thông báo phải lấy từ catalog ngôn ngữ, mà catalog
nằm ở repo C# này. Cách đi: thêm khóa trên vào C#, rồi Rust đồng bộ. Nếu chủ dự án chọn B hoặc A cho C#, Rust cần quyết định lại cho khớp.

## Ảnh hưởng tới bản Rust

Thay đổi C# được chấp nhận dịch chuyển mốc spec (SYNC-LEDGER.md, thư mục docs của repo Rust): cần đồng bộ catalog và chạy lại golden/oracle. Golden của catalog sẽ thêm đúng một khóa.
