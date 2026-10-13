---
id: NOWPF-AOT-LOCAL-TRIAL
order: 1240
summary: |-
  Chạy thử cục bộ Native AOT so với R2R (cùng commit 0a15b79d): smoke 0, 4 cửa sổ phụ mở thật, ảnh đầu 451 so với 1028 ms (máy bận, tin cậy thấp); đề xuất AOT làm mặc định có điều kiện (CHƯA XÁC NHẬN).
---

# NOWPF-AOT-LOCAL-TRIAL - có nên phát hành AOT làm mặc định (2026-10-13)

Số đo và lệnh tái lập: [perf/2026-10-13-aot-local-trial.md](../perf/2026-10-13-aot-local-trial.md). Nền: [NOWPF-WP36](NOWPF-WP36-PUBLISH-AOT.md),
[NOWPF-AOT-SETTINGS-FALLBACK](NOWPF-AOT-SETTINGS-FALLBACK.md).

## 1. Trạng thái

Sau WP-35 (cửa sổ phụ Win32 gốc) bản AOT KHÔNG còn thiếu Settings/Recovery/Diagnostics như WP-36 ghi: chạy thật trên máy này (VS BuildTools + SDK 26100),
`PhotoReview.exe` 15,5 MB, thư mục 23,1 MB, 11 tệp, không cần cài .NET.

| Mục | R2R | AOT |
|---|---|---|
| `--smoke` | mã 0 (3/3) | mã 0 (3/3) |
| ZoomPrompt, Settings, Recovery, Diagnostics mở thật | 12/12 | 12/12 (cùng lớp/tiêu đề/kích thước) |
| Ảnh đầu, trung vị 8 vòng | 1028 ms | 451 ms |
| Cửa sổ hiện, trung vị | 771 ms | 169 ms |
| Working set 10 s sau ảnh đầu, trung vị | 142 MB | 112 MB |

Độ tin cậy số tuyệt đối thấp (CPU máy 84-99 %); tỉ lệ ổn định theo từng vòng. Chưa kiểm: lưu cài đặt, RAW, hành động tệp, Benchmark/BatchReview trong tiến trình AOT.

## 2. Quyết định (đề xuất, CHƯA XÁC NHẬN)

**Đề xuất: phương án B.** Đổi `PhotoReview.exe` mặc định sang AOT, nhưng chỉ sau 3 điều kiện (mục 3), giữ bản R2R làm dự phòng một bản phát hành.

| Phương án | Ưu | Nhược |
|---|---|---|
| A. Giữ R2R mặc định, AOT là artifact tùy chọn (như WP-36) | Không rủi ro mới; không đổi `release.yml` | Người dùng không nhận lợi ích khởi động (~2x), WS -21 %, bỏ phụ thuộc .NET; WP-35 đã xong nên lý do giữ bản có cầu WPF không còn |
| **B. AOT mặc định có điều kiện, R2R dự phòng một bản phát hành** | Lợi ích đo được; thư mục ít tệp; không cần runtime; còn đường lui | Zip phát hành lớn hơn trong lúc giữ cả hai; `release.yml` cần MSVC (windows-latest có sẵn), publish ~4 phút; phải sửa luật WP-30 trong `TestFilterDriftTests` |
| C. Chỉ AOT, bỏ R2R ngay | Gọn nhất | Không đường lui nếu AOT lộ lỗi cắt gọt/phản chiếu mà test JIT không bắt được |

Việc sẽ làm khi người dùng chọn B: sửa `release.yml` để zip chính chứa exe AOT (R2R vào thư mục `fallback/`), cập nhật `verify-release.ps1`/luật CI, ghi README release.

## 3. Điều kiện trước khi đổi mặc định

1. Một lượt A/B trên máy rảnh (< 15 % CPU) để chốt NW-5 (<= 350 ms) và G0.6: `tools/diag/startup-probe.ps1` cùng lệnh ở perf doc.
2. Người dùng dùng AOT thật vài ngày: lưu Settings, RAW (LibRaw), di chuyển/Recycle/Undo, Benchmark, BatchReview; ghi lỗi nếu có.
3. Job CI `publish-aot` xanh liên tục trên vài PR (hiện `continue-on-error`), cộng thêm một smoke của chính artifact AOT trong CI với `aot-window-check.ps1` trên desktop ẩn.

Hoàn tác: giữ R2R trong `fallback/`; không đổi định dạng dữ liệu hay cấu hình.

## 4. Công cụ thêm (không đổi hành vi app)

`tools/diag/aot-hidden-run.ps1` (chạy một lệnh trên desktop riêng), `aot-window-check.ps1` (mở cửa sổ phụ bằng PostMessage, ghi cửa sổ thật),
`aot-ws10.ps1` (working set 10 s). Không dùng SendInput/SetForegroundWindow; chỉ dừng tiến trình do chính script khởi chạy.
