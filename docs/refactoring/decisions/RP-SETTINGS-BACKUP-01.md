---
id: RP-SETTINGS-BACKUP-01
order: 1470
summary: |-
  Open (from the Rust port review): when the backup of a newer-build config.json fails, SettingsStore.Save only logs a warning and still overwrites the file with version 4, silently dropping the newer build's unknown fields; the Rust port refuses the Save instead (P-66 option A) and the C# options are listed for the owner to align.
---

# RP-SETTINGS-BACKUP-01 - sao lưu config.json của bản mới hơn thất bại vẫn bị ghi đè

Nguồn: rà soát khi port sang Rust (P-66). Chưa có thay đổi mã ở C#.

## Hiện trạng trong C#

- `SettingsStore.cs:122`: `Load` đặt `_newerFileOnDisk` khi `config.json` có `ConfigVersion` lớn hơn `AppSettings.CurrentConfigVersion` (= 4, `AppSettings.cs:9`).
- `SettingsStore.cs:242-246` (trong `Persist`): nếu cờ bật, gọi `BackupNewerConfig(filePath)` rồi **đặt cờ về false ngay**, kể cả khi bản sao lưu lỗi; sau đó
  ghi đè bằng phiên bản 4 (`:252`, `WriteAllTextAtomic`). Các trường lạ do bản mới ghi bị `JsonSerializer` bỏ.
- `SettingsStore.cs:262-299` `BackupNewerConfig`: lỗi `IOException`/`UnauthorizedAccessException` khi `FileExists`/`Copy` chỉ `_log.Warn("Could not back up the newer-build config.json before overwriting it: ...")` rồi `return`
  (doc comment `:258-261`: "A failure here never blocks the Save"). Giữ tối đa `MaxNewerBackups = 3` (`:17`) bản sao `config.json.newer-yyyyMMddHHmmss`.
- Test khóa hành vi hiện tại: `SettingsStoreSaveAfterNewerVersionLoadTests.cs:125` `Save_WhenBackupFails_StillWritesTheConfig` (Copy ném `IOException`, Save vẫn ghi `ConfigVersion` 4).
- Khác với đường **Load**: `SettingsStoreFailureTests.cs:117` (`Load_SalvageBackupFails_ConfigNeverOverwritten`) và `SettingsStoreLoadRepairPersistenceTests.cs:218`
  (`Load_RepairBackupFails_ConfigUntouchedAndRepairedInMemory`) cho thấy khi sao lưu trước salvage/repair lỗi thì C# **không** ghi đè. Chỉ nhánh "bản mới hơn" là best effort.
- Người gọi `Save` đã bắt lỗi I/O (ví dụ `MainViewModel.cs:740-746` ghi `AppLog.Error` rồi tiếp tục), nên từ chối Save không làm ứng dụng sập; các chỗ gọi khác:
  `MainViewModel.cs:873,1506`, `MainWindow.xaml.cs:796,1104,1121,1143`, `ShellMainWindow.Input.cs:293` (chưa rà từng chỗ bắt ngoại lệ nào; xem mục kiểm thử).

## Vì sao quan trọng

Người dùng mở bản cũ sau khi đã dùng bản mới: nếu chỉ bản sao lưu lỗi (ổ đầy, thư mục bị khóa, phần mềm diệt virus) mà vẫn ghi đè được `config.json`, các trường của bản mới bị mất vĩnh viễn và không còn bản nào để khôi phục (dữ liệu cấu hình, không phải ảnh). Tác động tới tốc độ duyệt ảnh: không.

## Các phương án

