# AR15 — Dọn cache Imaging: overload chết, một writer nguyên tử, `SourceBytesCache` không hop thread

**Finding:** F18, F19 · **Quyết định:** không cần · **Kích thước:** ~0.5 ngày, 1 PR `refactor/ar15-imaging-cache-cleanup` (3 commit độc lập) · **GUI:** không · **Perf:** 15c cần một lần `run-matrix.ps1 -Profile quick` với `UseSourceBytesCache=true` (máy thật) · **Agent:** sonnet

## AR15a — xoá hai overload `logContext` không ai gọi

**Hiện trạng:** `src/PhotoReview.Imaging/Caching/DiskCacheStore.cs:255` `PruneDirectory(…, string? logContext, …)` và `:317` `ClearDirectory(…, string? logContext)` chuyển tiếp với `log: null` — tham số bị bỏ; `grep -rn "logContext" src tools tests` chỉ ra hai khai báo này. Overload thật ở `:217` và `:308` nhận `ILog?`.

**Thay đổi:** xoá hai overload. Build; nếu có caller ẩn (không kỳ vọng) chuyển sang overload `ILog?`. Kiểm tra `ImagingPublicSurfaceTests` (test bề mặt public của Imaging) — cập nhật danh sách nếu nó liệt kê hai overload này.

## AR15b — một primitive "temp file → ghi → rename"

**Hiện trạng:** hai bản độc lập cùng thuật toán và cùng comment về durability:

- `DiskCacheStore.WriteAtomicallyAsync(BitmapSource, path, …)` `:84-113`: PNG encoder, `FileMode.CreateNew`, 64 KiB buffer, `SequentialScan`, `FlushAsync`, `File.Move(overwrite: true)`, `finally TryDelete(tmp)`.
- `PreviewCacheFile.WriteAtomicallyAsync(bitmap, backend, orientation, …)` `:110-162`: header v7 + EXIF + JPEG, cùng chuỗi mở/flush/move/cleanup.

**Thay đổi**

1. Thêm `internal static class AtomicCacheFile` (`src/PhotoReview.Imaging/Caching/AtomicCacheFile.cs`):
   ```csharp
   internal static async Task WriteAsync(string cachePath, Action<Stream> writePayload, ILog? log, CancellationToken ct)
   {
       Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
       var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";   // khớp DiskCacheStore.TempFilePattern
       try
       {
           var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
           await using (stream.ConfigureAwait(false)) { writePayload(stream); await stream.FlushAsync(ct).ConfigureAwait(false); }
           File.Move(tmp, cachePath, overwrite: true);
       }
       finally { DiskCacheStore.TryDelete(tmp, log); }
   }
   ```
2. Hai `WriteAtomicallyAsync` gọi `AtomicCacheFile.WriteAsync(path, s => { …encoder.Save(s)… }, …)`; phần kiểm tra alpha/orientation/header của `PreviewCacheFile` giữ nguyên **trước** khi gọi.
3. Giữ comment "disposable cache, no WriteThrough" một lần ở `AtomicCacheFile`.

**Tests:** `CacheTempFileAndDiskProbeTests`, `PreviewCacheFileConcurrencyTests`, `DiskCacheStoreRobustnessTests` (đã có) phải xanh không sửa — chúng là gate hành vi (tmp pattern, cleanup khi lỗi, ghi đè). **Mutation:** đổi `FileMode.CreateNew` → `Create` trong primitive → test tmp-collision (nếu có) đỏ; bỏ `finally TryDelete` → test "leftover tmp after failure" đỏ. Nếu chưa có test bắt hai mutation này, thêm vào `CacheTempFileAndDiskProbeTests`.

## AR15c — `SourceBytesCache.GetOrRead` đọc trực tiếp trên thread gọi

**Hiện trạng:** `src/PhotoReview.Imaging/Caching/SourceBytesCache.cs:52-61`: `Lazy<Task<byte[]>>` bọc `Task.Run(ReadAndCache)` rồi `GetAwaiter().GetResult()` — hai slot thread pool cho một lần đọc. Cả 3 caller đã ở worker: `PreviewImageService.cs:631` (trong decode worker), `ThumbnailCache.cs:236` (trong `Task.Run`), `FileHashService.cs:46` (hash chạy nền). Flag `UseSourceBytesCache` mặc định tắt → tác động chỉ khi bật.

**Thay đổi**

1. `Lazy<Task<byte[]>>` → `Lazy<byte[]>` với `LazyThreadSafetyMode.ExecutionAndPublication`; `GetOrRead` trả `lazy.Value`; giữ `finally _inFlight.TryRemove(KeyValuePair(key, lazy))` (dedup theo identity như hiện nay).
2. Lưu ý `Lazy<T>` **cache exception**: khi `ReadAndCache` ném (file bị xoá giữa chừng), mọi waiter cùng lượt nhận cùng exception — hành vi giống `Task` hiện tại; entry bị remove ở `finally` nên lượt sau đọc lại. Ghi thành comment.
3. XML doc của `GetOrRead(string)` (`:38`) đã nói "cùng luật thread" — cập nhật: "chạy đồng bộ trên thread gọi; không gọi từ UI thread" và thêm `Debug.Assert(SynchronizationContext.Current is null)` hoặc kiểm tra `IUiScheduler` nếu có sẵn trong Imaging (không có → chỉ assert).

**Tests:** `SourceBytesCacheTests`/`SourceBytesCacheRobustnessTests` (đã có) xanh. Thêm: hai thread gọi `GetOrRead` cùng key với `IFileSystem`/stream giả đếm số lần mở → **đúng 1** lần đọc (mutation: bỏ `_inFlight.GetOrAdd`, đọc thẳng → đỏ vì 2 lần); thread gọi không đổi (`Environment.CurrentManagedThreadId` trong stub đọc == thread gọi; mutation: bọc lại `Task.Run` → đỏ).

**Perf (máy thật, một lần):** `run-matrix.ps1 -Profile quick` với `UseSourceBytesCache=true` trước/sau, F4; kỳ vọng không xấu hơn; ghi vào PR. Không đo → không merge 15c (AGENTS.md "Verify claims").

## Không làm (ghi để không lặp lại review)

Hợp nhất ba cache LRU (`PreviewImageService`, `ThumbnailCache`, `SourceBytesCache`) thành một generic: mỗi cái có eviction/persist khác nhau; chỉ làm khi phải sửa cùng một lỗi đồng thời ở cả ba. `BoundedLruCache` một lock: chưa có bằng chứng contention (decode chiếm 100–1000×) — theo dõi, không tối ưu trước.

## Verification / Acceptance

Gate chung + `PreviewCacheFileTests`/`DiskCacheStoreTests`. `grep -rn "Guid.NewGuid().ToString(\"N\") + \".tmp\"" src/PhotoReview.Imaging` = 1 (chỉ trong primitive). `grep -n "Task.Run" SourceBytesCache.cs` = 0.
