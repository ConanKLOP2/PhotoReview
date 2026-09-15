# Photo Review — Cơ chế chính và phân tích tốc độ load ảnh

Cập nhật 2026-09-15 theo source master tại commit c87d14a. Đây là tài liệu kỹ thuật để phân tích bottleneck và custom pipeline load ảnh; không chỉ là hướng dẫn sử dụng.

## 1. Mục tiêu đo hiệu năng

Mục tiêu thực tế là tối thiểu hóa thời gian từ thao tác chọn folder/ảnh đến khi người dùng thấy ảnh đúng, đồng thời giữ navigation kế tiếp không bị chờ. Tách bốn mốc:

1. T0 — người dùng chọn folder hoặc file.
2. T1 — scan catalog hoàn tất và có danh sách ảnh.
3. T2 — preview đầu tiên xuất hiện trên UI; đây là mốc quan trọng nhất.
4. T3 — ảnh adaptive rõ hơn xuất hiện, sau đó ảnh kế tiếp đã preload.

Metric nên theo dõi riêng T0→T1, T1→T2, T2→T3 và phím navigation→ảnh mới. Không gộp tất cả vào một thời gian LoadFolder.

## 2. Pipeline tổng quát

```text
Input folder/file
  -> normalize path + cancel generation cũ
  -> Directory.EnumerateFiles(top-level)
  -> ImageFileTypes.IsSupported
  -> start ExplorerOrderService query song song
  -> ImageSortService.Sort fallback
  -> cập nhật catalog và session
  -> ShowImageAsync(first/resume)
       -> thumbnail phase nếu LoadingMode=Preview
       -> adaptive/original preview phase
       -> cập nhật Image.Source trên UI
       -> start preload nền
  -> chờ Explorer snapshot + total source bytes
  -> nếu snapshot hợp lệ: reindex catalog và có thể present lại
```

Điểm thiết kế quan trọng: Explorer query và scan/sort không nằm hoàn toàn trên đường critical path của ảnh đầu tiên. App cố gắng hiển thị theo fallback trước, rồi mới thay bằng native Explorer order. Vì vậy khi tối ưu, phải đo riêng first-visible và final-order; không nên làm Explorer query đồng bộ trước ảnh đầu tiên.

### 2.1 So sánh chi tiết ba mode tải ảnh

Settings → **Chế độ tải ảnh** có **Fast**, **Preview** (mặc định) và **Original**. Ba lựa chọn dùng chung `GetPreviewAsync`, RAM cache và preload; khác nhau ở bước thumbnail và kích thước decode khi ảnh chưa được cache.

| Mode | Trình tự hiển thị khi RAM cache chưa có ảnh | Pixel được decode | Ưu tiên và chi phí |
|---|---|---|---|
| **Fast** | Decode bản adaptive rồi hiện ảnh, không chờ thumbnail. | `viewportWidth × DPI × 1.15`, giới hạn 1200–4000px. | Bỏ một bước đọc/decode thumbnail; thích hợp khi chuyển ảnh liên tục. |
| **Preview** | Decode/đọc thumbnail tối đa 800px và hiện trước, sau đó thay bằng bản adaptive. Nếu bản adaptive đang có task tải dở, app bỏ bước thumbnail và chờ task đó. | Bản rõ cùng target với Fast. | Thấy ảnh sớm hơn trong nhiều trường hợp, đổi lại có thể đọc/decode cùng nguồn hai lần trên RAM miss. |
| **Original** | Decode và hiện ảnh nguyên độ phân giải, không qua thumbnail. | `targetWidth=0`, nên WPF không đặt `DecodePixelWidth`. | Giữ nhiều pixel gốc nhất để xem chi tiết; ảnh lớn thường tốn RAM và thời gian decode hơn. |

Khi RAM cache đã có ảnh theo path, cả ba mode lấy bitmap đó ngay; Preview không hiện thumbnail. Đổi mode trong Settings hiện không xóa RAM cache, nên ảnh đã cache có thể tiếp tục dùng bitmap của mode trước. Original vì vậy không đảm bảo hiện pixel gốc đối với ảnh đã cache từ Fast/Preview cho đến khi cache được làm mới. Preload dùng cùng mode đang chọn, không tạo thumbnail trước.

### 2.2 Mode hiển thị và mode sắp xếp là lựa chọn riêng

