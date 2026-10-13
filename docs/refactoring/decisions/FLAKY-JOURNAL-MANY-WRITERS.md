---
id: FLAKY-JOURNAL-MANY-WRITERS
order: 1300
summary: |-
  CHƯA XÁC NHẬN (2026-10-13): test `ManyWritersOneFile_NoTornOrLostAcknowledgedRecords` đỏ khi CPU bị đói vì đòi >= 120/240 append
  qua được ngân sách retry (đếm theo SỐ LẦN, 5 lần, ~100 ms ngủ) của OperationJournal: số đó là lịch CPU, không phải bất biến. Mã sản xuất
  đúng (journal từ chối khi tranh chấp, không xoắn/mất), chỉ sửa test: writer retry như caller thật, rồi khẳng định MỌI bản ghi đã ack
  hiện diện đúng một lần và không xoắn. Test thứ hai cùng lớp (Committed >= 15/30) sửa cùng cách.
---

# FLAKY-JOURNAL-MANY-WRITERS - ngưỡng "append qua được ngân sách retry" là lịch CPU (2026-10-13)

**Trạng thái: CHƯA XÁC NHẬN** (đi theo khuyến nghị của lead; ADR 0007 không đổi).

## Nguyên nhân gốc

- `OperationJournal.OpenAppendWithRetry`: ngân sách retry đếm theo SỐ LẦN (`AppendAttempts = 5`, nghỉ 10/20/30/40 ms qua
  `Thread.Sleep`), không phải theo thời gian thật chia chung. Mỗi `Append` có ngân sách riêng; không có ngân sách chung làm writer đói.
- Một append giữ handle (`FileShare.Read`) trong lúc kiểm đuôi + ghi + flush. Khi CPU bị đói, writer đang giữ file bị ngắt lịch nhiều
  quantum; 5 writer còn lại hết 5 lần thử trong cửa sổ đó và `Append` ném `IOException` (hành vi sản xuất đúng: journal từ chối
  khi tranh chấp, caller báo lỗi/thử lại; không bao giờ ghi nửa dòng). Tỉ lệ qua = hàm của lịch CPU (đo: 72..118/240).
- Test đo SAI điều kiện: các assertion toàn vẹn (không xoắn, không mất bản ghi đã ack) luôn QUA; chỉ ngưỡng
  `acknowledged.Count >= 120` đỏ. Mức chắc chắn: cao (đọc mã + số đo 6/15 đỏ cùng một assertion, toàn vẹn qua).

## Sửa (chỉ test)

`tests/PhotoReview.Core.Tests/FileActions/JournalConcurrencyTests.cs`:

- Writer retry `Append` khi `IOException` (như caller sản xuất) bằng `Thread.Yield()` (không `Thread.Sleep`, không hạn chót thời gian
  thật từng bản ghi: bản thử đầu có hạn chót 20 s đã tự gây đỏ ~1/15 dưới tải). Phát hiện treo theo TIẾN ĐỘ: chủ test đỏ khi không
  ai ack thêm bản ghi nào trong 30 s (`JoinWhileProgressing`), lúc đó dừng các vòng retry.
- Khẳng định: mọi bản ghi được ack có đúng một dòng, không dòng xoắn, không ghi hai lần, và số ack == 240. Số lần bị từ chối chỉ là
  thông tin trong thông báo lỗi.
- `ReconcileWhileOtherProcessCommits_...`: cùng lớp (>= 15/30 Committed qua ngân sách retry) -> commit thread retry, khẳng định 30/30.
- Không đổi mã sản xuất, không nới ngưỡng, không đổi ngân sách retry/durability.

## Bảo vệ vẫn còn

Đột biến thủ công: bỏ ghi một bản ghi (id `w3-7`) nhưng vẫn ack -> test ĐỎ (`Assert.Contains() Failure: Item not found in set`).

## Số đo

- Tái hiện: 4 tiến trình đốt CPU (80/20 ms) trên máy 12 nhân KHÔNG làm đỏ (15/15 xanh); ghim burner + `dotnet test` vào 2 nhân
  (affinity 0x3): test CŨ đỏ 1/10 với đúng thông báo `only 117/240 appends got through the retry budget`.
- Sau sửa, cùng điều kiện ghim 2 nhân + 4 burner: 20/20 xanh (cả lớp `JournalConcurrencyTests`), 20/20 xanh (chỉ test này).
- `PhotoReview.Core.Tests` lọc `Journal|FileActions`: 1105/1105 xanh.
