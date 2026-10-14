---
id: FEAT-RENAME-DESIGN
order: 1390
summary: |-
  Thiết kế (CHƯA XÁC NHẬN, chưa có mã) cho "Đổi tên ảnh đang xem" bằng menu chuột phải (đảo Q-R49): dùng lại Move cùng thư mục + journal (không thêm kiểu journal mới), kiểm kê 36 nơi giữ đường dẫn (19 cần xử lý, 9 phải đổi khoá), xử lý riêng đổi chỉ hoa-thường, sidecar .xmp cùng giao dịch nhóm, cập nhật danh sách tại chỗ; 4 PR nhỏ, ước lượng 28-40 giờ-agent.
---

# FEAT-RENAME-DESIGN — Đổi tên ảnh đang xem (2026-10-14, CHƯA XÁC NHẬN)

Nguồn yêu cầu: người dùng, "Rename bằng menu chuột phải". Q-R49 ([`Q-R41-Q-R52-user-feedback.md`](Q-R41-Q-R52-user-feedback.md)) đã DECLINED 2026-09-28
vì "rủi ro hỏng index/cache, cần một lượt thiết kế riêng"; nay người dùng đảo lại. Tài liệu này là lượt thiết kế đó. **Chưa có dòng mã sản xuất nào.**
Mọi số dòng đối chiếu trên `origin/master` @ `57b04243`. Mục "chưa kiểm" là chỗ tôi KHÔNG đọc được bằng chứng; PR cài đặt phải kiểm trước.

## 0. Kết luận ngắn

1. **Cơ chế:** dùng lại `Move` + `OperationJournal` + `UndoService`, KHÔNG thêm `FileOperationType` mới (ADR 0003, mục "cross-build compatibility": bản cũ
   bỏ/ánh xạ sai dòng có `Type` lạ). Đổi tên = một Move có thư mục nguồn == thư mục đích. Bản cũ đọc đúng là Move. Chỉ cần một đường vào
   `FileActionService` mới (hiện `IsSamePath` từ chối cùng thư mục, `FileActionService.cs:536`, `:107` cho nhóm) và vá chỗ dùng `FileExists` sai với đổi chỉ-hoa-thường (mục 2.4).
2. **Danh sách/cache:** đổi tên chỉ đổi MỘT khoá đường dẫn (size/mtime giữ nguyên). Không có nguy cơ phục vụ pixel cũ (mọi khoá chứa path+size+mtime, INV-1);
   rủi ro thật là (a) catalog/session/presenter trỏ tên cũ -> `RemoveMissingCatalogItemAsync` (`ImagePresenter.cs:310`) gỡ nhầm ảnh vừa đổi tên,
   (b) Undo/Reconcile/Recovery dùng `FileExists(source)` sai khi chỉ đổi hoa-thường, (c) mồ côi cache (vô hại, bị LRU dọn).
3. **Vị trí:** đề xuất ảnh ở NGUYÊN chỗ (cùng chỉ số) cho v1, không nạp lại thư mục, không present lại (giữ zoom/pan/Keep zoom).
4. **Sidecar:** app hiện chỉ biết `.xmp` (không có .dop/.pp3/.acr). Đổi cả nhóm JPEG+RAW+XMP trong MỘT giao dịch journal nhóm có sẵn.
5. **Công:** 4 PR, ước lượng 28-40 giờ-agent (mục 7). Có 7 câu hỏi cần chốt (mục 8).

## 1. Kiểm kê mọi nơi giữ đường dẫn / danh tính tệp

Phương pháp: đọc mã (3 lượt rà song song, tôi đối chiếu lại các chỗ then chốt). "Hậu quả" = nếu KHÔNG làm gì sau khi đổi `a.jpg` -> `b.jpg`.
Xử lý: **RE-KEY** = đổi khoá/giá trị sang tên mới; **INVALIDATE** = xoá/huỷ theo path cũ; **GATE** = chặn bằng thế hệ/cổng; **-** = không làm gì (mồ côi lành tính / không giữ path).

