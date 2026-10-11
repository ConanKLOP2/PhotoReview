---
id: NOWPF-WP06
order: 620
summary: |-
  WP-06 (đợt 1c, 2026-10-11): tách PhotoReview.Imaging.Wpf (UseWPF) khỏi Imaging/Raw/LibRaw/TurboJpeg/Benchmarking (hết UseWPF, luật L-IMG Rule 3/11/12); codec C-02 bắt buộc ở PreviewImageService, ThumbnailCache, TurboJpeg, LibRaw, cache đĩa (hết mặc định WPF); fine-scale TurboJpeg = WIC Fant (WicPixelScaler, byte-exact với ScaleTransform cũ). Decode bench P50/P95 không hồi quy (<= +0,5 %). Hợp đồng v1.1 không đổi. Chưa đo: tune-matrix/ảnh đầu/peak WS của perf gate đợt 1.
---

# NOWPF-WP06 - tách PhotoReview.Imaging.Wpf, bỏ UseWPF khỏi Imaging* (2026-10-11)

Thẻ: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md) mục WP-06; tiền đề: WP-03/04/05 (cùng thư mục). Số đo: [perf/2026-10-11-wp06-imaging-wpf-split.md](../perf/2026-10-11-wp06-imaging-wpf-split.md).
Hợp đồng v1.1 (`Approved/contracts.v1.txt`) KHÔNG đổi (không tái sinh); kiểu WPF nào cũng không nằm trong hợp đồng.

## 1. Đã làm (toàn thẻ, một PR)

- **Project mới `src/PhotoReview.Imaging.Wpf`** (UseWPF, namespace `PhotoReview.Imaging.Wpf`, chỉ tham chiếu Core + Imaging): `WpfBitmapImageDecoder`,
  `WpfDecodedImage`, `WpfImageAdapter`, `WpfExifReader`, `WpfExifOrientation` (nửa WPF của `ExifOrientation`: `Read(BitmapMetadata)`, `Apply`, `CreateTransform`)
  và **`WpfBitmapSourceCodec`** = hợp nhất `WpfBitmapSourceCodec` (WP-03) + `WpfCacheImageCodec` (WP-04). Giữ ngữ nghĩa của codec cache: định dạng nguồn có thể
  mang alpha (kể cả indexed có palette không đục) -> Pbgra32, còn lại -> Bgr32. Mất so với codec cache cũ: không còn "mượn" `PixelBuffer` làm ảnh nền
  tảng (ném `ArgumentException`; bản WPF không bao giờ có PixelBuffer làm ảnh nền tảng, bản Win32 dùng `PixelBufferImageCodec`).
  `HasAlpha`/`IsFullyOpaque`/`AllAlphaOpaque`/`HasAlpha(IDecodedImage)` (trước ở `PreviewCacheFile.HasAlpha`) nay là static công khai của codec WPF.
- **Imaging, Imaging.Raw, Imaging.LibRaw, Imaging.TurboJpeg, Benchmarking: hết `UseWPF`** (không còn tham chiếu PresentationCore/WindowsBase). `FileFormatException`
  (WindowsBase) thay bằng lớp cha `FormatException` ở `FallbackImageDecoder`, `DiskCacheStore.IsCacheEntryFailure`, `RawDecoder`, `BenchmarkWorkloadRunner`
  (mở rộng nhẹ: mọi `FormatException` trong decode/đọc cache nay cũng là "lỗi fallback/entry hỏng"; trước đó `FileFormatException` thật chỉ do WPF ném).
- **Codec bắt buộc** (hết `WpfCacheImageCodec.Instance` mặc định): `PreviewImageService(metrics, isOriginal, target, platformCodec, ...)` (cả hai overload; tham số vị trí thứ 4),
  `ThumbnailCache(platformCodec, ...)` (tham số đầu), `TurboJpegDecoder(codec)`, `LibRawDecoder(codec, stageObserver?)` (ctor không tham số và ctor
  `Action<string>` đã bỏ), `ServiceFactories.CreateRawDecoder(..., codec)`, `PreviewCacheFile.WriteAtomicallyAsync/ReadAsDecodedImage(..., codec, ...)`,
  `DiskCacheStore.WriteAtomicallyAsync(image, codec, ...)`, `EmbeddedThumbnailReader.TryRead(path, codec)`, `BenchmarkImageExecutor(profile, files, codec, hasHeadroom?, decoder?)`.
  `PreviewImageService` không decoder/factory -> `WicDirectDecoder(codec, sourceReader)` (trước: `WpfBitmapImageDecoder`).
