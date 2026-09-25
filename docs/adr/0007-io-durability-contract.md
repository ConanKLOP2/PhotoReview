# ADR 0007: Hợp đồng độ bền ghi (IO01) — journal có thể cấu hình, session không fsync, bỏ qua file không đọc được có cảnh báo

- **Trạng thái:** Chấp nhận (Accepted), 2026-09-24 — quyết định của người dùng (IO01).
- **Thay thế:** quy tắc tạm "❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02" trong `task_on_progress.md` (quy tắc này hết hiệu lực khi các phần triển khai dưới đây được merge).
- **Triển khai:** IO03 (journal), IO04 (session), IO05 (enumeration) — mở lại sau khi Q-AR5 đóng IO02–IO07; làm **sau khi nhánh i18n (`feat/i18n`) được merge** vì đụng `FileActionController`, `MainViewModel`, Settings UI và chuỗi giao diện.

## Bối cảnh

Hiện trạng (master `199bd63`):

| Dữ liệu | Cách ghi | Chạy ở đâu |
|---|---|---|
| Operation journal (`OperationJournal.AppendLines`) | `FileOptions.WriteThrough` + `Flush(true)` cho **mỗi** bản ghi; mỗi Move/Recycle ghi 2 bản ghi (Prepared, Committed) | UI thread với thao tác đơn lẻ (AR04 giữ nguyên) |
| Settings (`SettingsStore.Save`) | `WriteAllTextAtomic`: file tạm `WriteThrough` + `Flush(true)` rồi `File.Move(overwrite)` | Khi người dùng bấm Lưu |
| Session (`SessionStore.Save`) | như Settings | Mỗi lần mở folder / cập nhật vị trí |
| Preview/thumbnail cache | ghi file tạm + rename, **không** fsync | Worker |

Đo trên máy người dùng (ổ C, SSD; 200 lần ghi journal, 100 lần ghi session, đo ngày 2026-09-24):

| Kiểu ghi | P50 | P95 | max |
|---|---:|---:|---:|
| Journal `WriteThrough` + `Flush(true)` (hiện tại) | 1,79 ms | 2,42 ms | 16,9 ms |
| Journal chỉ `Flush(true)` | 1,32 ms | 2,08 ms | 2,8 ms |
| Journal không fsync (OS cache) | 0,36 ms | 0,61 ms | 2,0 ms |
| Session/settings atomic 4 KB (hiện tại) | 3,75 ms | 5,47 ms | 21,6 ms |

⇒ mỗi thao tác file hiện tốn ~3,6 ms (đuôi > 17 ms) trên UI thread chỉ cho journal.

Phân biệt quan trọng: **process crash không làm mất dữ liệu đã ghi vào OS cache** (Windows vẫn flush); fsync chỉ bảo vệ trước **mất điện / OS crash**. Atomic temp+rename bảo vệ trước file nửa vời khi process crash; nó độc lập với fsync.

## Quyết định

### 1. Journal — cài đặt người dùng, 2 chế độ

| Chế độ | Hành vi | Mặc định |
|---|---|---|
| **Nhanh (J-C)** | Không `WriteThrough`, không `Flush(true)`; `Flush()` thường sau mỗi bản ghi để dữ liệu vào OS cache trước khi thao tác file bắt đầu. Thứ tự bản ghi giữ nguyên (Prepared ghi trước khi mutation, Committed sau). | ✅ **Mặc định** |
| **An toàn khi mất điện (J-D)** | Giữ `WriteThrough` + `Flush(true)` như hiện tại, nhưng việc ghi journal (và mutation đi kèm) chạy **ngoài UI thread**; thao tác vẫn chờ Prepared bền vững trước khi move/recycle. | tùy chọn |

