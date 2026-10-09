---
id: Q-FMT-WEBP-HEIC
order: 141
summary: |-
  WebP + HEIC/HEIF supported through the Windows codecs (WIC), no bundled libheif/libde265 (reverses Q-R52 for these two formats only, user request 2026-10-09); setting WebpHeicSupportEnabled (default on); a missing Store codec is a localized per-file error with install guidance, logged once; animated WebP shows frame 1; JXL/AVIF/PSD stay deferred.
---

# Q-FMT-WEBP-HEIC — WebP và HEIC/HEIF qua codec của Windows (2026-10-09)

**Bối cảnh.** Người dùng không xem được ảnh HEIC (iPhone) và muốn xem WebP (ảnh tải từ web). [Q-R52](Q-R41-Q-R52-user-feedback.md)
đã DECLINED "định dạng mới" cho tới khi có quyết định kiến trúc riêng (thư viện, giấy phép, kế hoạch test). Ngày 2026-10-09
người dùng yêu cầu lại; quyết định này đảo Q-R52 **chỉ cho WebP và HEIC/HEIF**. JXL, AVIF, PSD vẫn để sau.

## Quyết định

| Mục | Nội dung |
|---|---|
| Đường giải mã | Windows Imaging Component (WIC) có sẵn: WebP qua "Microsoft Webp Decoder" (Windows 11 / gói Store "WebP Image Extensions"); HEIC/HEIF qua "HEIF Image Extensions" + "HEVC Video Extensions" (Store). Luôn đi `WicDirectDecoder` (+ fallback WPF như mọi backend), bất kể backend người dùng chọn (`WebpHeicRoutingDecoder`). |
| Không đóng gói | Không libheif, libde265, libwebp hay DLL native mới; `THIRD-PARTY-NOTICES.md` không đổi. |
| Danh sách đuôi | `.webp`, `.heic`, `.heif` (`ImageFileTypes.WebpHeicExtensions`); `.hif`/`.avif`/`.heics`/`.jxl` không được liệt kê dù codec HEIF/JXL của Windows nhận chúng. |
| Setting | `WebpHeicSupportEnabled`, mặc định BẬT (cả config cũ không có trường này), Cài đặt > "WebP và HEIC/HEIF"; đổi giá trị thì nạp lại thư mục đang mở (giống `RawSupportEnabled`). Cửa sổ Cài đặt hiện dòng trạng thái codec của máy. |
| Probe lúc chạy | `WicCodecAvailability`: liệt kê decoder WIC (`CreateComponentEnumerator`, thấy cả codec gói Store) + hỏi Media Foundation có decoder HEVC (`MFTEnumEx`). Chạy một lần/tiến trình, lười (lần đầu gặp đường dẫn WebP/HEIC hoặc mở Cài đặt) — không chạm đường khởi động. Cài codec khi app đang chạy: cần khởi động lại. |
| Máy thiếu codec | File VẪN được liệt kê (người dùng thấy ảnh iPhone của mình và được bảo cần cài gì). Khi hiển thị: lỗi `MissingImageCodecException` (một `NotSupportedException`) có câu hướng dẫn đã dịch (en/vi) "cài ... từ Microsoft Store rồi khởi động lại". Bị từ chối trước mọi lần đọc đĩa; preload không prefetch byte của file đó, không ghi lỗi từng file (router ghi log một lần cho mỗi định dạng). Các ảnh khác duyệt bình thường. |
| Setting tắt | File không được liệt kê; nếu vẫn tới decoder (kéo thả cũ, Undo) thì lỗi đã dịch "WebP/HEIC đang tắt". Undo khôi phục file WebP/HEIC bất kể setting (tránh trông như Undo hỏng). |
| Hướng ảnh / alpha / động | Hướng EXIF: chuỗi đọc metadata sẵn có của WicDirect (`System.Photo.Orientation`). Alpha: WebP trong suốt ra Pbgra32 như PNG; cache đĩa JPEG từ chối ảnh có pixel trong suốt (IMG-01/Q-R7), ảnh đục được cache bình thường. WebP động: chỉ khung đầu. `ImageCacheKey` và định dạng `PreviewCacheFile` không đổi. |

## Vì sao WIC, không libheif

- **Pháp lý/bằng sáng chế:** HEIC dùng HEVC (H.265), có nhiều nhóm bằng sáng chế (MPEG LA/Access Advance/Velos). Tự phân phối
  libde265 (LGPL) + libheif (LGPL) trong bản build Windows kéo theo rủi ro bản quyền sáng chế HEVC cho người phân phối và nghĩa vụ
  LGPL (cho phép thay DLL, kèm notice). Codec của Microsoft được người dùng tự cài từ Store, giấy phép HEVC đi kèm gói đó.
- **Kích thước/bảo trì:** không thêm DLL native, không pin SHA/fetch script, không cập nhật bảo mật cho parser HEIF của bên thứ ba.
- **Nhất quán:** ADR 0001 đã chọn WIC làm backend mặc định; WebP/HEIC đi cùng pipeline (ICC→sRGB, xoay EXIF, pre-scale, premultiplied alpha).

## Hạn chế (đã chấp nhận)

- Phụ thuộc máy người dùng: HEIC cần **cả** "HEIF Image Extensions" **và** "HEVC Video Extensions" (gói HEVC có thể mất phí;
  một số máy có bản "from Device Manufacturer" miễn phí). WebP có sẵn trên Windows 11; Windows 10 cần gói "WebP Image Extensions".
- Máy dev (2026-10-09): WebP decoder **có**; HEIF decoder **có**; decoder HEVC **không** (MFTEnumEx thấy AV1/H.264 nên việc
  dò gói Store hoạt động). Vì vậy giải mã HEIC thật và hướng xoay HEIC (irot/imir vs EXIF) **chưa kiểm được trên máy này**;
  test `Heic_RealSample_*` (Category=Native) chạy khi có codec + biến `PHOTOREVIEW_HEIC_SAMPLE`. Cần kiểm tra bằng mắt với ảnh
  iPhone dọc sau khi cài HEVC.
- WebP động chỉ hiện khung đầu; không phát hoạt ảnh.
- Thumbnail/benchmark: benchmark (`BenchmarkWindow`) không liệt kê WebP/HEIC; "Open with" (script đăng ký) không đăng ký đuôi mới.

## Để sau

JXL (Windows có "Microsoft JPEG XL Decoder" trên một số bản), AVIF (codec HEIF + AV1 Video Extension), PSD: mỗi định dạng cần
quyết định riêng (danh sách đuôi, kế hoạch test, hành vi khi thiếu codec) theo cùng khuôn mẫu này.