| # | Nơi giữ (file:dòng) | Khoá / nội dung | Hậu quả nếu bỏ qua | Xử lý | Ai chịu |
|---|---|---|---|---|---|
| R01 | `ReviewCatalog._entries` (`ReviewCatalog.cs:16`), `CatalogEntry` (`CatalogEntry.cs:14-22`) | Path, Length, mtime, dims, CaptureGroup | Entry giữ tên cũ; lần present sau stat = Missing -> gỡ khỏi catalog (`ImagePresenter.cs:310`); tên mới chỉ về khi nạp lại folder | RE-KEY: API mới `ReviewCatalog.RenameEntry(old, new, index?)` (chưa có Rename/Replace; `UpdateMetadata :158` từ chối đổi path) | Core.ReviewCatalog |
| R02 | `_indexByPath :46`, `_pathsCache :59`, `_removedPositions :54` | path -> chỉ số (OrdinalIgnoreCase) | Tên cũ còn resolve, tên mới = -1 | RE-KEY cùng R01, `InvalidateIndex :135` | Core.ReviewCatalog |
| R03 | `StructuralVersion :67` | bộ đếm cấu trúc | Người tiêu thụ (compare index `ImagePresenter.cs:784`, preload snapshot `SharedServiceRegistration.cs:197`) không làm mới | RE-KEY: RenameEntry phải tăng (UpdateMetadata KHÔNG tăng) | Core.ReviewCatalog |
| R04 | `CaptureGroup` (`CaptureGroup.cs:8-15`: JpegPath/RawPath/XmpPath) | 3 đường dẫn, OrdinalIgnoreCase | Nhóm trỏ tên cũ; đổi lẻ một thành viên phá ghép cặp theo stem (`CaptureGroupBuilder.cs:20-50`) | RE-KEY cả nhóm theo stem mới (mục 3) | Core.ReviewCatalog |
| R05 | `CurrentIndex :77` | chỉ số | Giữ nguyên là đúng (đổi tên không dịch chỉ số) | - (cẩn thận: Remove+Restore làm `CurrentIndex++`, cần `SetCurrent`) | - |
| R06 | `SessionState.CurrentPath` (`SessionStore.cs:13`), ghi tại `ImagePresenter.cs:633`, `UpdateSessionPath` (`MainViewModel.cs:1263`) | path ảnh đang xem, JSON theo folder | Khởi động lại: `IndexOf` = -1 -> rơi về ảnh 0, im lặng (`FolderLoadCoordinator.cs:197`) | RE-KEY (`UpdateSessionPath(new)`) | App.Shared.MainViewModel |
| R07 | `SessionState.Skipped` (`SessionStore.cs:14`; thêm ở `MainViewModel.cs:537-553`) | danh sách path bỏ qua | Mất dấu skip của ảnh đó (không có consumer lọc theo nó: chưa tìm thấy) | RE-KEY (thay chuỗi; chưa có helper) | App.Shared.MainViewModel |
| R08 | `ImagePresenter._currentNavigationPath :58`, `_presentedFilePath :57`, `CurrentPhotoInfo :127`, `CurrentPresentedPath :165` | path đang trình diễn | `CurrentFilePath()` (`MainViewModel.cs:764`) trả tên cũ: Copy tên/đường dẫn, Delete, Move nhắm tệp không còn | RE-KEY bằng API mới `ImagePresenter.RetargetPath(old,new)` (KHÔNG `PresentAsync`: tránh chớp/đặt lại zoom) | App.Shared.ImagePresenter |
| R09 | `_compareIndex/_compareIndexVersion` (`ImagePresenter.cs:50-51`, dựng `:784-789`) | path -> (trái, phải), regex theo tên (`ComparePairService.cs:11-14`) | Cặp compare sai; đổi tên có thể tạo/phá cặp | RE-KEY: tự dựng lại khi `StructuralVersion` đổi (R03) | App.Shared.ImagePresenter |
| R10 | `CompareViewModel._leftPath/_rightPath/_selectedPath` (`CompareViewModel.cs:15-19`) | 2 path + chọn | Compare thao tác tên cũ | INVALIDATE: `Clear()` (đã gọi qua `OnCatalogChanged`, `MainViewModel.cs:1243-1252`) | App.Shared |
| R11 | `MainViewModel._lastNotifiedImagePath :154` | path cuối đã báo | Lần báo sau coi là ảnh mới, đặt lại `ForceHighQualityScaling` (lành tính) | RE-KEY (1 dòng) | App.Shared |
| R12 | `MainViewModel._skippedEntries :260` | danh sách tệp không đọc được | Hiện tên cũ tới khi nạp lại | - | - |
| R13 | `FileActionController._movePositions :537` (cap 256) | gợi ý vị trí Undo-Move | Hạ cấp êm sang `SortedInsertIndex :577` | - | - |
| R14 | `UndoService._moveHistory/_moveFingerprints/_lastUndoAction` (`UndoService.cs:20-33`) | Source/Destination, khoá theo path đích | Nếu ảnh vừa được Undo về rồi đổi tên: Undo cũ trỏ tên cũ; còn lại không ảnh hưởng | Đăng ký rename như Move (`Register`), xem mục 6 (đây là điểm quyết định) | Core.UndoService |
| R15 | `PreviewImageService._cache` RAM (`PreviewImageService.cs:22`), `ImageCacheKey.Path = GetFullPath().ToUpperInvariant()` (`ImageCacheKey.cs:67-79`) | path+len+mtime+box+backend | Miss -> giải mã lại ảnh hiện tại (không phục vụ nhầm); entry cũ mồ côi tới khi LRU | RE-KEY (tuỳ chọn, tối ưu): API mới `RekeyCachedPath(old,new)` thêm key mới trỏ cùng `IDecodedImage` bất biến; tối thiểu: INVALIDATE bằng `EvictCachedPath` (`:697`) | Imaging.PreviewImageService |
| R16 | `_originalDimensions :28` | key giống R15 (`CreateOriginal`) | Mồ côi (rò tới LRU); `EvictCachedPath` KHÔNG xoá nó | INVALIDATE: thêm vào `EvictCachedPath` (khoảng trống đã có sẵn) | Imaging.PreviewImageService |
| R17 | `_previewLoads :25` (in-flight dedup, theo (key, epoch)) | key + epoch | Tải dở của path cũ có thể công bố 1 entry mồ côi (ghi chú `:702-704`); không bao giờ phục vụ tệp mới | - | - |
| R18 | Cache đĩa `<SHA256>.pv4` (`GetDiskCachePath :877-900`; hash của "preview-v5-box\|PATH\|len\|ticks\|..."), `PreviewCacheFile :48` (không chứa path) | hash | Lần xem đầu sau đổi tên giải mã lại (miss); tệp cũ mồ côi tới prune 4 GB | - (tối ưu tuỳ chọn: đổi tên tệp cache sang hash mới; không đề xuất cho v1) | - |
| R19 | `SourceBytesCache._cache :12`, `_inFlight :13`, `_pathVersions :27` | RangeKey(path, len, mtime, off, count) | Byte cũ mồ côi; tải dở có thể xuất bản muộn | INVALIDATE: `Evict(old)` (`:181`) đã được `EvictCachedPath` gọi (`:700`) | Imaging |
| R20 | `ThumbnailCache._ramCache :27` + đĩa `<key>.png` (`BuildKey :267-273`, hash path+len+mtime) | hash | Thumbnail tạo lại; mồ côi (1 GB LRU) | - (không có API theo path; không cần) | - |
| R21 | `PreloadScheduler._preloadedKeys :67` | ImageCacheKey | Key cũ mồ côi; tên mới đếm là preload-miss | INVALIDATE: `RemovePreloadedKeysForPath(old)` (`:177`) | Imaging.Preload |
| R22 | `PreloadScheduler` `queued :478`/`running :483` (theo `entry.Path` từ snapshot) | path thô | Snapshot trễ chứa tên cũ -> log "cannot stat" rồi bỏ qua (`:606-610`); cửa sổ preload thiếu ảnh vừa đổi tên | GATE: `Cancel() :157` (như Move, `FileActionController.cs:286`) rồi chạy lại quanh chỉ số hiện tại | App.Shared.FileActionController |
| R23 | `PreloadBusyBackoff._states :16` | path | Trạng thái cũ vô hại | - | - |
| R24 | `PreloadOrderService`/`PreloadWindow` | theo chỉ số, không khoá path | Chỉ cần định tâm lại nếu đổi vị trí (mục 4) | - | - |
| R25 | `FileHashService._cache :35` (hash nội dung, khoá path, kiểm Length+mtime) | path | Mồ côi; tên mới tính lại (kết quả như cũ) | - | - |
| R26 | `ZoomDetailLoader` (`Target :290`, một `_cts :38`) | token+path+key | Giải mã bản gốc dở đọc path cũ -> lỗi FileNotFound -> trạng thái lỗi/đường gỡ-catalog | GATE: `CancelPending :237` + `OnPreviewPresented(token,newPath,newKey)` lại (`:132`) | App.Shared |
| R27 | `RawDecoder._containerInfoCache :33` (256 mục, RawInfoKey path+len+mtime) | path | Mồ côi, đọc lại header | - | - |
| R28 | `NavigationStatWorker` (`NavigationStatWorker.cs:19`) | hàng đợi closure, không giữ path | Stat dở trên path cũ = Missing | GATE: `StopForAction()` (`GenerationClock.cs:71`) trước khi đổi tên: stat dở bị bỏ vì `IsNavigationCurrent(token)` sai (`ImagePresenter.cs:303`) | Core |
| R29 | `FolderLoadCoordinator`: `PendingOrder`, `_readabilityProbe`, snapshot Explorer (`ReplaceOrder :379` đòi hoán vị CHÍNH XÁC) | path scan | Snapshot Explorer về SAU đổi tên chứa tên cũ -> `ExplorerSnapshotValidator` (`:6`) không khớp; probe có thể gỡ tên cũ (vô hại) | GATE: đổi tên chờ `PendingOrder` như mọi file action (INV-9, architecture.md) | App.Shared.FolderLoadCoordinator |
| R30 | `OperationJournal` dòng Prepared/Committed (`OperationJournal.cs:19`: Source/Destination/Size/mtime) | path chuỗi, append-only | Lịch sử đúng như đã xảy ra, không sửa | - (dòng mới ghi tên mới) | - |
| R31 | `OperationJournal.ReconcileOne :506-518`, `:640-643` (`FileExists(Source)`) | path | Đổi chỉ-hoa-thường: nguồn "còn" -> bị kết luận Failed dù thành công | INVALIDATE logic: nhánh case-only (mục 2.4) | Core.OperationJournal |
| R32 | `RecoveryFileCheck` (`:133`), `RecoveryRetryService` (`:89`, `:96`, `:176`, `:238`, `:251`) | FileExists nguồn/đích | Cùng lỗi case-only: S,D "cùng tồn tại" = Conflict | Nhánh case-only (mục 2.4) | Core |
| R33 | `UndoService.UndoMoveAsync :174-:180` (`FileExists(Destination)`, `FileExists(Source)` -> "đã đổi") | path | Undo đổi-hoa-thường luôn bị từ chối | Nhánh case-only | Core.UndoService |
| R34 | `WindowsRecycleBin.TryRestore(path,size,mtime)` / candidate chọn theo path+size+mtime | path lúc xoá | Không ảnh hưởng: đổi tên xảy ra trước khi xoá; nếu tên mới trùng một mục đã xoá, Undo xoá bị từ chối an toàn ("RecycleTargetExists", `UndoService.cs` nhánh Recycle) | - | - |
| R35 | `AppSettings.LastMoveToFolder/LastCopyToFolder :62,:65`, ActionProfiles (`ActionsJson`) | thư mục, mẫu đích | Không giữ tên tệp | - | - |
| R36 | `InfoOverlayViewModel.SetFolder :192`, tiêu đề cửa sổ/`TitleBarFormatter` | folder; tên tệp hiển thị | Tên cũ tới lần cập nhật sau (tiêu đề: **chưa kiểm** dòng mã) | RE-KEY qua `RetargetPath` (R08) | App.Shared |

