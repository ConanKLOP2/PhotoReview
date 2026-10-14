---
id: FEAT-RENAME-CORE
order: 1410
summary: |-
  PR1 "Lõi Core" của FEAT-RENAME-DESIGN (CHƯA XÁC NHẬN, Q1-Q7 theo khuyến nghị): RenameNamePolicy, FileActionService.RenameAsync/RenameGroupAsync (Move cùng thư mục, journal Move cũ, không ghi đè), nhánh đổi chỉ hoa-thường ở 6 chỗ qua IFileSystem.GetStoredFileNames, Q7 = Dismissed; chưa có UI/WPF/catalog (PR2/PR3).
---

# FEAT-RENAME-CORE — Lõi Core đổi tên tệp (2026-10-15, CHƯA XÁC NHẬN)

PR1 của [`FEAT-RENAME-DESIGN.md`](FEAT-RENAME-DESIGN.md). Chỉ lõi Core: **không** UI/menu/phím, **không** sửa catalog/session/presenter
(PR2), **không** sửa bản WPF. Chưa có đường gọi nào từ ứng dụng nên hành vi hiện tại không đổi.

## 1. Quyết định (người dùng chưa trả lời Q1-Q7; làm theo khuyến nghị, CHƯA XÁC NHẬN)

| Q | Chọn | Ghi chú |
|---|---|---|
| Q1 | A: đổi chỉ hoa-thường được hỗ trợ đầy đủ | không bao giờ hai bước qua tên tạm; tên thật trên đĩa lấy bằng liệt kê thư mục |
| Q2 | F2 mặc định | thuộc PR3, không có trong PR này |
| Q3 | rename vào ô Undo đơn | `UndoService.Register(result)` / `RegisterGroup(result)` như Move (ghi đè Undo lần trước) |
| Q4 | chỉ nhóm JPEG+RAW+XMP hiện biết | `CaptureGroupRenamePlan`; dò sidecar khác là PR4 |
| Q5 | chỉ Win32 | không sửa `PhotoReview.App`; lõi dùng chung, test WPF/App.Tests chạy lại |
| Q6 | ảnh ở nguyên chỗ | thuộc PR2 |
| Q7 | thất bại không đổi gì -> `Dismissed` | `IsRenameUntouched`; mọi trạng thái mơ hồ vẫn `Failed` (Recovery) |

## 2. Thay đổi theo lớp

| Lớp | Thay đổi |
|---|---|
| `Core/FileActions/RenameNamePolicy` (mới) | `Check` (tên gõ -> tên tệp đầy đủ + mã lỗi `RenameNameProblem`), `CheckFileName`, `IsReservedDeviceName`, `ExceedsPathBudget`. Core không có chữ: UI dịch theo mã (PR3) |
| `Core/FileActions/RenameRequest`, `CaptureGroupRenamePlan` (mới) | yêu cầu đổi tên một tệp; kế hoạch tên cho nhóm (mỗi thành viên giữ đuôi riêng; XMP `<base>.xmp` hoặc `<file>.<ext>.xmp` đi theo chủ) |
| `FileActionService` | `RenameAsync`, `RenameGroupAsync`; `ExecuteAsync` giữ nguyên (nay gọi `ExecuteCoreAsync(request, renameFileName: null)`); `ExecuteGroupAsync` nhận `CaptureGroupActionRequest.DestinationFileNames`. `IsSamePath` (:536, :107) KHÔNG đổi: Move/Copy vào chính thư mục nguồn vẫn bị từ chối (có test) |
| `FileActionResult.NoChange` (mới, cuối record) | đổi sang đúng tên đang có: thành công, không journal |
| `CaseOnlyRename` (mới, internal) | `IsCaseOnlyPair`, `StatAs`, `ExistsAs`, `ProbeTarget`: cặp không chỉ-hoa-thường = đúng lời gọi thường (một stat) |
| `IFileSystem.GetStoredFileNames` (mặc định qua `EnumerateFiles`) | tên đang LƯU trên đĩa; không P/Invoke (giữ L-AOT); `InMemoryFileSystem` có `StoredFileNamesOverride` + Move đổi-hoa-thường như NTFS |
| Journal | KHÔNG thêm `FileOperationType`/trường: dòng rename = Move cùng thư mục (ADR 0003); test so khoá JSON với Move thường |
| Ngôn ngữ | thêm `core.fileAction.renameInvalidName` (en/vi/notes) cuối tệp |

