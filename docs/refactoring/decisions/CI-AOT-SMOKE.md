---
id: CI-AOT-SMOKE
order: 1360
summary: |-
  CI publish-aot chạy thật PhotoReview.exe AOT hai lần (--smoke và --smoke <jpg> sinh tại chỗ) để bắt lỗi cắt gọt/ILC; thêm <1 s (CHƯA XÁC NHẬN, 2026-10-14).
---

# CI-AOT-SMOKE - smoke chạy exe AOT thật trong job publish-aot (2026-10-14)

Trạng thái: **CHƯA XÁC NHẬN** (đổi quy trình CI). Hoàn tác: revert PR (`tools/verify-release.ps1`, comment trong `ci.yml`,
`TestFilterDriftTests.cs`). Nguồn: [NOWPF-WP36-PUBLISH-AOT](NOWPF-WP36-PUBLISH-AOT.md), [CI-OPTIMIZE-ROUND2](CI-OPTIMIZE-ROUND2.md).

## Hiện trạng tìm thấy

`verify-release.ps1 -AotOnly -Smoke` ĐÃ chạy thật `PhotoReview.exe --smoke` của bản AOT (bỏ mọi DOTNET_* và dotnet khỏi PATH),
nhưng chỉ phủ cửa sổ + khung đầu. Shell Win32 chỉ có cờ `--smoke` (không có cờ probe-native/startup-probe), nên không có
đường "cửa sổ phụ" an toàn để phủ thêm.

## Đổi

- `-Smoke` chạy thêm `PhotoReview.exe --smoke <ảnh.jpg>`: JPEG 320x200 sinh bằng System.Drawing vào thư mục tạm; smoke chỉ thoát 0
  sau KHUNG ẢNH đầu, nghĩa là đường decode đã biên dịch AOT (TurboJPEG interop, preview pipeline) chạy thật. Quá hạn 30 s thoát 7.
- Mỗi lần chạy in rõ lệnh, pid, mã thoát, thời gian (ms). Không thao tác UI.
- Luật Architecture.Tests (WP-36) thêm: job publish-aot phải chạy verify-release với `-Smoke` và script phải có cả hai lần chạy.
- `publish-aot` vẫn continue-on-error, vẫn KHÔNG nằm trong needs của tag-version.

## Giới hạn

Chỉ phủ đường JPEG (repo không có RAW mẫu cho libraw) và cửa sổ chính; runner không GPU dùng WARP.