**Tổng: 36 nơi.** 19 cần xử lý chủ động (R01-R04, R06-R10, R14-R16, R19, R21, R22, R26, R28, R29, R31-R33 tính cả các nhánh case-only; R14 là điểm quyết định),
trong đó **9 thật sự đổi khoá/giá trị đường dẫn** (R01, R02, R04, R06, R07, R08, R10 qua Clear, R15 tuỳ chọn, R26); 17 còn lại không làm gì (mồ côi lành tính hoặc không giữ path).
`ViewerState` không giữ path (chỉ zoom/offset): đổi tên không làm đổi zoom (chỉ đúng nếu KHÔNG gọi `PresentAsync`).

Các bản đồ "một chỗ" thiếu trong mã hiện tại: không có `ReviewCatalog.Rename*`, không có `FileHashService.Evict`, `EvictCachedPath` không đụng `_originalDimensions`/Thumbnail/hash.

## 2. Cơ chế đổi tên

### 2.1 Dùng `FileActionService.Move` + journal + Undo có chạy đúng không?

Đọc `FileActionService.ExecuteAsync` (`:482-720`), `JournalTransaction` (`:135 VerifyMoved`), `UndoService`, `PhysicalFileSystem.Move :30` (= `File.Move` không ghi đè):

| Yêu cầu | Hiện trạng | Kết luận |
|---|---|---|
| Đích = thư mục + CÙNG tên tệp | `destinationPath = Path.Combine(folder, GetFileName(source))` (`:543`); request chỉ có `Destination` = thư mục | Không biểu diễn được tên mới -> cần request mới (`RenameRequest(Source, NewFileName)`) |
| Cùng thư mục | `IsSamePath -> IOException(Tr.CoreFileActionSameFolder)` (`:536`; nhóm `:107`) | Đường mới bỏ kiểm này |
| Trùng đích | `FileExists(destinationPath)` -> lỗi (`:553`), cộng `File.Move` fail-if-exists | Đúng; `File.Move` không có REPLACE_EXISTING nên cả TOCTOU cũng an toàn (tốt hơn Copy) |
| Ghi đè | Không bao giờ | Giữ: KHÔNG ghi đè (khuyến nghị cố định) |
| Verify | `VerifyMoved`: `FileExists(source)` -> `MoveSourceNotRemoved` (`JournalTransaction.cs:135-141`) | SAI với đổi chỉ-hoa-thường (mục 2.4) |
| Chéo ổ / copy+xoá | Cùng thư mục = cùng ổ, luôn là rename NTFS nguyên tử | Không có `CrashPartial`/`CrashMoveCopiedNotDeleted` cho rename: ma trận lỗi nhỏ hơn |
| Nhóm RAW+JPEG+XMP | `ExecuteGroupAsync` đã có manifest, preflight, bù trừ (`RestoreMovedMembers :394`), Undo nhóm | Tái dùng, chỉ thêm "đích theo bản đồ tên" thay cho "thư mục + tên cũ" |
| mtime | Đổi tên không đổi LastWriteTime | `FileFingerprint` so khớp chính xác; không cần dung sai FAT |