### Sáu chỗ case-only (design 2.4) và hai chỗ thêm

`JournalTransaction.VerifyMoved`, `OperationJournal.ReconcileOne` (đơn + `IsGroupMemberCompleted`), `RecoveryFileCheck.CheckPath`,
`RecoveryRetryService` (đơn, nhóm, `ExecuteRetry`), `UndoService.UndoMoveAsync` (+ `SafeFileExistsAs`) và `UndoGroupMoveAsync`: dùng
`CaseOnlyRename.ExistsAs/StatAs`. Thêm (không có trong bảng thiết kế): `FileActionService.RestoreMovedMembers`, `InspectGroupMember`,
`VerifyGroupMove`, `IsSourceGone` (bù trừ và báo cáo của nhóm cũng sai nếu bỏ qua). **Lệch so với thiết kế:** dòng :553
(`FileExists(destinationPath)` của `ExecuteAsync`) KHÔNG sửa vì không thể tới được bằng cặp chỉ-hoa-thường: `IsSamePath` đã chặn cùng thư mục;
kiểm đích của rename nằm ở `CaseOnlyRename.ProbeTarget`.

Một ba-spelling được xử lý an toàn: tệp lưu dưới cách viết THỨ BA (không phải nguồn, không phải đích) tính là "có cả hai" -> `Conflict`,
không bao giờ `Lost`/`Committed` từ một tên chưa chứng minh. Hai mục chỉ khác hoa-thường (thư mục phân biệt hoa-thường) = hai tệp khác nhau -> từ chối.

## 3. Điểm lệch / quyết định nhỏ (CHƯA XÁC NHẬN)

- **Chuẩn hoá Unicode: KHÔNG.** Tên dùng đúng như gõ (chỉ cắt khoảng trắng hai đầu); NFC/NFD là hai tên khác nhau trên NTFS. So trùng: `OrdinalIgnoreCase`.
- **Đuôi:** giữ đuôi gốc; gõ lại đúng đuôi cũ (không phân biệt hoa-thường) thì cắt; đuôi khác là một phần tên (`a.v2` -> `a.v2.jpg`); chỉ gõ đuôi = rỗng.
- **MAX_PATH:** manifest không có `longPathAware`, nên từ chối khi đường dẫn mới >= 260 VÀ dài hơn đường dẫn hiện tại (tệp đã nằm ở đường dẫn dài vẫn đổi tên ngắn hơn/bằng được).
- **Tên thiết bị:** thêm COM0/LPT0, CONIN$/CONOUT$, chỉ số trên (COM¹²³), kể cả có đuôi hoặc dấu cách trước dấu chấm.
- **Tên mới == tên cũ** (hoặc đã đúng cách viết): `NoChange`, không journal, không mutation.
- **Mô hình lỗi:** rename cùng thư mục là MỘT File.Move nguyên tử; injector có kind "rename" riêng (không áp CrashPartial/CrashMoveCopiedNotDeleted).
- **Nhóm:** xung đột đích (trùng nhau, hoặc trùng nguồn của thành viên khác) bị từ chối TRƯỚC khi journal; thành viên đã đúng tên được bỏ qua.
- **Undo:** không "re-key" các mục lịch sử cũ trong `_moveHistory`: hoàn tác theo ngăn xếp vẫn đúng thứ tự; re-key sẽ làm hỏng LIFO.

## 4. Kiểm kê còn lại cho PR2 (catalog/session/presenter/cache)

R01-R04 `ReviewCatalog.RenameEntry` (+`StructuralVersion`, nhóm theo stem mới); R06-R07 session `CurrentPath`/`Skipped`; R08-R09/R11
`ImagePresenter.RetargetPath` (không `PresentAsync`), compare index; R10 `CompareViewModel.Clear`; R14 đăng ký Undo (đã có ở Core);
R15-R16 `PreviewImageService.EvictCachedPath` + `_originalDimensions`; R19, R21 preload; R22 `Preload Cancel` + chạy lại; R26 `ZoomDetailLoader.CancelPending`;
R28 `NavigationStatWorker.StopForAction`; R29 chờ `PendingOrder`; R36 tiêu đề cửa sổ; đường Undo-rename -> `RenameEntry(dest -> source)` ở `FileActionController`
(nhận biết bằng cùng thư mục); `FileActionResult.NoChange`.

