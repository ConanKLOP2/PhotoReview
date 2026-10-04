# Bài học về đồng thời và I/O (tiếng Việt, dùng lại được cho project khác)

Hai bài học: (1) kiến trúc NGINX về process, I/O không chặn và cô lập việc chặn; (2) cách database tránh bán trùng một ghế (double booking). Phần 1 và 2 là kiến thức chung, không phụ thuộc project. Phần 3 ghi cách PhotoReview áp dụng, kèm vị trí trong code. Khi mang sang project khác, chỉ cần lấy phần 1, 2 và danh sách kiểm tra ở phần 4.

## 1. Bài học từ NGINX

### 1.1 Tối giản process: đừng tạo ra thứ không cần
- NGINX chạy vài worker (thường mỗi core một worker) gánh cả server. Nó không tạo process hay thread cho mỗi kết nối như mô hình cũ (Apache prefork).
- Mỗi process/thread tốn RAM (stack) và CPU (chuyển ngữ cảnh). Thêm worker vượt số core chỉ làm các worker giành CPU của nhau.
- Kết quả: bộ nhớ ổn định, chi phí không tăng tuyến tính theo số kết nối.
- **Quy tắc:** mọi pool phải có giới hạn tường minh. Fan-out không giới hạn (một task mỗi mục) là lỗi thiết kế.

### 1.2 Non-blocking I/O: đừng chờ đợi
- Một worker chạy vòng lặp sự kiện (epoll/kqueue). Khi một kết nối chờ đĩa hay mạng, worker chuyển sang kết nối khác. Một worker phục vụ hàng nghìn kết nối (bài toán C10K).
- **Quy tắc:** không `.Result`, `.Wait()`, `GetAwaiter().GetResult()` trên luồng giao diện hay luồng chính. Chờ bằng `await`.
- **Ngoại lệ hợp lý:** thư viện native chỉ có API đồng bộ (decoder đòi `Stream` đồng bộ). Khi đó đừng giả vờ async. Chạy đồng bộ trên luồng riêng (mục 1.3).

### 1.3 Cô lập việc chặn khỏi đường chính
- Có thao tác không có bản non-blocking. NGINX đẩy chúng sang thread pool riêng để event loop không bao giờ bị chặn.
- **Quy tắc:** đường chính chỉ chứa việc nhanh. Việc chậm đi đường phụ, có làn riêng, có hủy (cancellation) và có áp suất ngược (backpressure: hàng đợi có giới hạn, đầy thì bỏ bớt hoặc từ chối nhanh).
- Việc nền không được làm đói việc người dùng đang chờ. Việc người dùng chờ nên có làn và ưu tiên riêng.

### 1.4 Điểm chung
Giữ luồng quan trọng luôn phản hồi, dùng ít tài nguyên nhất cần thiết, không để một việc chậm kéo cả hệ thống chậm.

## 2. Bài học từ double booking (hai người cùng đặt một ghế)

Lỗi gốc: hai giao dịch cùng đọc "ghế A5 còn trống", cùng ghi "đã bán", người sau ghi đè người trước (lost update). Đọc rồi ghi (check-then-act) tách rời luôn có race.

| Cách | Ý tưởng | Điểm yếu |
|---|---|---|
| Khóa bi quan (`SELECT ... FOR UPDATE`) | Khóa ngay khi đọc, người thứ hai chờ | Chờ lâu, có thể deadlock |
| Khóa lạc quan (cột `version`) | `UPDATE ... WHERE id=5 AND version=3`; 0 dòng đổi thì thua, thử lại | Tốn thử lại khi tranh chấp cao |
| Ràng buộc duy nhất / update có điều kiện nguyên tử | `UNIQUE(show, seat)` hoặc `UPDATE ... SET sold=1 WHERE sold=0` | Chỉ đúng khi bài toán vừa khít |
| Giữ chỗ có thời hạn (hold + TTL) | Chọn ghế thì giữ vài phút, hết hạn thì nhả | Cần dọn hold hết hạn |
| Tuần tự hóa (hàng đợi, lock phân tán) | Mọi yêu cầu cho một ghế đi qua một cổng | Cổ chai, thêm hạ tầng |

Hai nguyên tắc chung:
1. **Gộp kiểm tra và ghi thành một thao tác nguyên tử** ở phía có quyền quyết định cuối cùng (database, hệ điều hành), không ở phía gọi.
2. **Người thua phải biết mình thua** (0 dòng đổi, lỗi trùng khóa, "superseded") và xử lý rõ ràng. Không ghi đè im lặng, không ghi lỗi giả.

Hệ quả cho desktop app: "hai người" có thể là hai cửa sổ/process, hoặc hai thao tác của cùng một người (lệnh xếp hàng ở thư mục A chạy khi đã sang thư mục B). Cùng một họ lỗi, cùng một cách chữa: gắn **định danh phiên bản** (folder epoch, journal anchor) vào thao tác lúc tạo, và kiểm tra lại sau mỗi chỗ chờ (`await`).

## 3. PhotoReview áp dụng thế nào (tại thời điểm 2026-10-04)

