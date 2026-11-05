---
id: RP-GATE-END-01
order: 1450
summary: |-
  Open (from the Rust port review): FileActionService.End() is public and clears the INV-4 in-progress flag unconditionally, so a stray End() can release another action's gate; the Rust port chose a token/guard (P-71 option C) and the C# options are listed here for the owner to align.
---

# RP-GATE-END-01 - `FileActionService.End()` công khai xóa cờ INV-4 vô điều kiện

Nguồn: rà soát khi port sang Rust (repo `PhotoReview-rs`, quyết định P-71). Chưa có thay đổi mã nào ở C#; tài liệu này chỉ để chủ dự án
xem lại và đồng bộ hai ứng dụng.

## Hiện trạng trong C#

- `src/PhotoReview.Core/FileActions/FileActionService.cs:45` `TryBegin()` = `Interlocked.CompareExchange(ref _inProgress, 1, 0) == 0`;
  `:50` `End()` = `Volatile.Write(ref _inProgress, 0)`. Cả hai đều `public`, `End()` không kiểm tra ai đang giữ cổng.
- `FileActionService` tự dùng: `TryBegin()` ở `:69` (đơn lẻ) và `:568` (nhóm), `End()` ở `:323` và `:845` (trong `finally`).
- `UndoService.cs:77-83` `TryBegin()` và `:89-98` `End()` chuyển tiếp sang `_fileActionService` (hoặc cờ riêng `_internalInProgress` khi không có
  service). Dùng trong `UndoService.cs:155/296`, `:346/386`, `:395/490`, `:598/682`.
- `RecoveryRetryService.cs:61` `TryBegin()` và `:69` `End()` qua `_fileActionGate`. Lưu ý: ở C# không có interface `IFileActionGate`; trường này
  có kiểu cụ thể `FileActionService?` (`RecoveryRetryService.cs:28,35`). `interface IFileActionGate` chỉ có ở bản Rust.
- Gọi `End()` trực tiếp trong test (đã grep toàn bộ `src` và `tests`):
  `App.Tests/CompositionRootTests.cs:46`, `App.Tests/Coordinators/FileActionControllerRenameTests.cs:446`,
  `App.Tests/ViewModels/MainViewModelMoveCopyToTests.cs:256`, `Core.Tests/FileActions/CaptureGroupSidecarAndGateTests.cs:131,148`,
  `FileActionServiceTests.cs:503,506`, `RecoveryRetryServiceTests.cs:346`, `RenameFileActionTests.cs:194`,
  `UndoServiceMutationGapTests.cs:86,256`, `UndoServiceSafetyTests.cs:93,108`, `UndoServiceTests.cs:486`,
  `Integration.Tests/RecoveryWindowGapTests.cs:129`. Mã sản xuất ngoài `Core/FileActions` không gọi `End()`.

## Vì sao quan trọng

INV-4 (chỉ một thao tác tệp tại một thời điểm) là lớp chặn hai thao tác Move/Copy/Delete/Undo/Retry chồng nhau trên cùng ảnh. Hiện mọi đường
trong mã sản xuất đều cặp `TryBegin`/`finally End`, nên chưa có lỗi thật. Nhưng vì `End()` là API công khai và vô điều kiện, một lời gọi thừa sau
này (ví dụ ở nhánh lỗi mới) sẽ mở cổng giữa lúc thao tác khác đang chạy: hai thao tác ghi nhật ký và đổi tên/di chuyển cùng lúc, rủi ro trực tiếp
tới dữ liệu ảnh của người dùng và tới tính đúng của Undo/Recovery. Không ảnh hưởng tốc độ duyệt ảnh.

## Các phương án

| | Thay đổi | Ưu | Nhược |
|---|---|---|---|
| A | Giữ nguyên | Không rủi ro hồi quy | Lỗi gọi `End()` thừa vẫn có thể xảy ra và không bị phát hiện |
| B | Giữ `End()` nhưng đổi thành `internal` + `InternalsVisibleTo` cho test | Sửa nhỏ; ngoài assembly không ai gọi được | `UndoService`/`RecoveryRetryService` cùng assembly vẫn gọi được; không chặn lỗi trong chính Core; test vẫn gọi `End()` trần |
| C | `TryBegin()` trả về token/guard (`IDisposable`/struct) và chỉ guard đó mới nhả cổng; bỏ `End()` công khai | Chỉ bên đang giữ mới nhả được; `using`/`finally` bắt buộc đúng cặp; double-dispose là no-op | Đổi API: `UndoService.TryBegin/End`, `RecoveryRetryService`, 11 file test đều phải sửa; cần thiết kế lại chỗ test giữ cổng "bận" bằng tay |
| D | Giữ `End()` nhưng kiểm tra chủ sở hữu bằng generation counter (`End(long ticket)`) | Ít thay đổi cú pháp hơn C | Vẫn phải truyền ticket qua mọi chỗ; dễ quên hơn guard |

## Khuyến nghị

Phương án C (khớp quyết định của chủ dự án cho bản Rust, P-71 option C): token/guard, không còn `End()` công khai. Lý do: biến một quy ước
("nhớ gọi End đúng lúc") thành bất biến do kiểu bảo đảm; chi phí chủ yếu là sửa test, không đổi hành vi người dùng.

## Phạm vi thay đổi nếu chọn C

Mã: `FileActionService.cs:45-50,69,323,568,845`; `UndoService.cs:77-98` và 4 cặp `TryBegin/End` (liệt kê trên); `RecoveryRetryService.cs:28,35,61,69`;
chỗ dựng `RecoveryRetryService` trong composition root (kiểm tra `CompositionRootTests.cs:34-46`). Test: 11 file nêu trên. Không đổi định dạng journal
hay file cấu hình.

## Cần kiểm thử

- Hai thao tác song song: cái thứ hai bị từ chối "bận", cổng vẫn do thao tác đầu giữ; sau khi thao tác đầu xong cổng mở đúng một lần.
- Guard được nhả khi thao tác ném ngoại lệ và khi bị hủy (`CancellationToken`).
- Dispose guard hai lần không nhả cổng của thao tác khác (test mới thay cho test gọi `End()` trần).
- Undo (`UndoLastAsync`) và Retry bị từ chối khi cổng đang bận, đúng như `CaptureGroupSidecarAndGateTests` hiện kiểm tra.
- Theo AGENTS.md: kiểm tra bằng đột biến (bỏ nhả cổng trong `finally` thì test phải đỏ).

## Bản Rust đã quyết định (chủ dự án xác nhận)

P-71, phương án C: thay `end()` công khai bằng token/guard để chỉ bên giữ mới nhả được cổng (PROPOSED-DECISIONS.md, thư mục docs của repo Rust). Hiện bản
Rust dùng RAII guard cho các đường nội bộ. Nếu C# chọn khác C, hai ứng dụng sẽ lệch về API cổng nhưng hành vi người dùng vẫn giống nhau.

## Ảnh hưởng tới bản Rust

Bất kỳ thay đổi C# nào được chấp nhận đều dịch chuyển mốc spec (SYNC-LEDGER.md, thư mục docs của repo Rust): cần đồng bộ và chạy lại golden/oracle. Thay đổi này
không đổi hành vi quan sát được nên golden có thể không đổi, nhưng vẫn phải chạy lại để chứng minh.
