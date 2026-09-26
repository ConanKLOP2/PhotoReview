# AR10 — Ngân sách RAM cache có hiệu lực ngay (đảo Q-R19)

**Finding:** F10 · **Quyết định:** Q-AR6 · **Kích thước:** ~0.5 ngày, 1 PR `feat/ar10-live-ram-budget` · **GUI:** không · **Máy thật:** không (đường nóng `TryGet`/`Set` không đổi) · **Agent:** sonnet

## Hiện trạng

Giá trị `ImageCacheRamPercent` chảy qua 4 chỗ và cả 4 "đóng băng" lúc dựng service:

1. `src/PhotoReview.App/App.xaml.cs:140,146` truyền `ImageCacheCapacityBytes` và `cacheRamPercent` theo giá trị vào `PreviewImageService`.
2. `PreviewImageService` ctor (`:146-151`) gọi `ResolveCapacity` một lần → `CapacityBytes { get; }` → `new BoundedLruCache(capacityBytes, …)`; `BoundedLruCache._capacity` là `readonly` (`src/PhotoReview.Core/Caching/BoundedLruCache.cs:7`).
3. `PreloadScheduler` được tạo một lần cho mỗi `MainViewModel` (`MainViewModelCompositionRoot.cs:45-60`) và chụp `previewService.CapacityBytes` vào record bất biến `PreloadOptions.FullFolderThresholdBytes` (`App.xaml.cs:160`; đọc ở `PreloadScheduler.cs:322,366` để quyết định preload cả folder).
4. `SourceBytesCachePolicy` singleton chia % giữa cache preview và cache byte nguồn (`App.xaml.cs:109-122`).

UI đã ghi "Có hiệu lực sau khi khởi động lại PhotoReview" (`Languages/vi.json:392`, `en.json:396`; comment XAML `SettingsWindow.xaml:190`). Q-R19 (2026-09-25) chấp nhận hành vi này. `MainViewModel.ShowSettings` (`:554-586`) đã có nhánh live cho `LoadingMode`/`DecoderBackend`.

## Q-AR6 — phương án