Settings → **Chế độ mở ảnh** mặc định **Fit**: co ảnh lớn vừa viewport, không phóng đại ảnh nhỏ và cập nhật giới hạn khi resize. **100%**, **200%**, **400%** lần lượt scale bitmap đang hiển thị 1×, 2×, 4×, không đổi độ phân giải decode. Do đó zoom 400% ở Fast/Preview chỉ phóng bitmap adaptive; muốn xem tối đa pixel gốc phải dùng Original.

Settings → **Chế độ sắp xếp ảnh** có **Theo tên kiểu Explorer** (`Name`, mặc định, so sánh tên tự nhiên), **Theo kích thước tăng dần** (`SizeAscending`) và **Theo kích thước giảm dần** (`SizeDescending`). Khi bằng kích thước, app phân thứ tự theo tên kiểu Explorer. Đây là fallback sort khi scan; snapshot hợp lệ từ Windows Explorer sẽ ghi đè thứ tự của cả ba mode. Vì vậy cần đo riêng fallback sort và final order khi benchmark.

## 3. Mở folder: từng bước và chi phí

### 3.1 Hủy request cũ

LoadFolderAsync hủy và dispose CancellationTokenSource cũ, tạo folder generation mới. Mục đích là folder cũ không được ghi đè UI sau khi người dùng chọn folder mới.

Chi phí thấp, nhưng thao tác đổi folder liên tục có thể vẫn để lại công việc native Explorer hoặc decode WPF đang chạy. Cancellation không cưỡng chế dừng mọi thao tác đồng bộ đã vào decoder.

### 3.2 Scan file

Directory.EnumerateFiles dùng TopDirectoryOnly, sau đó lọc qua ImageFileTypes.IsSupported. Đây là I/O metadata/directory enumeration, chưa đọc toàn bộ pixel ảnh.

Bottleneck thường gặp:

- Folder có rất nhiều entry không phải ảnh: chi phí enumerate + extension check tăng theo tổng entry.
- Network/share hoặc antivirus làm Directory.EnumerateFiles chậm.
- Path normalization và FileInfo lặp lại về sau làm tăng metadata I/O.
- Scan trả về List rồi chuyển sang array scannedFiles, tạo thêm cấu trúc collection nhưng không phải bottleneck chính so với disk.

Custom có thể đo số entry, số ảnh hỗ trợ, thời gian enumerate, bytes metadata và latency theo storage local/network.

### 3.3 ExplorerOrderService chạy song song

TryGetSnapshotAsync được start sau scan và trước fallback sort. Service tạo STA worker, tìm Explorer window tương ứng, gọi native IServiceProvider → IShellBrowser → active view → IFolderView2, lấy item count và display name theo index.

Chi phí có thể gồm COM lookup, enumerate item native, release COM pointer và timeout tối đa khoảng 2 giây. Timeout trả fallback nhưng worker/COM có thể còn hoàn tất sau khi await caller đã tiếp tục.

Đây là đường ảnh hưởng đến final order, không nên để nó chặn T2. Nếu ưu tiên tốc độ tuyệt đối, có thể:

- giữ fallback order làm order hiển thị đầu tiên;
- chạy Explorer snapshot sau T2 hoặc chỉ chạy khi người dùng bật tùy chọn Explorer order;
- cache snapshot theo canonical folder + Explorer view state trong thời gian ngắn;
- giảm timeout chỉ sau khi đo tỷ lệ false fallback;
- bỏ qua snapshot khi không có matching Explorer window.

### 3.4 Fallback sort

ImageSortService.Sort chạy trong Task.Run. Tùy ImageSortMode, sort Name có thể chỉ dùng tên; sort Size cần FileInfo.Length; sort orientation/metadata có thể mở decoder từng file. Đây là điểm có thể biến folder open từ metadata-only thành nhiều file read/decode.

Đặc biệt, nếu sort orientation đọc EXIF cho toàn folder, T1 bị kéo dài theo số ảnh và tốc độ storage. Nếu UX ưu tiên thấy ảnh trước, nên dùng name/fallback để present ảnh đầu tiên rồi metadata-sort nền, hoặc cache metadata theo path + length + LastWriteUtc.

## 4. ShowImageAsync: critical path của một ảnh

### 4.1 Chọn ảnh

ShowImageAsync kiểm tra index, tăng _generation, đổi _index và đặt status. Mỗi lần navigation tạo generation mới; kết quả cũ bị bỏ qua tại các guard chính.

