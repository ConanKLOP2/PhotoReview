---
id: RUST-MIGRATION-EVAL
order: 171
summary: |-
  Đánh giá chuyển PhotoReview sang Rust (2026-10-10): KHÔNG chuyển (chắc chắn cao) - Rust không chắc chắn tốt hơn; duyệt ảnh đã 1,4-2,2 ms P50 và decode dùng chung WIC, chỉ khởi động nguội chậm (1054-1182 ms) và phần lớn do WPF + thiết kế khởi động, đạt được bằng .NET không WPF (sàn đo 181 ms) với chi phí thấp hơn nhiều; người dùng chọn hướng bỏ WPF (NO-WPF-MIGRATION-PLAN).
---

# RUST-MIGRATION-EVAL - có nên viết lại bằng Rust (2026-10-10)

Kế hoạch được chọn sau đánh giá này: [NO-WPF-MIGRATION-PLAN](NO-WPF-MIGRATION-PLAN.md) (bỏ WPF, giữ .NET). File này giữ phần so sánh
ngôn ngữ/nền tảng và lý do **không** viết lại bằng Rust; mọi số đo khởi động nằm ở mục 2 của file kế hoạch.

## 1. Kết luận

**Không chuyển sang Rust (toàn bộ hay một phần). Mức chắc chắn: cao.** Bằng chứng không cho thấy Rust "chắc chắn tốt hơn":

1. **Duyệt ảnh (ưu tiên số 1 của dự án) không còn chỗ để Rust cải thiện.** Chuyển ảnh đã preload: P50 1,39-2,19 ms, P95 2,5-8,3 ms
   ([tieredpgo](../perf/2026-10-07-tieredpgo-confirmation.md)) - dưới một khung 60 Hz. Mọi phương án đều phải chờ cùng vsync.
2. **Decode là mã native của Windows (WIC), không phải C#.** ADR 0001: WIC Direct ~8 ms P50 cho 24 MP -> FHD; TurboJpeg (C, SIMD) qua P/Invoke
   **không** nhanh hơn ([ADR 0001](../../adr/0001-image-decoder.md)). Rust gọi cùng WIC (`windows-rs`) hoặc libjpeg-turbo -> cùng tốc độ.
   `zune-jpeg` (Rust thuần) chưa có bằng chứng nhanh hơn WIC có DCT-scaling ở kích thước màn hình.
3. **Chỗ chậm thật là khởi động nguội** (1054 ms bản publish, 1182 ms bản Explorer đang chạy) và đo hôm nay cho thấy: sàn .NET **không WPF**
   (Win32 + WIC, R2R) là **181 ms**, sàn WPF tối giản 403 ms. Tức là phần lớn khoảng cách đến từ WPF + thiết kế khởi động, **không** từ C#/.NET.
   Phần .NET còn lại (vào `main` ở ~65 ms) thì Native AOT bớt được (ước 40-55 ms, chưa đo).
4. **Chi phí:** viết lại ~53 nghìn dòng C# sản phẩm + công cụ và ~139 nghìn dòng test (9.341 test chạy trong CI) - ước **45-80 người-tuần**
   (mục 4), với rủi ro dữ liệu người dùng ở journal/Recycle Bin/undo đã được kiểm bằng fault-injection và mutation (81-97 %).
5. **Không có điều kiện đầu vào nào của Rust (an toàn bộ nhớ, không GC) đang là vấn đề đo được:** GC < 2 % ([device-tuning](../perf/2026-10-07-device-tuning-results.md)),
   không có lỗi bộ nhớ trong lịch sử ngoài phần native (LibRaw/TurboJpeg - vẫn là C dù gọi từ Rust).

**Điều có thể làm đổi kết luận:** (a) cần chạy đa nền tảng (Linux/macOS) - khi đó WIC mất, Rust/Avalonia/Skia đều phải được đánh giá lại;
(b) sau khi bỏ WPF + Native AOT, ảnh đầu vẫn > 400 ms và hồ sơ cho thấy runtime .NET (không phải WIC/I-O) chiếm > 100 ms; (c) chủ dự án muốn
duy trì bằng Rust vì lý do khác tốc độ.

## 2. So sánh nền tảng (rút gọn)

Số "ảnh đầu" là **sàn** (viewer tối giản, ms từ CreateProcess), không phải cả app; cả app cần cộng phần đọc settings, catalog, DI, overlay.

