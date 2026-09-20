# TC00 — baseline test (một phần)

- Ngày: 2026-09-20. Nhánh `codex/st01-clean-dead-code`. Lệnh: `dotnet build PhotoReview.slnx -c Release` rồi `dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual"`.
- **Đính chính:** bản báo cáo đầu (commit `a65be80`) ghi 774/775 nhưng chạy trên binary cũ — `HEAD` (`8c80f19`, ST09) không biên dịch (`DuplicateCleanupController.cs` thiếu `using PhotoReview.Core.Model`). Số liệu dưới đây là lần chạy sau khi sửa và build thật.

## Kết quả (build 0 lỗi)

| Project | PASS | FAIL |
|---|---|---|
| Architecture | 13 (gồm rule ST06 mới) | 0 |
| Core | 321 | 1 |
| Imaging | 205 | 0 |
| App | 174 | 0 |
| Integration | 62 | 0 |
| **Tổng** | **775** | **1** |

## Test lỗi

`OperationJournalTests.LargeJournal_ReadCommittedMoves_IsBoundedAndFast` (`Large journal startup reads only recent committed moves within threshold`): assert `< 100 ms`. Trong chạy song song toàn solution lỗi **2/2 lần** (112 ms, 166 ms); chạy riêng **8/8 PASS**. Kết luận có bằng chứng: nhạy tải khi các assembly chạy song song. Chưa xác định nguyên nhân gốc và chưa kiểm test này ở `master`. Không nới ngưỡng; xử lý theo TC09 (thay assert thời gian tường).

## TC00 chưa xong

Chưa làm: đo thời gian từng test (trx), lặp 30 lần nhóm G2/G4/G7, bảng kiểm kê giữ/viết lại/gộp/xóa, trait `HotPath/Stress/Native/Manual/Slow`. Các mục Q-T1..Q-T4 vẫn chờ quyết định.