- Hệ quả J-C: sau **mất điện/OS crash**, vài bản ghi cuối có thể mất ⇒ Undo/Recovery có thể không biết một thao tác vừa xảy ra (file đã ở đích nhưng journal không ghi). Process crash không bị ảnh hưởng.
- Cài đặt hiển thị trong Settings ("Độ bền nhật ký thao tác": Nhanh / An toàn khi mất điện) kèm mô tả rủi ro; lưu trong `AppSettings`, áp dụng ngay cho lần ghi kế tiếp.
- Bất biến giữ nguyên ở cả hai chế độ: không bao giờ ghi `Committed` trước khi filesystem mutation hoàn tất; một bản ghi = một dòng JSON hoàn chỉnh; đọc journal bỏ qua dòng hỏng cuối file.

### 2. Settings / Session (S-B)

- **Settings:** giữ nguyên (atomic + `WriteThrough` + `Flush(true)`) — hiếm khi ghi, là cấu hình người dùng tự chỉnh.
- **Session:** bỏ `WriteThrough`/`Flush(true)`, **giữ** atomic temp + rename. Mất điện có thể làm mất vị trí xem cuối hoặc để lại file session rỗng/hỏng ⇒ `SessionStore.Load` phải coi file rỗng/hỏng như "không có session" (không crash, không cảnh báo lỗi cho người dùng).
- Tách API: `IFileSystem.WriteAllTextAtomic(path, text, durable: bool)` (hoặc hai method) thay vì một policy chung cho mọi text write.

### 3. Enumeration — file/thư mục không đọc được (E-B)

- Khi mở folder, file hoặc mục không truy cập được (quyền, bị khóa, lỗi I/O) được **bỏ qua**, đếm lại, và hiển thị **cảnh báo rõ** cho người dùng (ví dụ "Bỏ qua 3 file không đọc được", có cách xem danh sách), đồng thời ghi metric/log.
- Không dùng `EnumerationOptions.IgnoreInaccessible = true` một cách im lặng; tổng số ảnh hiển thị phải phản ánh đúng là catalog đang thiếu.

## Phương án đã cân nhắc

- **Journal J-A** (giữ nguyên, chạy trên UI thread): an toàn nhất nhưng chặn UI 3–17 ms mỗi thao tác — thay bằng J-D cho người cần an toàn.
- **Journal J-B** (chỉ fsync Prepared): cần Recovery tự đối chiếu Prepared-không-Committed với filesystem; lợi ích (~40 %) nhỏ hơn J-C và phức tạp hơn J-D — không chọn.
- **Session S-A** (giữ fsync): bảo vệ dữ liệu không quan trọng với chi phí ghi thường xuyên — không chọn.
- **Enumeration E-A** (fail-fast) / **E-C** (bỏ qua im lặng): E-A chặn cả folder vì một file lỗi; E-C có nguy cơ review thiếu mà không biết — không chọn.

## Kiểm thử bắt buộc khi triển khai

- Journal: thứ tự Prepared → mutation → Committed ở cả hai chế độ; dòng hỏng cuối file bị bỏ qua; chuyển chế độ khi đang chạy; J-D không chạy trên UI thread (arch/integration test); crash/fault injection quanh write như hiện có trong `verify-all.ps1`.
- Session: file rỗng/hỏng ⇒ load trả về "không có session"; atomic round-trip; temp cleanup.
- Enumeration: thư mục có file bị từ chối quyền (tạo bằng ACL trong test) ⇒ catalog thiếu đúng số file, cảnh báo được phát, tổng số đúng.
- Perf: đo lại latency thao tác Move/Recycle (P50/P95) trước/sau cho cả hai chế độ.

## Triển khai (cập nhật 2026-09-24)

- **IO03** (journal: chế độ Fast mặc định / Power-loss safe, ghi ngoài UI thread): PR #65, merge `0a331de`.
- **IO04 + IO05** (session không fsync, giữ atomic; enumeration bỏ qua file không đọc được kèm cảnh báo): PR #66, merge `5c12231`.
- Quy tắc tạm trong `task_on_progress.md` đã được thay bằng hợp đồng này; các thay đổi độ bền sau này chỉ theo ADR này.