### 2.2 Loại journal mới hay Move?

| Phương án | Ưu | Nhược |
|---|---|---|
| **A. Move cùng thư mục (khuyến nghị)** | Không đổi schema; bản cũ đọc đúng (Move); Recovery/Retry/Undo/compaction/ma trận lỗi dùng lại; ADR 0003 đã cân nhắc và bác "Type mới" | Recovery hiển thị chữ "Move" (thấy rõ cùng thư mục); phải vá case-only |
| B. `FileOperationType.Rename` | Nhãn rõ | Bản cũ: `LenientEnumConverter` rơi về `Move=0` (đọc được) hoặc bỏ dòng (ADR 0003); mọi `switch` phải thêm; compaction/Recovery/Undo/Fault matrix đều đụng; rủi ro dữ liệu lớn hơn |
| C. Move + cờ phụ `Rename:true` (nullable, `WhenWritingNull`) | Nhãn rõ, bản cũ bỏ qua thành viên lạ (ADR 0003) | Thêm vào `JournalEntry` + `Equals/GetHashCode` + `WithOutcome` + compaction; bản cũ khi tự ghi outcome SẼ làm rơi cờ (như GroupMembers) |

Khuyến nghị A; nếu muốn nhãn "Đổi tên" trong Recovery thì phái sinh từ "thư mục nguồn == thư mục đích" (không cần cờ).

### 2.3 Quy tắc tên (`RenameNamePolicy`, hàm thuần trong Core, kiểm trước khi journal)

Chuỗi người dùng nhập là PHẦN TÊN (stem); phần mở rộng gốc giữ nguyên (nếu người dùng gõ đúng đuôi cũ, cắt đi; đuôi khác coi là một phần tên: `a.v2` -> `a.v2.jpg`).

| Quy tắc | Xử lý | Lý do |
|---|---|---|
| Rỗng / chỉ khoảng trắng | Từ chối ("Tên không được trống") | - |
| Cắt khoảng trắng đầu/cuối | Tự cắt | Win32 cắt im lặng tên có dấu cách cuối -> tên kết quả khác tên gõ |
| Dấu chấm hoặc dấu cách ở CUỐI stem | Từ chối | `File.Move`/Win32 bỏ chúng im lặng: `"a."`->`"a"` có thể trùng tệp khác |
| Ký tự cấm `< > : " / \ \| ? *` và điều khiển 0-31 | Từ chối, báo ký tự đầu tiên | `:` cũng chặn luồng NTFS (`a.jpg:stream`) |
| Tên thiết bị `CON PRN AUX NUL COM1-9 LPT1-9` (cả `CON.x`, cả `COM¹²³`) | Từ chối | Win32 trả handle thiết bị / lỗi khó hiểu |
| Độ dài thành phần > 255 UTF-16 | Từ chối | Giới hạn NTFS |
| Đường dẫn đầy đủ >= 260 | Từ chối (nếu app KHÔNG longPathAware: **chưa kiểm** manifest; không tìm thấy `longPathAware` trong `src/*/app.manifest`) | Tên mới dài hơn tên cũ có thể vượt MAX_PATH dù tệp cũ hợp lệ |
| Tên mới == tên cũ (đúng từng ký tự) | No-op, không journal | - |
| Chỉ khác hoa-thường | Cho phép, đường riêng (2.4) | Nhu cầu thật ("img.JPG" -> "img.jpg") |
| Trùng tệp/thư mục có sẵn | Từ chối, không ghi đè | - |
| Tệp bị khoá (editor ngoài không FILE_SHARE_DELETE) | `IOException` (sharing violation) -> thất bại sạch, thông điệp nêu "đóng ứng dụng đang giữ tệp" | Decoder của app mở `ReadWrite\|Delete` (INV-8; `PhysicalSourceReader.cs:15-17`) nên preload/giải mã của CHÍNH app không chặn. `libraw_open_wfile` (đường LibRaw/OnZoom): **chưa kiểm** share mode, cần test Native; dự phòng: `ZoomDetailLoader.CancelPending` trước khi đổi tên |
| Ổ mạng | Cùng ổ, rename phía máy chủ; NAS có thể phân biệt hoa-thường | Dùng nhận diện bằng liệt kê thư mục (2.4), không giả định |
| Thư mục chỉ-đọc / tệp chỉ-đọc | `UnauthorizedAccessException` -> thất bại sạch (đã là một họ lỗi trong ma trận: `FailUnauthorized`) | - |

### 2.4 Đổi chỉ hoa-thường trên NTFS (điểm dễ sai nhất)

`File.Move("a.jpg","A.jpg")` thành công trên NTFS, nhưng MỌI kiểm tra hiện có coi đây là lỗi/ngược lại vì `FileExists` không phân biệt hoa-thường:

