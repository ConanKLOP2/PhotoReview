---
id: NOWPF-WP12
order: 657
summary: |-
  WP-12 (đợt 2, 2026-10-11): WIC (decode/encode/probe codec) chuyển từ ComImport/RCW sang [GeneratedComInterface] + LibraryImport + [GeneratedComClass] (IStream); hành vi không đổi, pixel/byte-exact giữ nguyên, decode/cache bench không hồi quy (P50 +-2 %). Ref COM trả ngay bằng WicCom.Release (ComObject.FinalRelease) thay ReleaseComObject; test cân bằng wrapper theo luồng. Mutation 25/25 bị diệt (4 sống sót/nhầm đã lấp).
---

# NOWPF-WP12 - WIC sang `[GeneratedComInterface]` (2026-10-11)

Thẻ: [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md) mục WP-12; tiền đề WP-03/04/05/06. Mẫu COM cho WP-13b (đã có) và bước bật analyzer AOT (WP-34).
Số đo: [perf/2026-10-11-wp12-wic-generated-com.md](../perf/2026-10-11-wp12-wic-generated-com.md).

## 1. Đã làm (toàn thẻ, một PR; chỉ đụng `src/PhotoReview.Imaging` + test Imaging)

- **`Decoding/Wic/WicInterop.cs`**: mọi interface WIC là `[GeneratedComInterface]` (thứ tự vtable theo `wincodec.h`, số slot ở comment): `IWICBitmapSource`
  (+ `IWICBitmapFrameDecode`, `IWICColorTransform`, `IWICFormatConverter`, `IWICBitmapScaler`, `IWICBitmapFlipRotator` KẾ THỪA nó - hết nhân bản 5 method
  và hết ép kiểu trung gian), `IWICBitmapSourceTransform`, `IWICColorContext`, `IWICBitmapDecoder`, `IWICMetadataQueryReader`, `IWICStream`,
  `IWICImagingFactory`; mới `IComStream` (COM `IStream`, thay `System.Runtime.InteropServices.ComTypes.IStream` - kiểu ComImport không dùng được với
  ComWrappers) và `StatStgNative`. `ManagedIStream` là `[GeneratedComClass]` (CCW do `StrategyBasedComWrappers`); `Read/Write` nhận `byte*` và đọc/ghi
  thẳng bằng `Span<byte>` (bỏ bản chép qua `byte[]`). `WICCreateImagingFactory_Proxy` là `[LibraryImport]`.
- **Encode (`Caching/WicImageEncoder.cs`)**: `IWicBitmapEncoder`, `IWicBitmapFrameEncode`, `IWicPropertyBag2` cùng kiểu; **Probe codec**
  (`WicCodecAvailability`): `IEnumUnknown`, `IWICBitmapCodecInfo` ra khỏi lớp (đã `internal`), `MFTEnumEx` là `LibraryImport`, vòng liệt kê tách ra
  `CollectDecoders` để kiểm được. `WicExifReader.PropVariantClear` là `LibraryImport`. Không còn `ComImport`/`DllImport`/`ReleaseComObject`/
  `GetObjectForIUnknown` trong 4 assembly Imaging (grep), trừ LibRaw (P/Invoke native C, ngoài phạm vi).
- **Quy ước con trỏ ra** (như `Shell.Interop/ComInterop`, WP-13b): tham số RA kiểu interface là `out nint`; `WicCom.Wrap<T>` bọc bằng wrapper
  `UniqueInstance` (chiếm 1 ref; ép kiểu sai thì `FinalRelease` ngay), `WicCom.Release` gọi `ComObject.FinalRelease` (thay `Marshal.ReleaseComObject` - vô
  tác dụng với wrapper của ComWrappers). `WicComExtensions` giữ chữ ký cũ `out IFoo` nên chỗ gọi hầu như không đổi. Lý do không dùng `out IFoo`:
  wrapper do marshaller sinh nằm trong cache chung và `FinalRelease` KHÔNG trả ref của nó (WP-13b đã đo), mà decoder WIC giữ bộ nhớ làm việc lớn.
- **`ReadColorContexts`**: mảng `IWICColorContext[]` marshal theo `SizeParamIndex` thành mảng con trỏ thô do người gọi tạo (`CreateColorContext`),
  bọc sau khi `GetColorContexts` thành công; mọi đường lỗi trả đúng các ref còn thô.
- **Hành vi không đổi**: ánh xạ HRESULT (E_INVALIDARG -> `ArgumentException`, E_OUTOFMEMORY, `COMException`) qua `Marshal.ThrowExceptionForHR` như RCW cũ;
  `GetMetadataByName` vẫn `[PreserveSig]`; `ExifIfdRootOf`/`ReadFrameMetadata`/ICC fallback giữ nguyên; thứ tự release giữ nguyên.
- **`WicCom.OutstandingOnThisThread`** (chẩn đoán, theo luồng): số wrapper tạo trừ số đã release; chỉ để test cân bằng, không ảnh hưởng đường chạy.