- **`ImageDecoderFactory(..., decoderDecorator?, wpfDecoderFactory?)`**: ô `Wpf` do provider tường minh, hoặc `wpfDecoderFactory`, hoặc ánh xạ sang provider `WicDirect`
  (bản Win32: cài đặt "Wpf" đã lưu vẫn dùng được và không bị ghi lại); khi ánh xạ thì yêu cầu `WicDirect` không bọc `FallbackImageDecoder` tới chính nó;
  không có cả ba = `ArgumentException` lúc dựng (lỗi ghép nối).
- **Fine-scale = WIC Fant** (quyết định đã chốt (b), WP-05): `WicPixelScaler` (internal ở Imaging, IVT cho TurboJpeg) = `CreateBitmapFromMemory` + `IWICBitmapScaler`
  Fant -> `PixelBuffer`; `TurboJpegDecoder` dùng nó thay `PixelAreaResampler`. **Byte-exact với `TransformedBitmap(ScaleTransform)` cũ** (test
  `Resize_EqualsWpfScaleTransform`). E_OUTOFMEMORY của WIC -> `InsufficientMemoryException` (cùng phán quyết "quá lớn cho bộ nhớ"). `PixelAreaResampler` còn nguyên,
  chỉ test dùng (oracle thuần managed cho `PixelAreaResamplerTests`); có thể xoá khi lead muốn - KHÔNG xoá trong PR này.
- Nối app: `App.csproj` + `Benchmark.Cli` tham chiếu Imaging.Wpf; `App.xaml.cs`/`DecoderProviders`/`ServiceFactories` truyền `WpfBitmapSourceCodec.Instance`;
  `BenchmarkWindow` và CLI giữ decoder `WpfBitmapImageDecoder` tường minh (benchmark vẫn đo đúng backend WPF như trước); `tools/verify-release.ps1` yêu cầu
  `PhotoReview.Imaging.Wpf.dll`; `docs/architecture.md` cập nhật.
- Kiểu trả về đổi (hành vi nhìn thấy: không): `TurboJpegDecoder`/`LibRawDecoder` trả `DecodedImage` (PlatformImage = BitmapSource đã Freeze từ codec) thay vì
  `WpfDecodedImage`; không nơi nào trong App kiểm kiểu `WpfDecodedImage` (grep).
- Luật kiến trúc (`Architecture.Tests`): Rule 2/7/9/10 mở rộng (cấm System.Windows/PresentationCore/WindowsBase/Imaging.Wpf); **Rule 3** (K-1) quét MỌI namespace của 4 assembly Imaging
  (không chỉ Caching/Preload) và cấm mọi kiểu `System.Windows*`; **Rule 11** (assembly không tham chiếu assembly WPF nào + csproj không có `UseWPF`);
  **Rule 12** (Imaging.Wpf là assembly Imaging duy nhất dùng WPF, chỉ phụ thuộc Core/Imaging).

## 2. Chưa làm / để lead biết

1. **Perf gate đợt 1 chưa đủ**: chỉ có decode bench (mục 3). Còn `tune-matrix.ps1` S2/S3/S4, probe ảnh đầu (R2R; thêm nạp một assembly `Imaging.Wpf.dll`), peak working
   set F4 và đếm copy qua `ReviewMetrics`. Máy bận (nhiều agent) nên lead cần đo trên máy yên tĩnh khi đóng đợt 1; chưa tạo file `<ngày>-nowpf-wave1`.
2. Kiểu WPF của decoder đổi namespace `PhotoReview.Imaging.Decoding` -> `PhotoReview.Imaging.Wpf` (test/App dùng global using); `ExifOrientation` (Imaging) giờ thuần, không `partial`.
3. Việc dọn tiếp (không chặn): xoá `PixelAreaResampler` + `PixelAreaResamplerTests`; đổi tên test lịch sử nhắc "legacy parameterless" trong comment (đã sửa ở
   `TurboJpegPixelBufferParityTests`).
4. `wpfDecoderFactory` đặt cuối danh sách tham số của `ImageDecoderFactory` (sau `decoderDecorator`), đúng tên như thẻ.

## 3. Số đo (chi tiết ở perf)