- `FileActionService.cs:553` `FileExists(dest)` = true -> "đích tồn tại" (từ chối sai);
- `JournalTransaction.VerifyMoved :135` `FileExists(source)` = true -> `MoveSourceNotRemoved` (kết luận thất bại dù đã đổi);
- `OperationJournal.ReconcileOne :508-515` -> Failed; `RecoveryFileCheck :133` -> Conflict; `RecoveryRetryService :89,:96`; `UndoService :174,:180`;
- bộ kiểm bất biến của ma trận (`FaultInvariants.cs:110` "Move Committed but the source is still there") và `InMemoryFileSystem` (OrdinalIgnoreCase).

| Phương án | Ưu | Nhược |
|---|---|---|
| **A. Nhánh case-only có thật (khuyến nghị)**: hàm `FileNames.DiffersOnlyByCase(a,b)` + "tên thật trên đĩa" lấy bằng liệt kê thư mục (`EnumerateFiles(dir, name)` trả đúng kiểu chữ đang lưu; thư mục phân biệt hoa-thường có HAI mục = hai tệp khác nhau -> từ chối); vá 6 chỗ ở trên | Đúng, nguyên tử, không cần P/Invoke (giữ L-AOT, không thêm `DllImport` vào Core) | 6 điểm sửa trong Core + test riêng; tệp nhỏ nhưng nằm ở dữ liệu thật |
| B. Từ chối đổi chỉ-hoa-thường ở v1 | Rẻ nhất, không đụng reconcile | Người dùng không sửa được "IMG_1.JPG"; thông báo phải giải thích |
| C. Hai bước qua tên tạm (`a.jpg` -> `a.tmp-guid` -> `A.jpg`) | Không đụng kiểm tra | KHÔNG nguyên tử: crash giữa chừng để tệp ở tên tạm; journal ghi đích cuối nên Recovery thấy S,D đều Missing = `Lost` — bẫy dữ liệu |

Khuyến nghị A, đưa vào PR1 (để reconcile/undo đúng ngay từ đầu). Nếu chi phí vượt dự kiến thì lùi về B (PR1 chặn rõ ràng), tuyệt đối không C.

### 2.5 Thất bại không đổi gì: Failed hay Dismissed?

Move hiện ghi `Failed` (một mục Recovery "Retry") cho mọi lỗi sau Prepared. Với rename gõ tay, "thử lại từ Recovery" vô nghĩa và làm rối danh sách.
Đề xuất (CHƯA XÁC NHẬN): khi lỗi và nguồn còn nguyên + đích vắng (`IsUntouched`, đã có `:744`) -> `DismissRolledBack` (đã dùng cho huỷ/nhóm-rollback). Mọi trường hợp mơ hồ vẫn `Failed`.

## 3. Sidecar và cặp RAW+JPEG

**Hiện có (đã kiểm):** chỉ `.xmp`. Không có `.dop/.pp3/.acr/.xml` ở đâu trong mã. Quét: `FolderLoadCoordinator.cs:143-146` (chỉ khi `RawSupportEnabled`);
ghép: `CaptureGroupBuilder` (`:8`, `ChooseSidecar`, `IndexSidecars :133`): khoá (thư mục, tên không đuôi cuối), OrdinalIgnoreCase; `<base>.xmp` thắng, nếu không thì
`<file>.<ext>.xmp` khi duy nhất; mơ hồ = không nhận. Cặp = đúng 1 JPEG + đúng 1 RAW cùng stem; `RawPairMode.Separate` không gom. XMP CHỈ được nhận khi có cặp JPEG+RAW;
RAW lẻ + `IMG.xmp` không được theo dõi (Move/Delete để yên).

**Đề xuất quy tắc:**

| Tình huống | Hành vi |
|---|---|
| Ảnh có `CaptureGroup` (JPEG+RAW[+XMP]) | Một giao dịch journal nhóm (`ExecuteGroupAsync` mở rộng): mọi thành viên đổi sang stem mới, MỖI thành viên giữ đuôi của nó; XMP `<base>.xmp` -> `<new>.xmp`, `<file>.<ext>.xmp` -> `<new>.<ext>.xmp` |
| Một thành viên bị khoá / đích đã tồn tại | Preflight từ chối cả nhóm (không đổi một nửa); lỗi giữa chừng -> bù trừ có sẵn (`RestoreMovedMembers`), nguyên tử theo nghĩa "tất cả hoặc quay lại" |
| `Separate` hoặc tệp lẻ | Chỉ đổi tệp đang xem (nhất quán với Delete/Move hiện tại); KHÔNG đoán sidecar |
| Thành viên mất ngoài app | Như nhóm hiện tại: bỏ qua và báo (`missingSources`), còn ít nhất 1 ảnh mới chạy |
| Sidecar ngoài `.xmp` (`.dop`, `.pp3`, `.acr`) | KHÔNG làm ở v1 (app chưa biết chúng); xem câu hỏi Q4 |

## 4. Sắp xếp và vị trí

Sự thật: catalog không có comparer riêng, thứ tự = thứ tự chèn (`ReviewCatalog.cs:16`); sắp xếp chỉ ở lúc nạp (`FolderLoadCoordinator.cs:159 ImageSortService`, mode
Tên (natural)/Kích thước/Mặc định; thứ tự Explorer áp bằng `ReplaceOrder :379` từ snapshot một lần). Đã có tiền lệ chèn theo tên: `SortedInsertIndex` (`FileActionController.cs:577`, chỉ chính xác với Tên A-Z).

