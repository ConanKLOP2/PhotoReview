# AR17 — TurboJpeg: thử nghiệm hay đầu tư

**Finding:** F21 · **Quyết định:** Q-AR8 · **Kích thước:** (a) ~1 giờ (haiku); (b) ~2 ngày + đo (strongest); (c) ~0.5 ngày · **GUI:** (a) một lần nhìn Settings · **Máy thật:** (b) bắt buộc (`--decoder TurboJpeg`, F4)

## Hiện trạng

- `src/PhotoReview.Imaging.TurboJpeg/TurboJpegDecoder.cs:151-152`: `tj3Decompress8` → buffer native → `BitmapSource.Create` (**copy 1**, WPF sở hữu). `:159-178`: nếu cần fine-scale (DCT 1/8 hiếm khi trúng đúng box) hoặc xoay EXIF (ảnh dọc điện thoại — đa số F4) → `TransformedBitmap` + `WpfImageAdapter.Materialize` = `CopyPixels` sang buffer thứ hai + `BitmapSource.Create` (**copy 2–3**). `WicDirectDecoder.cs:104-178` làm scale+rotate+convert trong đồ thị WIC native, `CopyPixels` **một** lần.
- `:67-71`: file có ICC → `throw NotSupportedException` → `FallbackImageDecoder.cs:46-69` bắt và decode lại bằng WPF, **mỗi lần** decode (view, preload, mở lại), không nhớ theo file. Đọc byte đã được truyền lại qua `DecodeFailureSourceBytes` (không đọc đĩa hai lần — tốt).
- Số đo (PERF-STATUS "Not shipped"): TurboJpeg **chậm hơn** WicDirect trên F4; SIMD-only scale factors đo chậm hơn. `DecoderBackend` mặc định WicDirect (Q-R21, ADR 0001). AR01 mới ship TurboJpeg vào release (2026-09-23).

## Q-AR8 — phương án

| | (a) Giữ, đánh dấu "thử nghiệm" | (b) Đầu tư cho ngang WicDirect | (c) Gỡ khỏi Settings và release |
|---|---|---|---|
| Việc | Nhãn "(thử nghiệm — chậm hơn WicDirect trên ảnh lớn)" ở `enum.decoderBackend.turboJpeg` (vi/en); ghi PERF-STATUS; không đổi mã decode | (b1) Fine-scale + xoay bằng `IWICBitmapScaler`/`IWICBitmapFlipRotator` trên buffer BGRX native (tái dùng `WicInterop.cs`), một `CopyPixels`; (b2) ICC: `IImageDecoder` có `bool CanDecode(ReadOnlySpan<byte> header)`/kết quả `DecodeOutcome.Unsupported` thay exception, `FallbackImageDecoder` gọi trước; (b3) đo `--decoder TurboJpeg` vs WicDirect trên F4 (portrait 20–30 MP) và một bộ ảnh nhỏ (≤ 6 MP, DCT 1/2 trúng) | Xoá project reference, `DecoderProviders`, `TurboJpegOption` trong Settings, `fetch-native.ps1` khỏi build, `verify-release.ps1:34-40`; đảo AR01/Q-AR1 |
| Công | 1 giờ | 2 ngày + đo | 0.5 ngày + đảo quyết định AR01 |
| Rủi ro | 0 | Trung bình: `WicInterop` COM lifetime, stride/format; test `Decoding/*` phải mở rộng | Người dùng đã chọn TurboJpeg trong config → `SettingsNormalizer` phải reset về WicDirect (đã có pattern reset enum) |
| Lợi ích kỳ vọng | Người dùng không tự chọn backend chậm | Chỉ có giá trị nếu (b3) cho thấy TurboJpeg thắng ≥ 10 % ở ít nhất một profile; chưa có bằng chứng | Bớt 754 dòng + native DLL + 1 bước release |
| Test | i18n-check | Decoder tests: fine-scale đúng kích thước/orientation 1–8 (mutation: bỏ flip → đỏ); `FallbackImageDecoderTests`: ICC không ném, không ghi `RecordDecoderFallback` (mutation: giữ throw → metric tăng → đỏ) | `DecoderRegistrationTests`, `verify-release` |

**Khuyến nghị: (a).** Không có số đo nào cho thấy TurboJpeg thắng; đầu tư (b) trước khi có (b3) là tối ưu mù. Nếu người dùng muốn thử: làm **(b3) trước** như một lần đo (~1 giờ, `run-matrix.ps1 -Profile quick --decoder TurboJpeg`) rồi mới quyết (b1)/(b2). (c) chỉ khi muốn giảm bề mặt release.

## Nếu chọn (b) — thứ tự

1. (b3) đo baseline TurboJpeg vs WicDirect hiện tại → ghi PERF-STATUS.
2. (b2) ICC không exception (nhỏ, độc lập, cũng bỏ throw/catch cho mọi file ICC khi người dùng chọn TurboJpeg): `IImageDecoder` thêm `DecodeSupport Probe(ReadOnlySpan<byte> head)` mặc định `Supported`; `FallbackImageDecoder.Decode` gọi `_primary.Probe` trước, `Unsupported` → sang fallback không log Warn, đếm metric riêng `DecoderProbeFallback`.
3. (b1) đường WIC native cho fine-scale/xoay; đo lại; chỉ merge nếu ≥ WicDirect ở profile mục tiêu.

## Verification / Acceptance

(a): `i18n-check` xanh; Settings hiện nhãn; PERF-STATUS có dòng "TurboJpeg thử nghiệm (Q-AR8 a)". (b): gate chung + `Decoding` tests + bảng đo trước/sau trong PR. (c): release không còn `turbojpeg.dll`, `DecoderRegistrationTests` cập nhật.