## 5. Test

Xem mục kết quả trong mô tả PR. Tệp: `RenameNamePolicyTests`, `CaptureGroupRenamePlanTests`, `RenameFileActionTests`, `RenameFaultInjectionTests`
(12 ma trận: đơn, bị chiếm tên, case-only, nhóm, nhóm case-only, +Undo x3, hai lần đổi tên, reconcile gián đoạn x3).
Bất biến mới trong `FaultInvariants`: `SourceStillThere` (tên thật), "đã đổi tiếp bởi dòng sau" (rename hai lần / retry sau Undo); `FaultRig.Alias`.

## 6. Đột biến thủ công (hai bước, docs/MUTATION-TESTING.md)

| Id | Dot bien | File:dong | Ket qua | Test bat duoc |
|---|---|---|---|---|
| M01 | ProbeTarget: nhanh khong-case-only tra (false,false) | CaseOnlyRename.cs:73 | killed buoc 1 | RenameFileActionTests.RenameGroup_OneTargetTaken_ChangesNothingAndJournalsNothing |
| M02 | ProbeTarget: bo DirectoryExists | CaseOnlyRename.cs:73 | killed buoc 1 | RenameFileActionTests.Rename_TargetIsAFolder_IsRefused |
| M03 | IsCaseOnlyPair: OrdinalIgnoreCase -> Ordinal | CaseOnlyRename.cs:24 | killed buoc 1 | RenameFaultInjectionTests (case-only, 13 test) |
| M04 | IsStoredAs: bo ve `!= other` | CaseOnlyRename.cs:62 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.CaseOnlyRename_FileStoredUnderAThirdSpelling_IsNeverLostOrDone |
| M05 | IsStoredAs: Count>1 -> false | CaseOnlyRename.cs:57 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.CaseOnlyRename_TwoEntriesDifferingByCase_AreTwoFiles_Conflict |
| M06 | ProbeTarget: bo nhanh cung ten Ordinal | CaseOnlyRename.cs:70 | killed buoc 1 | RenameFileActionTests.Rename_ToTheSameName_IsANoChangeSuccessWithNoJournalLine |
| M07 | IsReservedDeviceName: return false dau ham | RenameNamePolicy.cs:~150 | killed buoc 1 | CaptureGroupRenamePlanTests.Build_InvalidName_ReturnsProblemAndNoMap |
| M08 | IsReservedDeviceName: bo TrimEnd(' ') | RenameNamePolicy.cs:~152 | killed buoc 1 | RenameNamePolicyTests.IsReservedDeviceName_Table |
| M09 | IsReservedDeviceName: bo nhanh COM/LPT | RenameNamePolicy.cs:~160 | killed buoc 1 | RenameNamePolicyTests.Check_DeviceName_ReturnsReservedDeviceName |
| M10 | Check: bo kiem stem cuoi '.' | RenameNamePolicy.cs:~92 | killed buoc 1 | RenameNamePolicyTests.Check_TrailingDot_ReturnsTrailingDotOrSpace |
| M11 | CheckFileName: bo kiem cuoi '.'/' ' | RenameNamePolicy.cs:~115 | killed buoc 1 | RenameNamePolicyTests.CheckFileName_Table |
| M12 | FirstInvalidCharacter: c<' ' -> c<'\0' | RenameNamePolicy.cs:~172 | killed buoc 1 | RenameNamePolicyTests.Check_ControlCharacterInside_ReturnsInvalidCharacter |
| M13 | FirstInvalidCharacter: bo '|' | RenameNamePolicy.cs:~172 | killed buoc 1 | RenameNamePolicyTests.Check_ForbiddenCharacter_ReturnsInvalidCharacterWithThatChar |
| M14 | Check: > Max -> >= Max | RenameNamePolicy.cs:~97 | killed buoc 1 | RenameNamePolicyTests.Check_FileNameOf255_IsAccepted |
| M15 | ExceedsPathBudget: >= MaxPath -> > | RenameNamePolicy.cs:~135 | killed buoc 1 | RenameNamePolicyTests.Check_NewPathExactly260_ReturnsPathTooLong |
| M16 | ExceedsPathBudget: bo `> currentFullPath.Length` | RenameNamePolicy.cs:~135 | killed buoc 1 | RenameNamePolicyTests.Check_LongCurrentPathAndSameLengthName_IsAccepted |
| M17 | Check: bo cat duoi trung duoi cu | RenameNamePolicy.cs:~81 | killed buoc 1 | CaptureGroupRenamePlanTests.Build_TypedJpegExtension_IsStripped |
| M18 | Check: caseOnly dung Ordinal | RenameNamePolicy.cs:~105 | killed buoc 1 | RenameNamePolicyTests.Check_OnlyCaseDiffers_IsCaseOnly |
| M19 | ExecuteCoreAsync Move/Copy: bo tx.BeginAsync | FileActionService.cs:683 | killed buoc 1 | 57 test FaultInjection (journal Prepared) |
| M20 | IsRenameUntouched luon true | FileActionService.cs:880 | killed buoc 1 | RenameFileActionTests.Rename_FailureAfterTheFileWasRenamed_StaysAFailedRecoveryItemNeverDismissed |
| M21 | IsRenameUntouched luon false | FileActionService.cs:880 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_MoveFails_NothingChangedAndTheLineIsDismissedNotLost |
| M22 | ExecuteCoreAsync: bo NoChange early return | FileActionService.cs:627 | killed buoc 1 | RenameFileActionTests.Rename_ToTheSameName_IsANoChangeSuccessWithNoJournalLine |
| M23 | ExecuteGroupAsync: bo `if (conflict) throw` | FileActionService.cs:154 | killed buoc 1 | RenameFileActionTests.RenameGroup_OneTargetTaken_ChangesNothingAndJournalsNothing |
| M24 | ExecuteGroupAsync: bo targets.Add (trung dich) | FileActionService.cs:179 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_TwoMembersToTheSameTarget_IsRefusedBeforeAnythingIsJournaled (da sua: map day du 3 thanh vien; truoc do vo hieu vi thieu a.xmp) |
| M25 | ExecuteGroupAsync: bo vong dich == nguon thanh vien khac | FileActionService.cs:185 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_RecoveryRetryOfAFailedGroupLine_FinishesEveryMember |
| M26 | ExecuteGroupAsync: bo alreadyNamed.Add+continue | FileActionService.cs:157 | killed buoc 1 | RenameFileActionTests.RenameGroup_PartlyAlreadyNamed_RenamesOnlyTheOthers |
| M27 | RestoreMovedMembers: StatAs -> GetFileStat | FileActionService.cs:447-448 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_SecondMemberFails_FirstIsPutBackAndTheLineIsDismissed |
| M28 | InspectGroupMember: StatAs -> GetFileStat | FileActionService.cs:502-503 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_SecondMemberFails_FirstIsPutBackAndTheLineIsDismissed |
| M29 | VerifyGroupMove: ExistsAs -> FileExists | FileActionService.cs:485 | killed buoc 1 | RenameFileActionTests.RenameGroup_CaseOnly_RenamesAllMembersUndoesAndReconcilesClean |
| M30 | JournalTransaction.VerifyMoved: -> FileExists | JournalTransaction.cs:138 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_Succeeds_StoresTheNewSpellingAndIsCommitted |
| M31 | ReconcileOne: sourceExists -> FileExists | OperationJournal.cs:509 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_StartupReconcile_FollowsTheStoredSpelling |
| M32 | ReconcileOne: destinationStat -> GetFileStat | OperationJournal.cs:510 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.CaseOnlyRename_UndoMovedTheFileButTheCheckFailed_DropsTheEntryByTheStoredSpelling |
| M33 | IsGroupMemberCompleted: sourceExists -> FileExists | OperationJournal.cs:641 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_SecondMemberFails_FirstIsPutBackAndTheLineIsDismissed |
| M34 | RecoveryFileCheck.CheckPath: -> GetFileStat | RecoveryFileCheck.cs:230 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_RecoveryVerdict_FollowsTheStoredSpelling |
| M35 | RetryCoreAsync: dich ton tai -> FileExists | RecoveryRetryService.cs:97 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_RecoveryRetryOfAFailedLine_FinishesTheRename |
| M36 | RetryGroupAsync: dich ton tai -> FileExists | RecoveryRetryService.cs:177 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_RecoveryRetryOfAFailedGroupLine_FinishesEveryMember |
| M37 | ExecuteGroupRetry: sau Move -> FileExists | RecoveryRetryService.cs:252 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.RenameGroup_CaseOnly_RecoveryRetryOfAFailedGroupLine_FinishesEveryMember |
| M38 | UndoMoveAsync: !ExistsAs(dest) -> !FileExists | UndoService.cs:176 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.CaseOnlyRename_UndoWhenTheFileWasRenamedByHand_DropsTheDeadHistoryEntry |
| M39 | UndoMoveAsync: ExistsAs(source) -> FileExists | UndoService.cs:182 | killed buoc 1 | RenameFileActionTests.CaseOnlyRename_Undo_RestoresTheOldSpelling |
| M40 | UndoGroupMoveAsync: StatAs -> GetFileStat | UndoService.cs:410-411 | killed buoc 1 | RenameFileActionTests.RenameGroup_CaseOnly_RenamesAllMembersUndoesAndReconcilesClean |
| M41 | SafeFileExistsAs: bo twin | UndoService.cs:302 | SONG ban dau -> killed buoc 1 sau khi them/sua test | RenameFileActionTests.CaseOnlyRename_UndoMovedTheFileButTheCheckFailed_DropsTheEntryByTheStoredSpelling |
| M42 | CaptureGroupRenamePlan: XMP cua JPEG = stem+ext | CaptureGroupRenamePlan.cs:57 | killed buoc 1 | CaptureGroupRenamePlanTests.Build_XmpNamedAfterJpeg_FollowsJpegFileName |
| M43 | CaptureGroupRenamePlan: Dictionary Ordinal | CaptureGroupRenamePlan.cs:36 | killed buoc 1 | CaptureGroupRenamePlanTests.Build_MapKeysCompareIgnoringCase |
| M44 | ExecuteCoreAsync: luon CreateDestinationFolder | FileActionService.cs:672 | TUONG DUONG quan sat | (khong test nao quan sat duoc: CreateDirectory idempotent tren thu muc co san; test dem 'mkdir' bi bo vi journal cache thu muc theo tien trinh nen khong on dinh) |
| M45 | UndoService.Register: bo _moveFingerprints[...] | UndoService.cs:113 | killed buoc 1 | 18 test UndoService (vd 'A failed undo move is journaled Failed and the move stays undoable') |