| Phương án | Ưu | Nhược |
|---|---|---|
| **A. Ở nguyên chỗ (khuyến nghị v1)** | Ảnh dưới mắt người dùng không nhảy; "Next/Previous" giữ hàng xóm; không cần re-center preload; đúng cho Explorer/Kích thước/Mặc định (không thể tính lại không gọi lại COM) | Thứ tự lệch so với Explorer cho tới lần nạp lại folder |
| B. Chèn lại theo comparer (Tên asc/desc) | Đúng thứ tự khi sắp theo tên | Ảnh hiện tại "nhảy" vị trí trong dãy; preload phải định tâm lại; sai với Explorer order (chỉ xấp xỉ) |
| C. Nạp lại cả folder | Đúng tuyệt đối | Giết mục tiêu "không nạp lại" của yêu cầu; mất cache preload, chớp, tốn I/O (vi phạm quy tắc 1 của AGENTS) |

Triển khai A: `ReviewCatalog.RenameEntry(old,new)` thay path tại chỗ, giữ `CurrentIndex`, Length/mtime/dims, tăng `StructuralVersion`. Ảnh không được present lại:
`ImagePresenter.RetargetPath` đổi các trường path (R08), giữ bitmap và `ViewerState` -> zoom, pan, Keep zoom, neo zoom nguyên vẹn; `PendingOrder`/`StopForAction`/`Cancel preload`
rồi chạy lại preload quanh chỉ số hiện tại (các ảnh khác không đổi path nên cache RAM của chúng còn nguyên). Nếu chọn B: dùng `Remove`+chèn tại chỉ số tính được và `SetCurrent`
(bẫy: `Restore` tăng `CurrentIndex` khi chèn <= hiện tại, `ReviewCatalog.cs:242`).

## 5. Giao diện

| Hạng mục | Đề xuất | Mã tham chiếu |
|---|---|---|
| Hộp thoại | `RenamePromptDialog : NativeDialog` theo mẫu `ZoomPromptDialog` (`Dialogs/Native/ZoomPromptDialog.cs:11`): nhãn + một ô `Edit` (tự được canh giữa dọc bởi `EditTextCentering` cho mọi Edit không `ES_MULTILINE`), nhãn lỗi ẩn, OK/Cancel; tên KHÔNG kèm đuôi, chọn sẵn toàn bộ (`EM_SETSEL(0,-1)`), đuôi hiển thị chữ xám bên phải hoặc trong nhãn; lỗi hiện tại chỗ (cơ chế `OnOk` đã có: hiện `ErrorId`, đo lại, `ResizeClient`) | `SecondaryDialogPresenters.cs:14` mẫu presenter; `Win32SecondaryWindowPort.cs:266`; `Win32DialogService.cs` `ISecondaryWindowPort`/`SecondaryWindowKind` |
| Presenter | `RenamePromptPresenter` trong App.Shared (không UI): `InitialText`, `TryAccept(text)` -> chuỗi lỗi đã localize hoặc null, gọi `RenameNamePolicy` | L-SHARED |
| Hợp đồng dịch vụ | `IRenamePromptService.PromptRename(string currentStem, string extension) -> string?` | cùng mẫu `IZoomPromptService.cs:7-10` |
| WPF (`PhotoReview.App.exe` vẫn là bản mặc định theo architecture.md) | Cửa sổ XAML nhỏ + `WpfRenamePromptService` | `ClickZoomCustomDialog.xaml`, `WpfZoomPromptService.cs`; xem Q5 |
| Mục menu | `ContextMenuItemId.RenameFile` thêm CUỐI enum (tên được lưu trong `HiddenContextMenuItems`, `ContextMenuLayout.cs:5`), nhóm 5 (cạnh Copy name/path), `NeedsPhoto: true`; ẩn/hiện tuỳ biến tự có qua `Settings > Mouse`; mặc định HIỂN THỊ (danh sách đen) | `ContextMenuLayout.cs:7-27,60-80`; `ContextMenuModelBuilder.cs:~65-85` (thiếu case = ném); `ContextMenuController.cs:141-170` (thiếu case = no-op im lặng!); `MainWindow.xaml:61` (WPF) |
| Phím tắt | `ShortcutMappings.RenameFile` (tuỳ chọn, được để trống), `ReviewCommandType.RenameFile` thêm vào `IgnoresAutoRepeat`, router: sau cổng `!hasImage`, CHỈ khi không có modifier (như Recycle/Actions) | `ReviewCommand.cs`, `ShortcutRouter.cs`, `SettingsNormalizer.cs:146,259-296`, `SettingsFieldMap.cs:32,60`, `ShortcutsSettingsPage.cs`; mẫu: `ToggleCaptureMember` |
| Xác nhận trước khi đổi | KHÔNG hỏi thêm (đã có hộp nhập + Undo) | - |
| i18n | `main.menu.renameFile` (+`.automationName`), `settings.contextMenu.item.renameFile`, `settings.shortcut.renameFile` (+`.clearAutomationName`), `dialog.rename.title/label/ext`, `status.renamed`, lỗi tên (mỗi quy tắc 2.3) vào `en.json`, `vi.json`, `en.notes.json` | `LocalizationGuardTests.cs:86,177` |

**F2 mặc định?** Đề xuất của lead: F2 mặc định.
| | Ưu | Nhược |
|---|---|---|
| F2 mặc định | Quy ước Explorer, dễ khám phá; F2 hiện KHÔNG gán (`KeyId.F2` chỉ tồn tại); hộp thoại không phá huỷ; có thể xoá | Cấu hình hiện có gán F2 cho một ReviewAction: `DisableConflictingOptionalShortcuts` im lặng tắt rename (người dùng đó không thấy phím); thêm một phím mặc định mới cho cả người dùng cũ |
| Trống mặc định | Không đổi hành vi người dùng cũ | Khó khám phá; người dùng phải tự gán |
Khuyến nghị: F2 mặc định (xem Q2).

## 6. Undo/Redo, Recycle liền sau, editor ngoài, preload