### 3.1 NGINX
- **Ít luồng, có giới hạn:** một process một cửa sổ mặc định; preload mặc định 8 worker (`PerformanceOptions`, `PreloadScheduler` dùng `SemaphoreSlim`), tự giảm còn `Max(2, ProcessorCount/3)` khi viewer đang decode và còn 1 khi link chậm (Q-R29-C2); viewer decode 2 slot, decode ảnh gốc/zoom 1 slot, LibRaw full decode 1 slot; ghi cache đĩa 2 worker với hàng chờ chặn 16 và bỏ bớt khi đầy. Không chỉnh `ThreadPool`; Workstation GC có chủ đích (AR12c).
- **I/O:** ADR 0005 cấm `.Result/.Wait()/GetResult()` ở tầng App. Đọc ảnh nguồn đồng bộ có chủ đích (`PhysicalSourceReader`) vì decoder native cần `Stream` đồng bộ, và chạy trên luồng decode, không phải UI. Stat điều hướng, quét thư mục, file action, Recycle Bin đều nằm ngoài UI thread.
- **Cô lập:** viewer decode có thread riêng ưu tiên AboveNormal, tách khỏi pool mà preload dùng. Seam `ISourceReader` gắn nhãn Viewer/Preload/Background. Chỉ một file action chạy tại một thời điểm (INV-4).
- **Đã đóng, không mở lại nếu chưa đo mới:** R01/R02/R03/R13 (stat đồng bộ trên UI thread, đo thấy không đáng kể), R04/R14, Q-R29 (chỉ NAS thật mới cần thêm), Server GC, bộ cấp phát băng thông.

### 3.2 Double booking
- **Nguyên tử ở phía hệ điều hành:** `FileMode.CreateNew` + `FileShare.None` (`PhysicalFileSystem`, `AtomicCacheFile`); `File.Move` không ghi đè; ghi file tạm rồi `Move(..., overwrite: true)`.
- **Khóa lạc quan:** quyết định P02 (`OperationJournal.AppendIfUnchangedSince`, `JournalTransaction(retryOf)`) chỉ ghi nếu journal chưa đổi kể từ lúc đọc, nếu không trả `Superseded`. Anh em: R09 `Dismiss`, FA-01.
- **Giữ chỗ:** Q-R27, mỗi thao tác đang chạy giữ một kernel event có tên; instance khác bỏ qua khi reconcile lúc khởi động.
- **Tuần tự hóa:** INV-4; mutex và pipe cho chế độ SingleWindow.
- **Khe hở được chấp nhận:** trong `InstanceMode.PerFolder`, journal dùng chung giữa các process, khoảng giữa đọc và ghi vẫn còn một khe hở nhỏ giữa các process (P02). Hậu quả chỉ là Recovery hiển thị tạm một mục pending, lần khởi động sau tự sửa. Không chọn khóa độc quyền vì phải đổi định dạng journal.
- **Đang được sửa theo đúng nguyên tắc này (PR #288, #290; kiểm tra trạng thái merge trước khi dựa vào):** lệnh xếp hàng mang định danh phiên bản thư mục (`_folderLoadEpoch`, chỉ tăng khi bắt đầu mở thư mục, khác `clock.CurrentFolder` vốn đổi theo từng action) và bị bỏ nếu thư mục đã đổi; dọn file đích dở của Copy chỉ khi thao tác này chứng minh đã tạo nó (`CopyCreationProof`). Độ dài file hay "chưa tồn tại lúc kiểm tra trước" không chứng minh được quyền sở hữu.

## 4. Danh sách kiểm tra khi viết code mới (mang sang project khác)

Đồng thời và I/O:
- [ ] Mọi pool, semaphore, hàng đợi có giới hạn rõ. Không có "một Task cho mỗi mục" không giới hạn.
- [ ] Không chặn luồng giao diện (`.Result`, `.Wait()`, I/O đồng bộ trong handler). I/O đồng bộ bắt buộc thì ở luồng riêng.
- [ ] Việc người dùng đang chờ có làn riêng, không dùng chung pool với việc nền.
- [ ] Việc nền tự giảm khi việc tương tác bận hoặc khi I/O chậm. Hàng đợi đầy thì bỏ bớt hoặc từ chối nhanh.
- [ ] Mọi việc chạy lâu đều hủy được.

Tranh chấp dữ liệu:
- [ ] Không có check-then-act tách rời trên tài nguyên dùng chung. Dùng thao tác nguyên tử của OS/DB (`CreateNew`, rename, `UNIQUE`, `WHERE version=`).
- [ ] Sau mỗi `await`, kiểm tra lại định danh phiên bản (thư mục, epoch, anchor) trước khi ghi trạng thái hay chạm tài nguyên.
- [ ] Người thua biết mình thua: trả kết quả rõ ("superseded", lỗi trùng), không ghi đè im lặng, không ghi lỗi giả.
- [ ] Chỉ xóa hay dọn thứ chính thao tác này đã tạo, có bằng chứng sở hữu. Độ dài file hay kiểm tra từ trước không phải bằng chứng.
- [ ] Thao tác có hậu quả ghi lại ý định trước (journal Prepared) rồi kết quả (Committed/Failed), để khôi phục được khi chết giữa chừng.
- [ ] Mỗi khe hở chấp nhận được có ghi quyết định (vì sao, hậu quả, ai tự sửa).

Kiểm thử các lỗi này:
- [ ] Tái hiện bằng test xác định: cổng (`TaskCompletionSource`), fake đồng bộ; không `Thread.Sleep`/`Task.Delay`.
- [ ] Mỗi test có đối chứng cùng điều kiện nhưng không có tranh chấp, để chứng minh test không đỏ giả.
- [ ] Kiểm tra tay: hoàn tác bản sửa thì test mới phải đỏ.