| | (a) Giữ Q-R19 (khởi động lại) | (b) Live theo mô hình đẩy |
|---|---|---|
| Hành vi | Không đổi | Kéo slider + Save → cache co/giãn ngay, preload tính lại chính sách cả folder |
| Công | 0 | ~80–120 dòng + test, 6 file |
| Rủi ro dữ liệu | 0 | 0 (chỉ cache RAM; không chạm file/journal) |
| Rủi ro hành vi | 0 | Thu nhỏ khi đang preload: evict + scheduler cancel/re-center → có thể thấy 1 lần decode lại ảnh lân cận |
| Test | — | LRU `SetCapacity` (mutation), `ApplyRamBudget`, scheduler đọc ngưỡng live |
| Ưu | Đơn giản, đã chấp nhận, UI đã ghi | Nhất quán với `LoadingMode`/`DecoderBackend` live; bỏ dòng "khởi động lại" |
| Nhược | Người dùng phải khởi động lại (~2 s, #46) | Đảo quyết định đã chốt; thêm 1 API mutable ở LRU |

**Khuyến nghị: (a).** Đổi % RAM rất hiếm, UI đã nói rõ, khởi động lại rẻ. Làm (b) chỉ khi người dùng muốn Settings hoàn toàn live. Nếu chọn (a): đóng AR10, ghi Q-AR6 = a trên `master` (PR docs, AGENTS.md "Decision log"), không sửa mã.

## Thay đổi nếu chọn (b) — mô hình đẩy

Không dùng `Func<long>` ở LRU: khi giảm dung lượng phải **chủ động evict** và scheduler phải **tính lại** chính sách; delegate kéo không làm được hai việc đó.

1. `src/PhotoReview.Core/Caching/BoundedLruCache.cs`
   - Bỏ `readonly` ở `_capacity`; thêm `public long Capacity { get { lock (_gate) return _capacity; } }`.
   - Thêm:
     ```csharp
     public int SetCapacity(long capacity)
     {
         ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
         lock (_gate)
         {
             _capacity = capacity;
             var evicted = 0;
             while (_size > _capacity && _lru.Last is not null)
             {
                 var oldest = _lru.Last!;
                 _lru.RemoveLast(); _items.Remove(oldest.Value.Key); _size -= oldest.Value.Size; evicted++;
             }
             return evicted;
         }
     }
     ```
   - `Set` giữ nguyên (đã dùng `_capacity` dưới lock).
2. `src/PhotoReview.Imaging/Caching/PreviewImageService.cs`
   - Tách `:146-151` thành `public void ApplyRamBudget(int? cacheRamPercent, long requestedBytes)`: gọi `ResolveCapacity(requestedBytes, cacheRamPercent, RamBudgetPolicy.GetPhysicalMemoryBytes(), _sourceBytesCache?.CapacityBytes, out var line)`, ghi `_log.Info(line)`, `Volatile.Write` vào backing field của `CapacityBytes` (đổi thành `{ get; private set; }` với đọc `Volatile`), gọi `_cache.SetCapacity(...)`; ctor gọi method này.
   - Ghi metric số entry bị evict vào `ReviewMetrics` (thêm counter `RamBudgetEvictions`, hiện trong Diagnostics).
3. `src/PhotoReview.Imaging/Preload/PreloadOptions.cs` + `PreloadScheduler.cs`
   - `FullFolderThresholdBytes: long` → `Func<long> FullFolderThresholdBytes`; hai chỗ đọc `:322` và `:366` gọi delegate. Ctor tiện ích (`:100-125`) nhận `Func<long> fullFolderRamThreshold`.
   - `App.xaml.cs:160`: `fullFolderRamThresholdBytes: () => previewService.CapacityBytes`.
   - Không cần API "re-center" mới: `MainViewModel` gọi `_preloadController.Cancel()` rồi present lại index hiện tại (`_presenter.PresentAsync(_catalog.CurrentIndex)`) như nhánh đổi backend đã làm.
4. `src/PhotoReview.App/ViewModels/MainViewModel.cs` `ShowSettings` (`:554`): chụp `previousPercent`; sau `changed`, nếu khác → `_previewService.ApplyRamBudget(new, settings.ImageCacheCapacityBytes)`; `_preloadController?.Cancel()`; present lại index hiện tại. **Không** `ClearCache`.
5. `SourceBytesCachePolicy`: **giữ cần khởi động lại** (flag bật/tắt đã cần restart theo `architecture.md`; `ResolveCapacity` vẫn trừ đúng phần đã dành vì nhận `sourceBytesCache?.CapacityBytes`).
6. i18n: bỏ câu cuối "Có hiệu lực sau khi khởi động lại PhotoReview." / "Applies after restarting PhotoReview." ở `settings.ramCache.hint`; xoá comment `SettingsWindow.xaml:190`. Chạy `tools/i18n-check.ps1`.
7. Docs: bảng "Settings có ảnh hưởng kiến trúc" trong `docs/architecture.md` thêm dòng `ImageCacheRamPercent` (live, đẩy qua `ApplyRamBudget`); ghi Q-AR6 = b trên `master` (PR docs, AGENTS.md "Decision log").

## Tests

- `tests/PhotoReview.Core.Tests/Caching/BoundedLruCacheTests.cs`
  - `SetCapacity_Smaller_EvictsOldestUntilFits`: 4 entry 10 B, `SetCapacity(25)` → còn 2 mới nhất, trả về 2. **Mutation:** xoá vòng `while` trong `SetCapacity` → đỏ.
  - `SetCapacity_Larger_KeepsEverything_AndAcceptsBiggerEntries`.
  - `SetCapacity_ZeroOrNegative_Throws`.
- `tests/PhotoReview.Imaging.Tests/RamCachePercentTests.cs` (đã có): thêm `ApplyRamBudget_ChangesCapacityBytes_AndEvicts` (dùng `PreviewImageService` với decoder giả như các test hiện có).
- `tests/PhotoReview.Imaging.Tests/PreloadEstimateCalibrationTests.cs`: ngưỡng đọc qua delegate — đổi giá trị delegate giữa hai lần `Recenter` → quyết định `wholeFolder` đổi tương ứng. **Mutation:** chụp delegate một lần trong ctor → đỏ.
- `tests/PhotoReview.App.Tests/ViewModels/MainViewModelAdvancedTests.cs`: `ShowSettings` với percent đổi gọi `ApplyRamBudget` (fake `IDialogService` trả `true` và đổi `SettingsStore.Current`), không gọi `ClearCache`.

## Verification / Acceptance

- Gate chung (summary). Không cần perf gate: `TryGet`/`Set` không đổi; `SetCapacity` chỉ chạy khi Save Settings.
- Chấp nhận: đổi % trong Settings, cửa sổ Diagnostics hiện `CapacityBytes` mới ngay; log có dòng "Memory budgets" thứ hai; `i18n-check` xanh.

## Không thuộc phạm vi

`PreviewDiskCacheCapacityBytes` (đĩa) và `SourceBytesCapacityBytes` — vẫn cần khởi động lại; ghi rõ trong hint nếu có control.