- **Undo là một ô duy nhất**: `_lastUndoAction` (`UndoService.cs:27`, ghi ở `Register :104`); không có Redo (không tìm thấy). Đăng ký rename vào ô này sẽ GHI ĐÈ khả năng Undo của lần xoá/di chuyển ngay trước nó. Hai lựa chọn: (a) đăng ký như Move (nhất quán, mất Undo lần trước), (b) không đăng ký, người dùng đổi tên lại (không mất Undo cũ nhưng "đổi tên không Undo được" trái kỳ vọng). Khuyến nghị (a) + ghi trong thông báo trạng thái; xem Q3.
- Undo của rename = Move ngược (đích -> nguồn) như `UndoMoveAsync`; kết quả Undo phải được `FileActionController` hiểu là RE-KEY (`RenameEntry(dest -> source)`) chứ KHÔNG phải "chèn lại như Move vào thư mục" (`InsertRestoredMove :555`). Nhận biết bằng cùng-thư mục.
- **Recycle/Delete liền sau**: dùng đường dẫn mới; không có trạng thái nào còn tên cũ nếu R01/R08 đúng. `LacksRecycleBin`/`FitsInRecycleBin` theo ổ không đổi.
- **Editor ngoài đang giữ tệp**: thất bại sạch (2.3). `ExternalEditor` mở theo path hiện tại tại thời điểm bấm: sau đổi tên mở tên mới; một editor đã mở giữ handle cũ có thể ghi lại tên cũ (tạo bản sao) — rủi ro người dùng, ngoài kiểm soát của app.
- **Preload đang đọc tệp**: không chặn nhờ `FileShare.Delete` (INV-8), nhưng quy trình phải `StopForAction` + `Cancel` (như Move, `FileActionController.cs:286`) để không còn tác vụ nào cầm path cũ rồi báo "không đọc được".
- **Cổng**: `FileActionService.TryBegin` (INV-4: một action cùng lúc, `:44`); từ chối khi `IsBusy`/Compare đang chọn path khác thì lấy `CurrentFilePath()` (`MainViewModel.cs:764`).

## 7. Kế hoạch test, đột biến, luật kiến trúc, ước lượng, thứ tự PR

### 7.1 Bảng test

| Lớp | Test | Ghi chú |
|---|---|---|
| Đơn vị (Core) | `RenameNamePolicy`: bảng tên (rỗng, dấu cách/chấm cuối, ký tự cấm, CON/NUL/COM1/`COM¹`, 255/256, đuôi giữ nguyên, trùng đuôi cũ) | Hàm thuần |
| Đơn vị | `FileActionService.RenameAsync`: thành công/trùng đích/nguồn mất/bận (INV-4)/huỷ/lỗi `IOException` và `UnauthorizedAccessException`/journal không ghi được; kết quả `JournalPersisted` | Mẫu `FileActionServiceTests.cs` |
| Đơn vị | Case-only: `a.jpg`->`A.jpg` thành công + Undo + reconcile Committed + Recovery không Conflict; trên `InMemoryFileSystem` CẦN thêm chế độ "tên thật" (hiện chỉ OrdinalIgnoreCase) | Vá 6 chỗ mục 2.4 |
| Đơn vị | Nhóm JPEG+RAW+XMP: cả ba đổi, một thành viên khoá -> không đổi gì (bù trừ), XMP dạng `<file>.<ext>.xmp` | `CaptureGroupActionServiceTests` làm mẫu |
| Đơn vị | `ReviewCatalog.RenameEntry`: giữ CurrentIndex/metadata/nhóm, tăng StructuralVersion, trùng tên trả false, `IndexOf` OrdinalIgnoreCase | |
| Tích hợp (App.Tests/Integration) | MainViewModel: đổi tên ảnh hiện tại -> `CurrentFilePath`, session path, compare cleared, preload chạy lại, KHÔNG gọi `PresentAsync` (đếm), zoom/offset giữ; Undo trả tên cũ; snapshot Explorer trễ | `ReplaceOrder` + `PendingOrder` |
| Fault-injection | Mở rộng ma trận `FaultInjectionMatrix` (xem 7.2) | `tests/PhotoReview.Core.Tests/FileActions/FaultInjection*.cs` |
| Native/Slow | Đổi tên thật trên NTFS tạm: case-only, tên 255 ký tự, đường dẫn >260, tệp giữ bởi handle KHÔNG share-delete (mong lỗi sạch), tệp giữ bởi `libraw_open_wfile` | Tự dọn; KHÔNG chạm Thùng rác thật |
| UI (Shell.Tests) | Thêm `"rename"` vào `Cases()` của `DialogEditCenteringTests` (dpi 96/144 x vi/en; BitBlt: khẳng định vùng mẫu KHÔNG toàn nền, khoảng trống trên/dưới +-1, caret trong hàng chữ); test lỗi hiện tại chỗ + Enter/Esc; `ContextMenuControllerTests` (id mới), `ContextMenuModelBuilderTests`, golden `context-menu.v1.json` + `MenuGoldenRecorder`, `SettingsShortcutsPageTests` (27 -> 28 ô), `ShortcutRouterTests` (F2, có/không modifier, không ảnh), `ConfigKeyContractTests` | Danh sách do lượt rà UI; chạy `Category=UI` (không bỏ) |
| Kiến trúc | L-SHARED (presenter không WPF), L-SHELL, L-AOT (không `dynamic`/`DllImport`; case-only dùng liệt kê thư mục), `LocalizationGuardTests` (không chữ Việt trong .cs; key đủ ở en/vi) | Không đổi luật, chỉ phải tuân thủ |

### 7.2 Mở rộng ma trận lỗi (#482)

Cơ chế: `AssertAllFaultPointsAsync(name, create, act, ...)` đánh số mọi lời gọi biến đổi (`FaultInjectionFileSystem.Kinds`), nhân với `FaultKind` có thể áp (`Applies`),
rồi kiểm bất biến sau "restart + reconcile" (`FaultInvariants.CheckState`: không mất ảnh, không đổi không journal, mọi verdict đúng, reconcile idempotent, và các việc kế tiếp không ghi đè).
Thêm: kịch bản `Rename_single`, `Rename_group_jpg_raw_xmp`, `Rename_case_only`, `Undo_rename`; `AssertReconcileInterruptedAsync` cho từng cái. `Applies` phải coi rename là "move nguyên tử"
(không áp `CrashPartial`/`CrashMoveCopiedNotDeleted`: cùng ổ). Bộ kiểm: (1) "Move Committed nhưng nguồn còn" cần so tên thật khi `DiffersOnlyByCase`; (2) thêm bất biến
"tên đích không bao giờ bị ghi đè: tệp lạ cùng tên mới còn nguyên" (follow-up `Foreign` đã có mẫu, `FaultInvariants.cs:163-191`); (3) thêm "không có tên tạm sót lại" (nếu
sau này có đường hai bước); (4) bất biến "đổi tên không thay mtime/size".

