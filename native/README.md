# Native Binaries

Thư mục này chứa các thư viện native unmanaged được nạp runtime bởi PhotoReview.

## `native/x64/turbojpeg.dll`

- **Tên thư viện:** libjpeg-turbo (TurboJPEG 3 API)
- **Phiên bản:** 3.0.0
- **Kiến trúc:** Windows x64 (AMD64)
- **Kích thước:** 3,038,208 bytes
- **SHA-256:** `E9BDEC69FA2008EAF557CB68BD483E62A350B740A1588497D14AC157A0DD583D`
- **Dependencies:** Statically linked CRT (chỉ phụ thuộc `KERNEL32.dll`)
- **Giấy phép:** BSD-3-Clause / Independent JPEG Group (IJG) / zlib (xem `THIRD-PARTY-NOTICES.md`)
- **Mục đích:** Backend giải mã ảnh JPEG tốc độ cao (SIMD-accelerated) cho `PhotoReview.Imaging.TurboJpeg`.
