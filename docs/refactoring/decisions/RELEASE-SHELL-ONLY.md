---
id: RELEASE-SHELL-ONLY
order: 1440
summary: |-
  Gói phát hành GitHub chỉ còn MỘT sản phẩm: shell Win32 PhotoReview.exe (không PhotoReview.App.exe, Imaging.Wpf, App.WpfWindows); publish-release.ps1 -IncludeWpf giữ đường dựng WPF (CHƯA XÁC NHẬN, 2026-10-15).
---

# RELEASE-SHELL-ONLY - release GitHub chỉ có shell Win32 (2026-10-15)

Trạng thái: **CHƯA XÁC NHẬN** (quyết định của người dùng: "trên release github chỉ còn 1 bản mới"; chi tiết triển khai chưa duyệt).
Hoàn tác: revert PR này, hoặc dựng gói hai exe bằng `publish-release.ps1 -IncludeWpf` + `verify-release.ps1 -IncludeWpf`.
Nguồn: [NOWPF-WP30-PACKAGING](NOWPF-WP30-PACKAGING.md) (gói hai exe, bị thay thế phần "hai exe").

## Thay đổi

- `tools/publish-release.ps1`: mặc định chỉ publish `src/PhotoReview.Shell.Win32`. Công tắc mới `-IncludeWpf` (mặc định tắt) publish thêm
  `PhotoReview.App` vào cùng thư mục. `-Aot/-RequireAot/-AotOnly` giữ nguyên nghĩa.
- `tools/verify-release.ps1`: danh sách bắt buộc chỉ gồm shell + thư viện + native + giấy phép + Languages. Các tệp WPF
  (`PhotoReview.App.exe/.dll/.deps.json/.runtimeconfig.json`, `Imaging.Wpf.dll`, `App.WpfWindows.dll`) bị CẤM trừ khi có `-IncludeWpf`.
  Đọc `PhotoReview.runtimeconfig.json` thật: in "Runtime required: ..." và báo lỗi nếu cần WindowsDesktop. Phiên bản/commit kiểm trên `PhotoReview.dll`.
- `.github/workflows/release.yml`: zip chỉ có gói shell; SHA256 chỉ của zip và `PhotoReview.exe`; ghi chú yêu cầu ".NET 10 Runtime
  (Microsoft.NETCore.App 10.x)"; bước zip từ chối nếu thấy tệp WPF. Tên job/check (`release`, `build-test-publish`) không đổi.
- `deploy/install|uninstall-photo-review-association.ps1`: `-Variant` mặc định đổi `Wpf` -> `Shell` (`-Variant Wpf` cho bản WPF tự build).
- Mã WPF (App, App.WpfWindows, Imaging.Wpf) KHÔNG bị xoá, vẫn build và test; gỡ hẳn là WP-37.

## Hệ quả

- Người dùng cũ có liên kết `.jpg` và menu Explorer trỏ tới `PhotoReview.App.exe` sẽ vỡ sau khi cập nhật (tệp biến mất khỏi zip): phải đăng ký lại
  bằng `install-photo-review-association.ps1` (mặc định `Shell`) hoặc nút "Cập nhật đường dẫn" trong Settings của shell.
- Gói nhỏ hơn (không WPF), cần .NET 10 Runtime thường, không cần Windows Desktop Runtime.
- `release.yml` chỉ chạy khi người dùng bấm workflow_dispatch (tag có sẵn, có thể `draft: true` để kiểm tra); bản dựng gói (`publish-release` + `verify-release -Smoke`) chạy ở mọi PR trong job `build-publish` của CI.
