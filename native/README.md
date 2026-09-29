# Native Binaries

Thư mục này chứa các thư viện native unmanaged được nạp runtime bởi PhotoReview.

## `native/x64/libraw.dll`

- **Tên thư viện:** LibRaw
- **Phiên bản:** 0.22.2 (official Windows x64 release, compiled by MSVC 2022)
- **Nguồn tải:** `https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip`
- **Kiến trúc:** Windows x64 (AMD64)
- **Kích thước:** 1,153,024 bytes
- **SHA-256:** `6A459C22039ABF0EAC4D263673337C8ED5F223ACBD372FCF77610DEBF80AC8CD`
- **Giấy phép:** LGPL-2.1-only hoặc CDDL-1.0; bản quyền và điều khoản đầy đủ nằm ở `native/libraw/LICENSE.LGPL` và `native/libraw/LICENSE.CDDL`.
- **Mã nguồn:** gói tải kèm source; upstream repository: `https://github.com/LibRaw/LibRaw`.
- **Tải/kiểm tra:** `tools/fetch-libraw.ps1` xác minh hash DLL trước khi lưu vào `native/x64/`.

## `native/x64/turbojpeg.dll`

- **Tên thư viện:** libjpeg-turbo (TurboJPEG 3 API)
- **Phiên bản:** 3.0.0
- **Kiến trúc:** Windows x64 (AMD64)
- **Kích thước:** 3,038,208 bytes
- **SHA-256:** `E9BDEC69FA2008EAF557CB68BD483E62A350B740A1588497D14AC157A0DD583D`
- **Dependencies:** Statically linked CRT (chỉ phụ thuộc `KERNEL32.dll`)
- **Giấy phép:** BSD-3-Clause / Independent JPEG Group (IJG) / zlib (xem `THIRD-PARTY-NOTICES.md`)
- **Mục đích:** Backend giải mã ảnh JPEG tốc độ cao (SIMD-accelerated) cho `PhotoReview.Imaging.TurboJpeg`.