| Tiêu chí | C thuần (Win32 + WIC) | C++ (Win32/D2D, WIL/WRL) | Rust (windows-rs + D2D/WIC) | .NET không WPF + Native AOT | Ở lại WPF (tối ưu) |
|---|---|---|---|---|---|
| Sàn ảnh đầu | ước 110-140 (= sàn .NET đo 181 trừ phần vào `main` ~65 + hello-world native ~10-20) | như C | như C | đo 181 (R2R); AOT ước 120-150 | đo 403 |
| Cả app, ước | 250-330 | 250-330 | 250-330 | 250-350 (AOT) / 300-400 (R2R) | 500-600 (sau P-1) |
| Chuyển ảnh preload | không đổi (vsync + WIC) | không đổi | không đổi | không đổi | hiện 1,4-2,2 ms P50 |
| Decode | WIC (chung) | WIC (chung) | WIC qua windows-rs, hoặc zune-jpeg / libjpeg-turbo | WIC (chung) | WIC (chung) |
| RAW / HEIC | LibRaw (C) trực tiếp; HEIC qua WIC | như C | LibRaw qua FFI (`rawloader` thiếu nhiều body, không thay được); HEIC qua WIC (libheif = LGPL + bằng sáng chế HEVC, xem [Q-FMT-WEBP-HEIC](Q-FMT-WEBP-HEIC.md)) | như hiện tại | như hiện tại |
| Dùng lại mã hiện có | 0 % | 0 % | 0 % (có thể FFI, xem mục 4C) | Core/Imaging/Platform/ViewModels gần như 100 %; View viết lại | 100 % |
| Dùng lại 9.341 test | 0 (viết lại, chưa có khung test sẵn cho C) | 0 (GoogleTest/Catch2) | 0 (`cargo test`) | Core 2918 + Imaging 2992 + phần lớn App giữ | 100 % |
| Mutation testing | gần như không có công cụ dùng được | Mull (hạn chế) | cargo-mutants (tốt) | Stryker.NET (đang dùng) | Stryker.NET |
| An toàn bộ nhớ | thấp | trung bình (RAII) | cao | cao (managed) | cao |
| Công tới ngang tính năng (người-tuần) | 70-110 | 60-95 | 45-80 | 21-33 (xem kế hoạch) | 1-3 |
| Năng lực người duy trì + AI agent | thấp nhất (dễ lỗi) | trung bình | trung bình (học Rust + windows-rs; COM trong Rust dài dòng) | cao (đang dùng) | cao |
| Kích thước | < 2 MB | 2-5 MB | 3-8 MB | 5-15 MB (ước) | runtime .NET + ~15 MB |

**Đọc:** về tốc độ ảnh đầu, C/C++/Rust và .NET AOT chênh nhau ước **≤ 30-50 ms** (phần khởi tạo runtime); khác biệt chính so với hiện tại
(~700-850 ms) đến từ **bỏ WPF và sửa thiết kế khởi động**, việc mà .NET làm được với công ít hơn 2-4 lần và giữ được bộ test.

## 3. Kiểm kê mã nguồn và tính năng (đếm 2026-10-10, origin/master `f3f62728`)