### 7.3 Đột biến thủ công cần làm (docs/MUTATION-TESTING.md; chỉ bộ test dùng fake, KHÔNG Thùng rác thật)

Bỏ kiểm trùng đích; ghi đè (`File.Move(..., true)`); bỏ `RenameNamePolicy` từng quy tắc (chấm cuối, CON, ký tự cấm); bỏ nhánh case-only ở từng trong 6 chỗ; bỏ `StopForAction`;
không `Cancel` preload; `RenameEntry` không tăng `StructuralVersion`; không cập nhật session path; Undo rename đi nhánh "chèn lại như Move"; bù trừ nhóm bị bỏ; XMP không được đổi;
đổi thứ tự Prepared/mutation; `DismissRolledBack` ở sai điều kiện (khi nguồn đã đổi). Mọi mutant sống phải bịt, chạy lại bộ rộng (AGENT-HYGIENE 5.8).

### 7.4 Ước lượng (giờ-agent, ƯỚC LƯỢNG THÔ, chưa đo)

| PR | Nội dung | Ước lượng |
|---|---|---|
| PR1 | Lõi Core: `RenameNamePolicy`, `FileActionService.RenameAsync` (+ nhóm), nhánh case-only 6 chỗ, `InMemoryFileSystem` tên thật, ma trận lỗi, test đơn vị + Native; chưa có UI | 10-14 |
| PR2 | Catalog/session/presenter/cache: `ReviewCatalog.RenameEntry`, `RetargetPath`, `EvictCachedPath` bổ sung, gate/preload, đường Undo re-key, `FileActionController` | 7-10 |
| PR3 | UI: presenter + hộp thoại Win32 + (WPF) + menu + F2/settings + i18n + test UI/golden | 8-11 (Win32 5-7; WPF 3-4) |
| PR4 | Sidecar/nhóm nâng cao (phần nhóm 3 người dùng chọn: probe `.xmp` cho tệp lẻ, v.v.) | 3-5 |
| Tổng | | 28-40 |

PR theo thứ tự trên; mỗi PR dựa trên master (không xếp chồng). PR1 chưa lộ ra UI nên bật tính năng chỉ ở PR3. Tất cả PR chạm dữ liệu thật: chạy mô hình mạnh, mutation bắt buộc, KHÔNG auto-merge khi chưa xanh.

## 8. Rủi ro còn mở và điều cần người dùng quyết

Rủi ro còn mở (không cần quyết, cần kiểm khi cài): `libraw_open_wfile` share mode (6, chưa kiểm); manifest `longPathAware` (2.3, chưa kiểm); `TitleBarFormatter` có giữ tên không (R36); thay đổi
`InMemoryFileSystem` ảnh hưởng nhiều test cũ (cần cờ opt-in); đổi tên khi Explorer snapshot đang chờ (R29: bắt buộc `PendingOrder`); trạng thái mồ côi cache tới LRU/prune.

| ID | Câu hỏi | Phương án + ưu/nhược | Khuyến nghị |
|---|---|---|---|
| Q1 | Đổi chỉ hoa-thường (2.4) | A hỗ trợ đầy đủ (đúng, +6 chỗ sửa, +test) / B từ chối ở v1 (rẻ, thiếu tính năng) / C tên tạm (KHÔNG: mất dữ liệu khi crash) | A |
| Q2 | F2 mặc định? (mục 5) | F2 (dễ tìm; vô tình tắt rename với người đã gán F2) / trống (an toàn cho người cũ; khó tìm) | F2 |
| Q3 | Rename có vào Undo (ô đơn, ghi đè Undo trước)? | (a) đăng ký (nhất quán, mất Undo lần xoá liền trước) / (b) không đăng ký (giữ Undo cũ, không hoàn tác được rename) | (a) |
| Q4 | Phạm vi sidecar | A chỉ nhóm hiện biết JPEG+RAW[+.xmp] (nhất quán Delete/Move, đơn giản) / B thêm dò `.xmp`/`.dop`/`.pp3`/`.acr` theo `<stem>.x` và `<file>.<ext>.x` cho tệp lẻ (đỡ mồ côi sidecar, nhưng đoán, dễ đổi nhầm tệp lạ và phải thêm vào quy tắc Move/Delete cho nhất quán) | A ở v1, B là PR4 nếu người dùng cần |
| Q5 | Phạm vi UI: Win32 shell hay cả WPF | chỉ Win32 `PhotoReview.exe` (rẻ -3-4h, WPF `App.exe` là bản mặc định theo architecture.md nên thiếu tính năng) / cả hai | Cả hai |
| Q6 | Vị trí sau đổi tên (mục 4) | A nguyên chỗ / B chèn lại theo Tên / C nạp lại | A |
| Q7 | Thất bại không đổi gì: Dismissed hay Failed (Recovery) (2.5) | Dismissed (ít rối) / Failed (giống Move, giữ "Retry") | Dismissed |

Khi người dùng chọn: tôi (agent cài đặt) sẽ ghi lựa chọn vào `OPEN-DECISIONS.md` (fragment này đổi `CHƯA XÁC NHẬN` -> `ĐÃ XÁC NHẬN`) và bắt đầu từ PR1. Cho tới lúc đó
KHÔNG có mã sản xuất nào được viết; nếu người dùng không trả lời, làm theo cột "Khuyến nghị" và đánh dấu CHƯA XÁC NHẬN (quy tắc dự án), mỗi PR ghi cách hoàn tác (gỡ mục menu/phím; lõi không có UI thì vô hiệu).