Tong ket: 45 mutant, tat ca bien dich. Lan chay dau: 32 killed buoc 1, 0 killed chi buoc 2, 13 SONG (M04 M05 M24 M25 M27 M28 M32 M33 M36 M37 M38 M41 M44), 0 tuong duong.
Sau khi bo sung test mutation trong RenameFileActionTests va sua test M24 (map thieu a.xmp nen bi chan o buoc kiem tra ten truoc, khong toi duoc vong trung dich): 12 trong 13 song bi killed o buoc 1; M44 chuyen thanh TUONG DUONG quan sat (test dem mkdir bi bo vi khong on dinh): 0 song that, 1 tuong duong.
Ghi chu: M05 va M27 tung hien killed buoc 2 mot lan (test flaky ngoai pham vi), chay lai 2 lan deu SONG. Sau khi them test: loc 'Rename' 202 test xanh; ca bo (Category!=Manual&Native&Slow) 3440 test xanh o baseline.

## 7. Hoàn tác

Lõi chưa có người gọi: vô hiệu = không gọi `RenameAsync`/`RenameGroupAsync` (PR3 chưa nối). Gỡ hẳn: revert PR này (các tệp mới + 7 chỗ
`CaseOnlyRename.*` trở về `FileExists/GetFileStat`; `IFileSystem.GetStoredFileNames` là phương thức mặc định thêm, bỏ đi không ảnh hưởng bản cũ).