| Project | File .cs | Dòng .cs | Kiểu (class/struct/record/interface/enum) | Ghi chú |
|---|---:|---:|---:|---|
| PhotoReview.App (WPF) | 96 (+14 XAML, 1480 dòng) | 15.563 | 161 | View, ViewModels (0 file dính WPF), Coordinators, Input, Services |
| PhotoReview.Core | 126 | 12.668 | 196 | catalog, settings, session, journal, file actions, i18n (en/vi JSON ~970 khoá) |
| PhotoReview.Imaging | 50 | 7.227 | 90 | decode WIC/WPF, cache RAM/đĩa, preload |
| PhotoReview.Imaging.Raw | 28 | 3.912 | 36 | 8 định dạng RAW, preview nhúng |
| PhotoReview.Imaging.LibRaw | 7 | 1.310 | 19 | P/Invoke libraw.dll |
| PhotoReview.Imaging.TurboJpeg | 5 | 890 | 8 | P/Invoke turbojpeg.dll |
| PhotoReview.Platform.Windows | 18 | 2.838 | 50 | Explorer order (COM), Recycle Bin, instance, DWM |
| PhotoReview.Localization.Generator | 3 | 840 | 8 | source generator |
| PhotoReview.Benchmarking / PerfAnalysis | 9 / 7 | 1.097 / 1.389 | 27 / 22 | |
| tools/PhotoReview.Benchmark.Cli | 18 | 4.324 | 37 | perf session, harness |
| tools/*.ps1 + tools/diag | - | 3.488 + 2.106 | - | CI/docs/perf scripts (giữ nguyên với mọi phương án) |
| **Tổng sản phẩm + công cụ C#** | **367** | **~52.100** | | |

| Test project | File | Dòng | `[Fact]/[Theory]` | Test chạy trong CI 2026-10-10 |
|---|---:|---:|---:|---:|
| Core.Tests | 231 | 37.061 | 1.697 | 2.918 |
| Imaging.Tests | 250 | 39.576 | 1.781 | 2.992 |
| App.Tests | 180 | 37.994 | 1.610 | 2.238 |
| Integration.Tests | 109 | 21.062 | 755 | 1.113 + 15 Slow |
| Architecture.Tests | 20 | 2.421 | 71 | 65 |
| **Tổng** | **790** | **~138.000** | **5.914** | **9.341** |

Tính năng hiện có và độ khó port **sang Rust** (để so; với hướng bỏ WPF xem cột cuối):

| Tính năng | Nằm ở | Rust: độ khó / rủi ro | Bỏ WPF (.NET) |
|---|---|---|---|
| Catalog, sort Default/Name/Explorer order (COM `IFolderView`, snapshot, INV-7/9) | Core, Platform | cao / cao (COM shell trong Rust, race đã sửa nhiều vòng) | giữ |
| Preload cửa sổ 32/8 + cả thư mục, RAM budget, áp lực bộ nhớ, ưu tiên I/O (Q-R29) | Imaging | cao / trung bình | giữ |
| Cache RAM LRU + đĩa (preview JPEG, thumbnail), `ImageCacheKey`, INV-1/2 | Imaging | trung bình / cao (định dạng cache phải tương thích) | giữ (encoder WIC thay WPF) |
| Decode WIC Direct + ICC + EXIF orientation, fallback (INV-12), TurboJpeg | Imaging | trung bình / trung bình | tách khỏi `BitmapSource` |
| RAW 8 định dạng, preview nhúng, LibRaw on-zoom, cặp JPG+RAW (ADR 0009) | Imaging.Raw/LibRaw | cao / cao (parser container ~3.900 dòng + corpus) | giữ |
| WebP/HEIC qua WIC, probe codec | Imaging | thấp / thấp | giữ |
| Journal Prepared/Committed, undo, Recovery, Recycle Bin, ADR 0007, fault-injection | Core | **rất cao / rất cao (dữ liệu người dùng)** | giữ |
| File actions + action profiles, copy/move/delete nhóm, duplicate cleanup | Core | cao / cao | giữ |
| Settings (~60), export/import, i18n en+vi, source generator | Core | trung bình / thấp | giữ, UI Settings viết lại ở P5 |
| Menu chuột phải tuỳ biến, phím tắt tuỳ biến | App | trung bình / thấp | viết lại (P3) |
| Shell integration (liên kết .jpg, "Browse with PhotoReview") | Platform/App | thấp / thấp | giữ |
| Zoom theo pixel nguồn (ADR 0008), Fit/FitWidth 1-2/FitHeight, T89 | App | cao / cao | viết lại (P3), port test trước |
| Pan, kinetic (Q-R40), touchpad 2 ngón, click-zoom, chuột giữa | App/Input | cao / trung bình | logic thuần giữ, input viết lại |
| Crossfade, overlay EXIF, toolbar tự ẩn, HUD | App | trung bình / thấp | viết lại (DirectWrite) |
| Multi-monitor, F11, placement trước khi hiện, cloak (P-STARTUP) | App | trung bình / trung bình | viết lại (logic dùng lại) |
| Single-instance + forward (pipe, SEC-02), PerFolder | Platform/Core | trung bình / trung bình | giữ |
| Compare, batch review, hash, duplicate | App/Core | trung bình | View viết lại |
| Update check thủ công (GitHub API) | Core | thấp | giữ |
| Perf harness, EventSource trace, Benchmark.Cli, tune-matrix | Core/tools | trung bình | host mới (P3) |

## 4. Phương án Rust đã xét (giữ ngắn)

- **C. Rust một phần.** (C1) launcher Rust hiện ảnh đầu rồi giao cho app .NET: hai tiến trình, hai cửa sổ (hoặc chuyển HWND), cách xử lý
  input/ trạng thái trong lúc .NET khởi động rất phức tạp, và lợi ích bị chặn bởi việc app .NET vẫn phải lên (~1 s) trước khi người dùng
  thao tác được - **loại**. (C2) lõi decode/cache bằng Rust qua FFI: decode đã là WIC native, cache đã đạt P50 ~2 ms - **không có gì để thắng**,
  thêm ranh giới FFI và hai hệ test - **loại**.
- **D. Viết lại toàn bộ bằng Rust.** Stack hợp lý nếu phải làm: `windows-rs` (Win32, COM, WIC, D2D, DirectWrite, Shell), cửa sổ tự quản lý
  (không `winit` - cần Per-Monitor V2, cloak, placement chi tiết), Direct2D/D3D11 (wgpu không cần thiết cho ảnh 2D), WIC cho JPEG/PNG/WebP/HEIC
  (tránh libheif vì LGPL + bằng sáng chế HEVC), LibRaw qua FFI (`rawloader` không đủ body), `serde`/`serde_json` cho config/journal (giữ đúng
  định dạng), i18n tự viết đọc cùng JSON catalog, `cargo test` + `cargo-mutants`, `cargo-wix`/zip. Rủi ro trưởng thành: COM trong Rust dài
  dòng, không có UI toolkit native trưởng thành cho Settings (sẽ là Win32 tự dựng hoặc egui/Slint với dark mode khác hẳn). Ước 45-80 người-tuần
  (gồm ~10-15 tuần viết lại test có giá trị tương đương 9.341 test). **Loại** vì lợi ích ≤ 30-50 ms so với .NET AOT (mục 2).
- **E. C++ native:** cùng tốc độ với Rust, công cao hơn (60-95 người-tuần), an toàn bộ nhớ kém hơn. **Loại.**

## 5. "Chắc chắn tốt hơn?" - tiêu chí và cách kiểm chứng

| Tiêu chí đo được | Hiện tại | Rust (ước) | .NET không WPF (ước) | Rust chắc chắn tốt hơn? |
|---|---|---|---|---|
| Ảnh đầu nguội (median) | 1054 / 1182 ms (đo) | 250-330 | 250-350 (AOT) | **Không** - ngang .NET AOT trong sai số ước |
| Ảnh đầu ấm (cache đĩa) | ~1425-1529 ms lô warm cũ ([startup](../perf/2026-10-10-startup-first-image.md)) | như nguội | như nguội | Không |
| Chuyển ảnh P50/P95 | 1,4-2,2 / 2,5-8,3 ms | không đổi | không đổi | **Không** |
| RAM | 14,3 GB peak (pixel ảnh) | không đổi đáng kể | không đổi đáng kể | Không |
| Kích thước | runtime + ~15 MB | 3-8 MB | 5-15 MB | Có (không quan trọng với người dùng này) |
| Ổn định / dữ liệu | journal + fault-injection + mutation 81-97 % | phải xây lại | giữ | **Không** (rủi ro cao hơn) |
| Công bảo trì | 1 ngôn ngữ, 9.341 test | viết lại tất cả | giữ Core/Imaging | Không |

**Rust không cải thiện:** I/O NAS/ổ chậm (giới hạn đĩa/mạng), decode WIC (cùng codec), codec HEIC/WebP của Store, vsync/compositor DWM,
Explorer COM (cross-process ~60 ms), Recycle Bin shell.

**Nếu vẫn muốn kiểm chứng Rust** (không khuyến nghị lúc này): spike R1 - viewer Rust tối giản (windows-rs + WIC + D2D) mở cùng file F4, cùng
probe; GO chỉ khi nhanh hơn spike S1 .NET AOT ([kế hoạch](NO-WPF-MIGRATION-PLAN.md) mục 5) **≥ 100 ms** median. 3-5 ngày, cần cho phép cài:
**rustup + toolchain `stable-x86_64-pc-windows-msvc`** (rustup.rs, ~250-400 MB) **và** MSVC build tools + Windows SDK (cùng thứ spike S1 cần,
~4-5 GB), cùng crate `windows` từ crates.io (~100-200 MB cache). Dự đoán trước: không đạt 100 ms vì phần chênh lệch có thể có chỉ là khởi tạo
runtime (~40-55 ms).

## 6. Quyết định

**RS-1 - Hướng nền tảng.** Người dùng đã chọn "bỏ WPF trước" (2026-10-10). Phương án đã xét: (a) Rust toàn bộ - loại (mục 2, 4D);
(b) Rust một phần - loại (mục 4C); (c) C/C++ - loại (mục 2, 4E); (d) .NET không WPF - **chọn**, chi tiết và các quyết định NW-1..NW-5 ở
[NO-WPF-MIGRATION-PLAN](NO-WPF-MIGRATION-PLAN.md); (e) chỉ tối ưu WPF - giữ làm bước P-1 của kế hoạch. Spike R1 chỉ mở lại khi một điều kiện
ở cuối mục 1 xảy ra.
