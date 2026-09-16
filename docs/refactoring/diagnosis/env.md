# Môi trường đo hiệu năng (D00)

**Ngày ghi:** 2026-09-16  
**Commit:** aed950b Record T05 approval and T11 confirmation

---

## Thông tin máy

| Thành phần | Giá trị |
|---|---|
| **CPU** | Intel Core i7-9750H @ 2.60 GHz |
| CPU Cores | 6 cores / 12 logical processors |
| **RAM** | 31.85 GB |
| **Ổ chứa repo (C:)** | Kingston SNV2S500G 465.8 GB NVMe SSD |
| **Windows** | Windows 11 Home Single Language · 10.0.26200 (build 26200) · 64-bit |
| **GPU 1** | Intel UHD Graphics 630 · Driver 26.20.100.7323 |
| **GPU 2** | NVIDIA GeForce RTX 2080 with Max-Q Design · Driver 32.0.15.9579 |
| **Màn hình 1** | 1920×1080 · DPI 96 (primary) |
| **Màn hình 2** | 1440×2560 · DPI 96 |
| **Power plan** | Turbo (GUID: 6fecc5ae-f350-48a5-b669-b472cb895ccf) |
| **Defender** | Enabled · Real-time protection: On |
| **.NET SDK** | 10.0.401 · Commit e34a38d2ae |
| **.NET Runtime** | 10.0.12 · Commit 95017c711e |

---

## Công cụ chẩn đoán

| Công cụ | Trạng thái | Version | Đường dẫn / Ghi chú |
|---|---|---|---|
| **dotnet-counters** | Cài | 10.0.745401+cef304c50763bf24f99566cb31d55540842e7ae9 | Global tool (nuget.org) |
| **dotnet-trace** | Cài | 10.0.745401+cef304c50763bf24f99566cb31d55540842e7ae9 | Global tool (nuget.org) |
| **Process Monitor** | Cài | (Windows Sysinternals) | `%USERPROFILE%\..\..\..\MyProjects\PhotoReview\work\tools\procmon\Procmon64.exe` |
| **Process Monitor (ZIP)** | — | — | **Size:** 3199668 bytes · **SHA-256:** 80A6442B46AF762ED1432F6FEC3F7E20366BED62A2522B3486503398A40A1128 |
| **Procmon64.exe (Signature)** | Valid | Microsoft Code Signing PCA 2024 | Issuer: Microsoft Corporation, Redmond, WA |
| **WPR (Windows Performance Recorder)** | Có sẵn | — | `C:\WINDOWS\system32\wpr.exe` (Windows ADK) |
| **WPA (Windows Performance Analyzer)** | Chưa cài | — | Hỏi người dùng khi cần |
| **PresentMon** | Chưa cài | — | Hỏi người dùng khi cần |
| **RAMMap** | Chưa cài | — | Người dùng tự chạy để empty standby list (cần admin) |

---

## Fixture

**CHỜ NGƯỜI DÙNG CUNG CẤP ĐƯỜNG DẪN**

Theo PERF-DIAGNOSIS-PLAN.md mục 5, cần các fixture sau:

- **F1:** 300 JPEG 20–24 MP
- **F2:** 100 JPEG 45–50 MP  
- **F3:** 50 PNG/TIFF
- **F4:** 5.000 JPEG nhỏ
- **F5:** Folder > 16 GB
- **F6:** 20 cặp compare
- **F7:** Ảnh chụp dọc có EXIF orientation

Người dùng sẽ cung cấp đường dẫn root cho từng fixture. Mapping thực tế lưu tại `work/diag/fixtures.local.json` (gitignored, không commit). Nguồn: một thư mục ảnh cục bộ, 60 thư mục con, 1.842 ảnh (1.820 JPEG, 22 PNG), 12,9 GB. Đã chọn: F1 (58 ảnh, 24 MP, nén mạnh), F1b (54 ảnh, 27 MP), F2 (98 ảnh, 48 MP), F3 (22 PNG), F4 (519 ảnh, 4,6 GB). Không có: F5 (> 16 GB), F6 (cặp compare thật) và F7 (EXIF xoay); các nhóm này sinh tạm trong `%TEMP%` khi cần.