Hiện tại status dùng FileInfo(path).Length trước khi decode. Đây là một metadata read đồng bộ trên UI thread. Với local disk thường nhỏ, nhưng network/share hoặc nhiều navigation nhanh có thể làm UI hitch. Có thể cache file length trong catalog hoặc lấy từ scan.

### 4.2 Pha thumbnail

Khi LoadingMode=Preview, nếu RAM preview chưa có và không có preview task đang chạy, app gọi ThumbnailCache.GetAsync(path). ThumbnailCache mặc định decode tối đa 800px, BitmapCacheOption.OnLoad, Freeze bitmap rồi trả về.

Thumbnail giúp T2 nhỏ hơn vì ảnh nhỏ xuất hiện trước adaptive preview. Tuy nhiên đây là một decode/read riêng trước adaptive decode. Với SSD và ảnh nhỏ, lợi ích UX thường lớn; với ảnh rất lớn hoặc format decode chậm, tổng CPU/I/O có thể tăng.

ThumbnailCache có:

- RAM LRU mặc định 256 MB;
- disk quota mặc định 1 GB;
- in-flight dedup theo key;
- disk cache đọc PNG nếu có;
- ghi mới có thể tắt, và MainWindow hiện dùng persistNewThumbnails:false;
- cancellation của waiter không xóa công việc chung đang được caller khác dùng.

### 4.3 Pha adaptive/original preview

GetPreviewAsync kiểm tra _cache theo path. Nếu chưa có, kiểm tra _previewLoads theo path để dùng chung task đang chạy. Sau đó đọc LoadingMode: Original dùng targetWidth=0; mode khác tính target width từ viewport, DPI và quality multiplier 1.15, giới hạn 1200–4000px.

Decode chạy trong Task.Run:

1. Tính disk cache path bằng SHA-256 của path + file length + LastWriteUtc ticks + targetWidth.
2. Nếu PNG cache tồn tại, đọc bằng BitmapImage.OnLoad.
3. Nếu cache lỗi, xóa cache rồi decode source.
4. Nếu không có cache, mở FileStream với SequentialScan, decode WIC/WPF và Freeze bitmap.
5. Đưa bitmap vào RAM LRU.

Không ghi PNG disk cache mới trong pipeline MainWindow hiện tại; comment trong source cố ý tránh PNG encoding và durable write làm chậm review.

### 4.4 Điểm cần lưu ý về cache key

Disk key có targetWidth, nhưng RAM _cache và _previewLoads hiện chủ yếu dùng path. Vì vậy cùng một path sau resize, đổi mode hoặc đổi DPI có thể dùng lại bitmap cũ trong RAM dù target decode mới khác. Đây là điểm custom quan trọng:

- Nếu ưu tiên correctness khi resize: key nên gồm path + fingerprint + targetWidth + loading mode + DPI/quality.
- Nếu ưu tiên tốc độ navigation: path-only RAM key giảm decode lặp và thường tốt hơn khi viewport ổn định.
- Nếu resize thường xuyên: dùng generation/viewport bucket, ví dụ width làm tròn theo 256px, để tránh tạo quá nhiều bitmap.
- Nếu source thay đổi giữ nguyên length/mtime: cả disk key và hash fingerprint cơ bản có thể không phát hiện; muốn tuyệt đối cần content hash nhưng sẽ tốn I/O.

### 4.5 Present và Compare

Sau adaptive preview, MainImage.Source được gán trên UI thread. App bắt đầu PreloadAroundAsync ngay sau đó. Nếu có compare pair, ảnh chính có thể bị ẩn và hai ảnh compare được GetPreviewAsync tuần tự theo await; sau đó có thể hash hai file bằng Task.WhenAll nếu bật CompareHashEnabled.

Vì vậy Compare có đường critical path riêng:

- preview trái;
- preview phải;
- FileInfo size nếu bật;
- hash song song nếu bật;
- cập nhật status.

Nếu mục tiêu là thấy ảnh nhanh, nên present ảnh chính trước rồi load compare panel sau; hoặc load trái/phải song song bằng Task.WhenAll. Hash nên luôn ngoài first-present path nếu chưa cần để quyết định thao tác.

## 5. Decode và bộ nhớ

DecodeSource dùng BitmapImage.CacheOption=OnLoad. Stream được đóng sau EndInit, bitmap Freeze để dùng ngoài UI thread. DecodePixelWidth giúp WIC tạo bitmap nhỏ hơn thay vì giữ full-resolution trong RAM khi targetWidth > 0.