| | Thay đổi | Ưu | Nhược |
|---|---|---|---|
| A | Giữ nguyên (best effort) | Cài đặt luôn lưu được | Mất âm thầm trường của bản mới khi sao lưu lỗi |
| B | Từ chối Save (ném `IOException`), tệp mới giữ nguyên, cờ giữ để lần Save sau thử lại sao lưu | An toàn dữ liệu; thống nhất với Load salvage/repair; khớp Rust | Khi sao lưu cứ lỗi, người dùng không lưu được cài đặt (có log); phải sửa test hiện có |
| C | Ghi cài đặt chỉ trong bộ nhớ, không đụng tệp | Không mất tệp, UI không báo lỗi | Che giấu lỗi, cài đặt biến mất khi thoát |
| D | Hỏi người dùng (hộp thoại) có ghi đè không | Người dùng quyết | Save được gọi từ nhiều nơi nền (đổi cờ overlay, vị trí cửa sổ): hộp thoại không phù hợp; thêm chuỗi UI |

## Khuyến nghị

Phương án B (khớp quyết định của chủ dự án cho bản Rust, P-66 option A, ADR ADR-D-2026-11-03-settings-backup-failure ở repo Rust): `BackupNewerConfig` trả lỗi, `Persist` (`:227`) ném `IOException`
trước khi ghi và **không** xóa `_newerFileOnDisk`. Lý do: cùng nguyên tắc "không ghi đè khi chưa giữ được bản gốc" mà đường salvage/repair đã áp dụng; chi phí chỉ là một lỗi lưu cài đặt hiếm
mà người gọi đã xử lý. Việc cắt tỉa bản sao lưu cũ (cuối `BackupNewerConfig`, đến `:299`) vẫn chỉ ghi Warn, không chặn Save.

## Phạm vi thay đổi nếu chọn B

`SettingsStore.cs:242-246` (không xóa cờ trước khi ghi thành công; xóa sau `WriteAllTextAtomic`), `:262-275` (bỏ `catch` nuốt lỗi, giữ log Warn rồi ném lại), doc comment `:258-261`.
Test: sửa `Save_WhenBackupFails_StillWritesTheConfig` thành kỳ vọng `IOException`, tệp nguyên byte, không có bản sao lưu; thêm test "lần Save sau sao lưu thành công thì ghi và xóa cờ"
và "sao lưu xong nhưng ghi lỗi thì cờ vẫn bật" (như ADR Rust).

## Cần kiểm thử

- Sao lưu lỗi: `Save` ném `IOException`, `config.json` giữ nguyên từng byte, `Changed` không phát, `_current` không đổi.
- Sao lưu lại thành công ở lần Save kế: tệp mới được lưu thành `.newer-*` rồi bị ghi đè bằng v4, trường lạ nằm trong bản sao lưu.
- Sao lưu thành công nhưng ghi lỗi: cờ còn bật, lần sau không mất bản sao lưu mới.
- Rà từng người gọi `Save` liệt kê ở trên: mỗi chỗ đều bắt `IOException` (hoặc chủ ý để lộ lỗi); không chỗ nào để lỗi thoát ra luồng UI.
- Mutation theo AGENTS.md (gói dữ liệu cấu hình): đảo thứ tự xóa cờ -> test đỏ.

## Bản Rust đã quyết định (chủ dự án xác nhận)

P-66 phương án A, ADR ADR-D-2026-11-03-settings-backup-failure (repo `PhotoReview-rs`, thư mục docs/adr, PR #530): `backup_newer_config` trả `io::Result<()>`; lỗi ở kiểm tra tồn tại, chọn tên hay copy
-> ghi cảnh báo rồi trả lỗi; `persist` ánh xạ sang `SettingsSaveError::Io` và dừng trước khi ghi; tệp mới giữ nguyên, `newer_file_on_disk` giữ nguyên và chỉ bị xóa sau khi ghi
nguyên tử thành công; cắt tỉa vẫn chỉ cảnh báo. Rust hiện **khác** C# ở đúng đường lỗi này. Nếu chủ dự án giữ A cho C#, cần ghi lại đây là khác biệt cố ý.

## Ảnh hưởng tới bản Rust

Thay đổi C# được chấp nhận dịch chuyển mốc spec (SYNC-LEDGER.md, thư mục docs của repo Rust): cần đồng bộ và chạy lại golden/oracle; dòng PARITY `D-settings-newer-backup-failure` chuyển từ "khác biệt" sang "khớp".