`Benchmark.Cli --decoder-bench` 24 JPEG 17-19 MP -> 1920, 6 lượt xen kẽ base/new (median): Wpf P50 94,3 -> 94,5 ms (+0,1 %), WicDirect 146,5 -> 147,2 (+0,5 %),
TurboJpeg (fine-scale WIC Fant) 226,6 -> 227,4 (+0,4 %); P95 -0,5 / -1,3 / 0,0 %. Không hồi quy > 5 %. Cache đĩa JPEG/PNG: đường mã không đổi (codec gộp giữ nguyên
ToPixels/FromPixels), `PreviewCacheCrossVersionTests` (fixture byte-compatible) xanh.

## 4. Kiểm chứng

- Build Release toàn `PhotoReview.slnx`: 0 warning / 0 error. Test: Architecture 102, Core 3154, Shell 228, Imaging 4161, App 2385 (gồm Category=UI, runner hidden desktop), Integration 1139: xanh.
- Test sửa: ~50 file test đổi chữ ký (thêm codec, `WpfExifOrientation`, `wpfDecoderFactory`); `TurboJpegPixelBufferParityTests` không còn "đường legacy" nên so với
  WPF `TransformedBitmap` trên CÙNG điểm ảnh TurboJPEG (byte-exact) và, cho fine-scale, với decode WPF (ngưỡng nới MAE <= 5 / PSNR >= 25 dB; đo: tệ nhất MAE 3,8 / PSNR 26,8 trên
  fixture checkerboard cạnh sắc cực đoan, khác bộ giải mã JPEG nên không còn so được với khung cũ 30,6 dB). Test mới: `WicPixelScalerTests` (7),
  `Wp06RequiredCodecTests`, `Wp06DecoderFactoryWpfSlotTests` (gồm `ImageDecoderFactory_WithoutWpf_MapsWpfToWicDirect_AndKeepsSetting`), `Wp06WpfFreeExceptionMappingTests`.
- **Mutation thủ công, 25 đột biến chọn lọc** (sonnet; worktree riêng): 18 bị diệt ngay (Fant->NearestNeighbor, layout đích, bỏ fine-scale/orientation TurboJpeg,
  null codec TurboJpeg, ánh xạ Wpf->WicDirect, `wpfDecoderFactory`, throw lỗi ghép nối, decoder mặc định WicDirect với codec tiêm, `FormatException` fallback/cache-entry,
  ToPixels alpha, FromPixels dispose, thứ tự truy vấn orientation, IsFullyOpaque, `ReportSourceReleased` LibRaw, `using System.Windows.Media` trong Imaging -> build đỏ,
  `UseWPF` trong Imaging.csproj -> Rule 11 đỏ). 7 sống sót và xử lý:
  - M13 (null codec khi truyền decoder tường minh) và M15 (ThumbnailCache phải đưa codec TIÊM vào `EmbeddedThumbnailReader`): **lấp bằng test mới**
    (`PreviewImageService_NullCodec_WithExplicitDecoder_Throws`, `ThumbnailCache_EmbeddedThumbnail_UsesTheInjectedCodec`).
  - M2 (nhánh cùng kích thước của `WicPixelScaler` bị tắt): tương đương về kết quả (Fant cùng kích thước ra y hệt bản chép), chỉ khác đường chạy.
  - M3 (bỏ `ObjectDisposedException.ThrowIf` ở `WicPixelScaler`): tương đương thực tế - `source.Stride/Address` của buffer đã Dispose tự ném `ObjectDisposedException`
    (test `Resize_InvalidArguments_Throw_AndLeakNothing` vẫn ghim hành vi).
  - M4, M20 (giải phóng buffer đích khi WIC/CopyPixels ném giữa chừng ở `WicPixelScaler`, `WpfBitmapSourceCodec.ToPixels`) và M6 (nhánh E_OUTOFMEMORY -> `InsufficientMemoryException`
    của `WicPixelScaler`): **không kiểm được** nếu không có điểm tiêm lỗi vào WIC/`BitmapSource` (kiểu WPF không thể giả); nhánh giống hệt nhánh đã có test ở
    `WicCacheImageReader` (WP-04). Ghi nhận, không thêm seam production chỉ để kiểm.
  - Một test chập chờn đã biết xuất hiện ngẫu nhiên khi chạy đột biến (`PreviewImageServiceMutationTests` ~dòng 342, thread above-normal): xanh khi chạy lại, không do đột biến.
