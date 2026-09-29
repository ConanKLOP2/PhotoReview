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
- **Mã nguồn tương ứng:** archive phát hành chính thức `native/libraw/LibRaw-0.22.2-Win64.zip`; SHA-256 được ghim tại `native/libraw.package.sha256`. Archive này được đóng cùng output phát hành dưới tên `LibRaw-SOURCE.zip`.
- **Thông báo và giấy phép:** output phát hành có `LibRaw-NOTICE.txt`, `LibRaw-LICENSE.LGPL`, và `LibRaw-LICENSE.CDDL`.
- **Tải/kiểm tra:** `tools/fetch-libraw.ps1` xác minh hash của cả DLL và archive mã nguồn trước khi lưu.
- **Kiểm tra khi build:** target `EnsureLibRawBinary` của `PhotoReview.Imaging.LibRaw.csproj` luôn chạy `tools/fetch-libraw.ps1`; script băm `libraw.dll` và archive nguồn theo `native/libraw.sha256` / `native/libraw.package.sha256`. Nếu mọi pin khớp thì không dùng mạng (build offline được); nếu DLL thiếu, cũ hoặc sai thì tải lại bản 0.22.2 chính thức. Package được băm TRƯỚC khi giải nén, các entry bị chặn thoát khỏi thư mục tạm, file được cài qua bản tạm đã xác minh. Một named mutex (`Local\PhotoReview-fetch-libraw-*`) tuần tự hóa các lần chạy đồng thời (cấu hình AnyCPU và x64).
- **Chế độ script:** `-Verify` chỉ kiểm tra (không mạng, không ghi, exit 1 nếu lệch pin); `-PackagePath <zip>` cài từ package cục bộ (offline/test) với cùng các kiểm tra hash.

## `native/x64/turbojpeg.dll`

- **Tên thư viện:** libjpeg-turbo (TurboJPEG 3 API)
- **Phiên bản:** 3.0.0
- **Kiến trúc:** Windows x64 (AMD64)
- **Kích thước:** 3,038,208 bytes
- **SHA-256:** `E9BDEC69FA2008EAF557CB68BD483E62A350B740A1588497D14AC157A0DD583D`
- **Dependencies:** Statically linked CRT (chỉ phụ thuộc `KERNEL32.dll`)
- **Giấy phép:** BSD-3-Clause / Independent JPEG Group (IJG) / zlib (xem `THIRD-PARTY-NOTICES.md`)
- **Mục đích:** Backend giải mã ảnh JPEG tốc độ cao (SIMD-accelerated) cho `PhotoReview.Imaging.TurboJpeg`.