## 2. Không làm / để lead biết

1. **Analyzer AOT/trim (IL2xxx/IL3xxx) không chạy được trên `net10.0-windows`**: SDK báo `NETSDK1210` (IsAotCompatible/EnableAotAnalyzer không hỗ trợ TFM này),
   nên không có số "0 cảnh báo IL" để ghi. Thay bằng: generator `SYSLIB1050-1099` sạch (build Release 0 warning cả `PhotoReview.slnx`) và grep không còn
   reflection/`ComImport`/`Marshal.GetObjectForIUnknown` trong các file WIC. Bước bật analyzer là WP-34 (cần đa TFM hoặc cờ riêng).
2. `LibRaw` còn `DllImport` (native C, `CharSet.Unicode`): không thuộc WIC, để WP-34.
3. `IWICBitmapCodecInfo` chỉ khai các slot đang dùng đúng thứ tự (slot không gọi là placeholder), không đủ cả `IWICComponentInfo`.

## 3. Kiểm chứng

- Build Release `PhotoReview.slnx`: 0 warning / 0 error. Test: Imaging (trừ 8 test hỏng sẵn từ master: `RawOrientationTests` thiếu corpus và
  `Webp_ThroughTheRouter_...` do WP-06), Core 3184, Shell 341, Architecture (trừ test manual `L-CONTRACT`), App `Category!=UI` 2203 (trừ test manual golden), Integration
  1253 (trừ manual `GoldenRecordTests` và A3 chập chờn dưới tải, xanh khi chạy riêng): xanh. Parity byte-exact (WicDirect pixel parity, `PreviewCacheCrossVersionTests`, WebP) giữ xanh.
- Test sửa: fake WIC của `WicDirectFaultInjectionTests`/`WicDirectInternalsMutationTests` đổi chữ ký (con trỏ thô), `ManagedIStreamTests`/`DecodingHelpersMutationTests`
  gọi `Read/Write` bằng con trỏ, `LegacyWicDirect` khai kiểu `out IFoo` tường minh. Test mới: `WicGeneratedComTests` (12: ref COM thật qua đếm AddRef/Release,
  layout `STATSTG`, ép kiểu = QueryInterface, ranh giới con trỏ của `ReadColorContexts`, `EnumerateDecoders`/`CollectDecoders`) và `WicLifetimeBalanceTests`
  (6: decode đủ hình dạng pipeline, đường lỗi, thumbnail, encode/decode cache, scaler, probe - wrapper tạo = wrapper trả).
- **Mutation thủ công, 25 đột biến chọn lọc** (script, worktree riêng; sau khi loại 8 test hỏng sẵn khỏi bộ lọc): 25/25 bị diệt. Ngay lần đầu 21 bị diệt rõ ràng
  (release/`FinalRelease`, `UniqueInstance`, ref gốc của `Wrap`, đảo slot vtable `IWICBitmapSource`/factory/`IStream`/decoder, `ManagedIStream` Read/Seek/Stat,
  rò con trỏ thô khi lỗi dữ liệu, ánh xạ E_INVALIDARG, `SafeReleaseCom`, QueryInterface của probe, wrapper không trả ở codec info/property bag/query reader/scaler/bitmap/
  thumbnail, `encoder.Commit`, wrapper ép kiểu sai). 4 bị báo "sống sót" ở lần chạy đầu:
  - **M04** (đảo `GetSize`/`GetPixelFormat` của `IWICBitmapSource`) và **M16** (đảo `GetFriendlyName`/`GetContainerFormat` của `IWICBitmapCodecInfo`): script
    nhận nhầm host test chết (`0xC0000005`, vtable lệch ghi đè stack) là "sống sót"; chạy tay thì host sập đúng ở test liên quan. Thêm test để lỗi này có tên và
    không phụ thuộc vào việc host sập: `Cast_IsQueryInterface` (đọc `GetSize` qua `IWICBitmapSource`) và `EnumerateDecoders_ListsTheJpegCodec`
    (codec JPEG in-box, tên + đuôi file). Các test WebP/HEIC cũ tự bỏ qua khi codec vắng nên không thể là chốt chặn.
  - **M11** (rò con trỏ thô ở nhánh lỗi bất ngờ của `ReadColorContexts`) và **M25** (rò ref IUnknown mà `Next` của enumerator trả): sống sót thật vì không có cách
    quan sát ref; **lấp bằng test mới** `ReadColorContexts_UnexpectedFaultInFill_PropagatesAndReleases` và `CollectDecoders_ReadsAndReleasesEveryItem` (CCW giả giữ một ref
    của test, đếm bằng `Marshal.Release`). Chạy lại: cả 4 bị diệt.
  Một test chập chờn đã biết (Integration A3 "forwarded launch ... after the main window closed") xuất hiện dưới tải khi chạy song song, xanh khi chạy riêng: không do thay đổi.