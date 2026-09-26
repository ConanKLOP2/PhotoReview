# AR16 — Probe "đọc được" từng file khi mở folder: đo trước, rồi quyết

**Finding:** F20 · **Quyết định:** Q-AR7 (sau bước đo) · **Kích thước:** đo ~2 giờ (sonnet); nếu làm (c) ~1 ngày · **GUI:** không · **Máy thật:** **có** — fixture F4 (`work/diag/fixtures.local.json`) · **Agent:** sonnet đo, strongest thiết kế (c) vì chạm `FolderLoadCoordinator`/INV-7/INV-9

## Hiện trạng

`src/PhotoReview.Core/IO/PhysicalFileSystem.cs:186-233` `EnumerateReadableFilesWithStat`: sau khi lọc định dạng, **mỗi** file được mở `new FileStream(path, Open, Read, ReadWrite|Delete, bufferSize: 1)` rồi đóng ngay (`:216-226`) để phát hiện file khoá/không quyền; file lỗi → `onSkipped`. Gọi từ `FolderLoadCoordinator.cs:111` trong `Task.Run`, trước khi catalog sẵn sàng (T1) và trước frame đầu (T2). Với F4 (1841 file) = 1841 `CreateFile`+`CloseHandle` trên đường tới frame đầu, dù ADR 0007 chỉ yêu cầu "không bỏ qua im lặng", không yêu cầu probe *trước* khi hiển thị.

Đây là quyết định ADR 0007/IO04 có chủ ý (thay `IgnoreInaccessible`); đổi cần bằng chứng số.

## Bước 1 — đo (bắt buộc trước Q-AR7)

1. Thêm 2 mốc `PerfTrace` (EventSource đã có, D03) quanh vòng probe trong `FolderLoadCoordinator.LoadAsync`: tổng thời gian enumerate có probe, và số file. Không thay đổi hành vi.
2. Chạy trên máy thật, F4, cold và warm OS cache, 5 lần mỗi cell:
   - A: build hiện tại;
   - B: cùng build với biến môi trường tạm `PHOTOREVIEW_DIAG_SKIP_PROBE=1` (nhánh diag chỉ trong `PhysicalFileSystem`, xoá sau khi đo — pattern `DiagOptions` sẵn có, xem `DiagOptionsTests`).
3. Ghi vào `docs/refactoring/PERF-STATUS.md`: `probe ms` P50, `first visual` P50 A vs B, tỉ lệ. Ngưỡng quyết định: probe ≥ 5 % first-visual (≈ ≥ 8 ms trên nền 166 ms) → cân nhắc (c); dưới → (a).

## Q-AR7 — phương án

| | (a) Giữ probe trước | (b) Probe lười: bỏ hẳn, chỉ báo khi decode thật mở lỗi | (c) Probe nền sau frame đầu |
|---|---|---|---|
| Hành vi | Không đổi | Danh sách "Bỏ qua N file" chỉ xuất hiện khi người dùng/preload chạm tới file lỗi; file lỗi vẫn nằm trong catalog cho tới lúc đó (điều hướng tới nó → lỗi hiển thị, `ImagePresenter.cs:485-499` đã xử lý `FileNotFound` bằng remove, còn `UnauthorizedAccess` chỉ báo lỗi) | Catalog hiện ngay không probe; probe chạy nền cùng generation, khi xong → `OnFilesSkipped` + `ReviewCatalog.RemovePaths` (giữ current, như `ReplaceOrder`) |
| I/O trước frame đầu | 1 open/file | 0 | 0 (dời ra sau) |
| Rủi ro dữ liệu | 0 | 0 (không sửa file) | 0 |
| Rủi ro hành vi | 0 | Đếm "N ảnh" và phím End/Jump có thể tới file lỗi; session lưu index theo catalog có file lỗi | Catalog đổi sau frame đầu → INV-7/INV-9 phải mở rộng: remove sau interaction vẫn giữ current; preload đang chạy phải bỏ key của file bị remove |
| Test | — | Sửa `FolderLoadSkippedFilesTests`; thêm test present file không quyền → status lỗi + không remove | `FolderLoadCoordinatorTests.Races`: probe trễ sau đổi folder bị bỏ (generation); remove giữ current; `PreloadScheduler` cancel key bị remove |
| Ưu | Đơn giản, đúng ADR 0007 hôm nay | I/O ít nhất; mã ít nhất | Giữ trọn ngữ nghĩa "N file bị bỏ qua" mà không chặn frame đầu |
| Nhược | Trả 1 syscall/file cho trường hợp hiếm | Mất tính "biết trước"; đổi ngữ nghĩa ADR 0007 → cần ADR update | Phức tạp nhất; hai lần đi qua danh sách |

**Khuyến nghị:** đo trước. Nếu ≥ ngưỡng → **(c)** (giữ ADR 0007, chỉ dời thời điểm). Nếu < ngưỡng → **(a)** và ghi số vào PERF-STATUS để không rà lại.

## Nếu chọn (c) — thay đổi

1. `IFileSystem`: giữ `EnumerateFilesWithStat` (không probe) cho lần quét chính; thêm `bool TryProbeReadable(string path, out string? failure)` (một open/close) — `EnumerateReadableFilesWithStat` hiện thực bằng hai hàm này (không xoá, test cũ giữ).
2. `FolderLoadCoordinator.LoadAsync`: quét không probe → `_catalog.Reset` → present như hiện nay; sau `OnFolderLoaded` khởi chạy `ProbeInBackgroundAsync(generation, paths)` trên `Task.Run`, 8 file song song tối đa (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism = 8`), kết quả về UI thread qua `IUiScheduler` (ADR 0005: không `ConfigureAwait(false)` trong App — phần nặng nằm trong `Task.Run`).
3. Khi về: nếu `generation` không còn hiện hành → bỏ. Nếu còn: `_catalog.RemovePaths(skipped)` giữ current (thêm method vào `ReviewCatalog` cạnh `ReplaceOrder`, có `AssertOwnerThread`), `IPreloadController.ClearPreloadedKeys(paths)`/re-center, `_sink.OnFilesSkipped(folder, skipped)`.
4. Nếu file lỗi **đang** là current: `ImagePresenter` đã đang hiển thị lỗi (present gặp `UnauthorizedAccess` → status lỗi) → sau remove, present index mới (giống Delete).
5. Docs: `architecture.md` mục ADR 0007 "Quét folder" + INV-7/INV-9 mở rộng "snapshot probe trễ".

**Mutation (c):** bỏ kiểm tra generation trong callback → test "probe trễ sau đổi folder" đỏ; bỏ `ClearPreloadedKeys` → test preload đỏ.

## Verification / Acceptance

Bước 1: bảng số trong PERF-STATUS + Q-AR7 ghi trên `develop`. (c): gate chung + `run-matrix.ps1 -Profile gate` trước/sau (first visual không xấu hơn, S2/S3 không đổi) + `FolderLoadSkippedFilesTests` mở rộng.