Chi phí decoded memory xấp xỉ width × height × 4 bytes, chưa tính overhead WPF/decoder. Một ảnh 4000×3000 có thể khoảng 48 MB cho BGRA32; preload 10 ảnh có thể nhanh chóng chiếm hàng trăm MB.

MainWindow preview RAM cache đặt policy tối đa 16 GB, nhưng đây là trần logic chứ không phải cam kết app có thể giữ 16 GB an toàn. ThumbnailCache có quota riêng 256 MB. Memory-pressure guard chỉ quyết định có tiếp tục preload hay không; nó không biến bitmap đang được WPF/UI giữ thành nhẹ hơn.

Custom knobs nên tách:

- max decoded bytes của RAM preview;
- max thumbnail RAM bytes;
- số decode đồng thời;
- số ảnh preload trước/sau;
- target decode width;
- memory pressure threshold;
- có preload toàn folder hay chỉ lân cận.

Không nên chỉ tăng cache quota để tăng tốc; nếu working set vượt RAM vật lý, GC/trim/page fault có thể làm T2 và navigation xấu hơn.

## 6. Preload pipeline

PreloadAroundAsync hủy preload generation cũ, tạo token mới, chọn offsets +1..+8 và -1/-2. Nếu _totalSourceBytes nhỏ hơn FullFolderRamThresholdBytes thì mở rộng thành toàn folder; nếu chưa tính xong tổng bytes, _totalSourceBytes là long.MaxValue nên chỉ preload lân cận.

Mỗi vòng lặp:

1. Dispatcher.Yield để nhường UI/rendering.
2. Kiểm tra generation và cancellation.
3. Kiểm tra HasPreloadHeadroom.
4. Bỏ qua index ngoài range hoặc đã có cache.
5. Tạo PreloadOneAsync.
6. Chạy tối đa hai task trong batch nhờ _preloadSlots.

Điểm nghẽn và trade-off:

- Concurrency=2 bảo vệ disk/CPU nhưng có thể chưa đủ với SSD nhanh.
- Tăng concurrency có thể làm tranh chấp decoder, bandwidth và RAM.
- Yield giúp UI responsive nhưng kéo dài thời gian warm cache.
- Cancel không ngắt tức thì decode WPF đã bắt đầu.
- Preload task có thể hoàn thành sau khi người dùng đã đổi ảnh; generation guard ngăn present sai nhưng bitmap có thể vẫn được cache nếu task kết thúc theo đường hiện tại.
- Full-folder preload phụ thuộc tổng bytes được tính xong bằng FileInfo trên toàn catalog; không nên coi đây là tức thời.

Để custom, benchmark ba cấu hình: concurrency 1/2/4, offsets lân cận 2/8, và bật/tắt full-folder theo tổng decoded bytes thay vì source bytes.

## 7. Các bottleneck có xác suất cao

| Ưu tiên | Khu vực | Dấu hiệu | Hướng kiểm tra |
|---|---|---|---|
| P0 | Decode source ảnh lớn | T2/T3 cao, CPU cao, source reads tăng | đo decode từng format và target width |
| P0 | RAM key chỉ theo path | resize không đổi ảnh rõ, hoặc ảnh cũ được dùng lại | log targetWidth và cache key |
| P1 | Sort metadata/orientation | T1 cao trước ảnh đầu tiên | tách sort time và metadata reads |
| P1 | Thumbnail rồi adaptive decode | cùng source bị đọc/giải mã hai lần | so sánh Preview với Fast |
| P1 | Compare | ảnh chính chờ hai ảnh/hash | đo first-present trước/sau Compare |
| P1 | Explorer COM | final order chậm/timeout | log status, elapsed, window count |
| P2 | FileInfo trên UI thread | navigation hitch nhỏ | cache catalog metadata |
| P2 | Preload concurrency | RAM/CPU spike hoặc I/O queue | thử 1/2/4 và đo P95 |
| P2 | Disk cache lookup | nhiều File.Exists/PNG decode | đo hit rate và cache latency |

## 8. Instrumentation nên bổ sung

ReviewMetrics hiện có cache hits/misses, source bytes, source reads, decode milliseconds, presented images và present milliseconds. Nhưng decode milliseconds hiện được ghi cùng source read trong GetPreviewAsync; chưa tách scan, sort, thumbnail, disk-cache hit, source decode, queue wait, UI assignment và Explorer.

Nên thêm event record tối thiểu:

- operationId, folderGeneration, imageGeneration;
- path hash hoặc filename an toàn, không log path nhạy cảm nếu không cần;
- stage: Scan, Sort, Explorer, ThumbnailCacheHit, ThumbnailDecode, PreviewRamHit, PreviewDiskHit, SourceDecode, Compare, Present, Preload;
- queueStart, workStart, workEnd, presentEnd;
- targetWidth, viewportWidth, dpi, loadingMode;
- sourceLength, sourceLastWriteTicks, decodedWidth, decodedHeight;
- cache key version, hit/miss, cancellation, exception;
- thread id và whether user-visible/preload.

Metric cần báo cáo:

- T0→T1, T1→T2, T2→T3;
- navigation key→present P50/P95/P99;
- source read count và bytes per presented image;
- duplicate decode rate;
- thumbnail-to-adaptive promotion time;
- cache hit ratio theo RAM/disk/source;
- peak decoded bytes và working set;
- Explorer query latency/timeout rate;
- preload usefulness: ảnh được preload và thực sự xem trong N bước tiếp theo.

## 9. Chiến lược custom theo mục tiêu

### Ưu tiên ảnh đầu tiên

- Không chờ Explorer snapshot.
- Sort bằng filename hoặc giữ enumerate order trước.
- Dùng Fast hoặc thumbnail-only first, adaptive decode sau.
- Hoãn OriginalDimensions, Compare và hash sau first-present.
- Tránh FileInfo đồng bộ trên UI thread; dùng metadata scan nền.

### Ưu tiên chuyển ảnh liên tục

- Giữ RAM cache theo path khi viewport ổn định.
- Preload +1..+2 trước, sau đó mở rộng nếu hit rate tốt.
- Dùng concurrency 2 làm baseline; tăng chỉ khi P95 giảm và working set ổn định.
- Cancel preload cũ khi navigation, nhưng chấp nhận decode đã bắt đầu có thể hoàn tất.

### Ưu tiên chất lượng sau khi ảnh xuất hiện

- Present thumbnail trước, adaptive target width theo viewport×DPI.
- Không dùng target 4000 mặc định nếu viewport thực chỉ cần 1600–2400.
- Original chỉ decode khi người dùng yêu cầu.

### Ưu tiên RAM thấp

- Giảm MaxCacheBytes và FullFolderRamThresholdBytes.
- Không preload toàn folder chỉ dựa trên source bytes; ước tính decoded bytes.
- Giảm target width và concurrency.
- Clear cache theo folder/generation, nhưng đo chi phí re-decode sau đó.

## 10. Kế hoạch benchmark thực tế

Dùng cùng một fixture và warm/cold state:

1. 100 ảnh JPEG cùng kích thước.
2. 1.000 ảnh hỗn hợp JPEG/PNG/TIFF.
3. 20 ảnh cực lớn.
4. Folder có ảnh hỏng, Unicode, network/share nếu cần.
5. Explorer không mở, Explorer mở đúng folder, Explorer timeout.

Chạy từng mode Fast/Preview/Original và từng setting preload. Ghi ít nhất 30 lần navigation sau warm-up; báo cáo median, P95, max, source bytes, read count, peak working set và GC.

Không kết luận tối ưu từ một lần chạy. So sánh baseline trước/sau từng thay đổi riêng lẻ.

## 11. Các giới hạn hiện tại cần nhớ

- Contract tests chưa thay thế GUI automation và benchmark P95.
- RAM preview key và in-flight preview key chưa mô tả đầy đủ target width/mode.
- Disk adaptive cache được đọc nếu có nhưng pipeline MainWindow không ghi cache PNG mới.
- Thumbnail và adaptive có thể tạo hai lần đọc/decode cho ảnh đầu tiên.
- Explorer worker có thể tiếp tục sau timeout.
- Journal/recovery correctness không phải bottleneck load ảnh nhưng có thể ảnh hưởng thao tác sau load.
- MainWindow.xaml.cs vẫn gom nhiều trách nhiệm, nên thay đổi performance cần tránh làm hỏng cancellation/UI generation.

## 12. Verification sau mỗi custom

```powershell
dotnet run --project PhotoReview.Tests -c Release
dotnet publish PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o PhotoReview.App/bin/Release/net10.0-windows/publish
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-release.ps1 -ReleaseDirectory 'PhotoReview.App/bin/Release/net10.0-windows/publish'
```

Release test/build xanh chỉ chứng minh contract và artifact; quyết định tối ưu phải dựa trên trace/benchmark key-to-present và P95.
