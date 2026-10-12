---
id: NOWPF-WP29
order: 930
summary: |-
  WP-29 (đợt 3 NO-WPF, 2026-10-12): perf host + probe tái sử dụng đo trên BẢN PUBLISH R2R của shell Win32 và bản WPF, xen kẽ, xuất CSV/JSON/Markdown (`--startup-probe` của Benchmark.Cli, `tools/diag/startup-probe.ps1`; thống kê percentile/loại ngoại lai/lịch xen kẽ có test + mutation 20/21 đột biến bị bắt). Kết quả đợt 3 (`perf/2026-10-12-nowpf-wave3.md`): ảnh đầu shell 463-472 ms vs WPF ~1,2 s (NW-5 <= 600 đạt; <= 450 hụt 13-22 ms), cửa sổ thấy được 356 ms (hụt <= 150), WS đỉnh -16 %, KHÔNG đạt key->frame P95 <= 3 ms (shell 183-220 ms: ~15 % phím bị khung đơn lẻ chặn 200-450 ms ở EndDraw = WP21-STALL, WPF 11-17 ms) và Settings lần sau trên R2R median 289 ms (>= 150). Chưa làm: số lần chép khung, `tune-matrix -App`, GPU dedicated, zoom/pan của WPF. App không đổi.
---

# NOWPF-WP29 - perf host + probe đợt 3 (2026-10-12)

Thẻ: WP-29 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); gate: mục 7.4 của [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (đợt 3), NW-5, G0.x.
Số đo: [perf/2026-10-12-nowpf-wave3.md](../perf/2026-10-12-nowpf-wave3.md). Phụ thuộc: WP-20, WP-21 (đã vào master). App WPF và shell **không đổi**
(chỉ thêm công cụ ở `tools/` và test).

## 1. Đã làm

| File | Nội dung |
|---|---|
| `tools/PhotoReview.Benchmark.Cli/ShellHost/ProbeStatistics.cs` | `Percentile` (nội suy tuyến tính, kẹp [0,100]), `RemoveHighOutliers` (median + 3 x 1,4826 x MAD, cần >= 5 mẫu, MAD > 0, chỉ phía cao), `Summarize`, `Compare` (hồi quy = delta > max(5 %, 1 ms)), `ProbeSchedule.Interleave` (round-major, xáo theo seed, vòng khởi động). |
| `.../ShellHost/ProbeTrace.cs` | Parser CSV `PHOTOREVIEW_PERF_TRACE` + rút mốc: ảnh đầu (anchor `appStartup` + chênh UTC), `windowShown`/`windowRevealed`, key->frame (mốc post của probe -> `RenderedFrame` đầu tiên trước phím kế), tách "trong cache" qua `Lookup=ramHit`. |
| `.../ShellHost/StartupProbe.cs` | `IProbeSession` (ranh giới tiến trình, fake được trong test), `StartupProbeRunner` (lịch xen kẽ, loại vòng khởi động và lần lỗi, so app thứ hai với app đầu), `ProbeReport` (CSV một dòng/lần chạy cùng cột cho hai bản, JSON, Markdown), `WaveThreeTargets`. |
| `.../ShellHost/ProcessProbeSession.cs` | Một tiến trình thật: data root + trace dir + thư mục ảnh riêng, chờ khung đầu, `PostMessage(WM_KEYDOWN/UP)` (KHÔNG SendInput), WS đỉnh, CPU máy, đóng bằng `CloseMainWindow`; chỉ kill tiến trình do chính nó tạo sau 15 s. |
| `.../ShellHost/StartupProbeCommand.cs`, `Program.cs` | Verb `--startup-probe --shell <exe> [--wpf <exe>] --source <folder> [--count --rounds --warmup --seed --keys --out]`. |
| `tools/diag/startup-probe.ps1` | Vỏ PowerShell: `-Publish` hoặc `-ShellExe/-WpfExe`, in tải CPU trước lô, gọi CLI. |
| `tests/PhotoReview.Integration.Tests/ShellHost/*` | 38 test (thống kê, trace, runner, báo cáo, tham số) + `StartupProbeSmokeManualTests` (Category=Manual: 2 vòng thật trên bản publish). |

## 2. Quyết định

1. **Đo out-of-process trên exe publish, không dựng host in-proc.** Thẻ gốc ghi `ShellHost/*` chạy perf session S2/S3/S4 trên shell. `--perf-session` dựng
   đồ thị `AppHost` của WPF và không dùng được cho shell nếu không kéo `Shell.Win32` + vòng lặp Win32 vào CLI; đo ngoài tiến trình trên chính
   `PhotoReview.exe` R2R vừa đúng yêu cầu "đo trên bản publish" vừa dùng đúng các sự kiện `PhotoReviewPerf` mà hai bản cùng phát. Đổi lại: không
   có số lần chép khung và không có zoom/pan của WPF (xem mục 4).
2. **key->frame đo từ lúc probe post phím, không từ `KeyInput.inputDelayMs`.** Thông điệp được post mang dấu thời gian không có nghĩa (WPF báo ~3,5 s);
   đồng hồ UTC cùng máy gồm cả hàng đợi UI nên không bỏ sót độ trễ trước handler.
3. **Loại ngoại lai chỉ phía cao và chỉ cho độ trễ khởi động** (nhiễu chỉ làm chậm); key->frame và WS giữ nguyên để không giấu các khung treo (chính
   là phát hiện của gói này).
4. **Settings/Diagnostics lần sau đo bằng `--probe-timing` của `Shell.WpfBridge.Tests` publish R2R** (không phải qua shell): từ ngoài tiến trình không có
   đường mở Settings (không phím tắt; nút toolbar vẽ bằng D2D, không UIA) mà không SendInput/đổi mã app.

## 3. Mutation (nhẹ, thống kê/trace/runner): 21 đột biến, 20 bị bắt, 1 tương đương

Mỗi đột biến áp vào nguồn, chạy `ShellHost` (38 test không Manual), hoàn nguyên. Bị bắt: percentile (floor->ceil, dấu nội suy, bỏ kẹp [0,100]);
loại ngoại lai (ngưỡng mẫu 5->4, `<=`->`<` ở giới hạn, hệ số 1,4826->1, bỏ `Abs`, MAD=0 `<=`->`<`); `Compare` (`>`->`>=`, `Max`->`Min`); lịch xen kẽ
(`<`->`<=` vòng khởi động, bỏ seed, bỏ xáo); `Summarize` (đếm loại); trace (dấu anchor, dấu độ trễ nhập liệu, đảo lọc `ramHit`, mất `i++` của `""`);
runner (giữ vòng khởi động, giữ lần lỗi). Sống sót duy nhất: không bỏ dòng `#` ở `Parse` (đột biến tương đương: dòng comment chỉ có 1 trường nên vẫn bị
nhánh `< 11 trường` loại).

## 4. Việc còn lại / chuyển cho gói khác (CHƯA XÁC NHẬN: chờ người dùng/lead)

- **WP21-STALL vẫn mở và là gate đợt 3 trượt:** key->frame P95 trong cache của shell 183-220 ms (đích <= 3 ms và <= WPF) do khung đơn lẻ sau khoảng
  nghỉ 250 ms bị `EndDraw` chặn 200-450 ms (~15 % phím). Gợi ý WP-21: đo lại với `DXGI_SWAP_EFFECT_FLIP_DISCARD` + `ALLOW_TEARING`, hoặc giữ
  DWM/GPU "ấm" bằng khung rỗng thưa, hoặc render sang bề mặt composition; nếu không khắc phục được trên Intel UHD 630, quyết định có chấp nhận
  (NE chưa có) hay dừng ở đợt 3 theo mục 7.5 ("gate trượt: dừng, báo người dùng với số đo").
- Cửa sổ thấy được 356 ms (đích <= 150) và ảnh đầu 463-472 ms (đích <= 450): đường găng là config + ngôn ngữ (~110 ms) + khoá instance + decode
  JPEG 24 MP (~210 ms) theo WP-20; muốn xuống cần đổi thiết kế (hiện khung đen/ảnh nhỏ trước, hoặc mmap decode) - ngoài phạm vi.
- Settings lần sau median 289 ms trên R2R (đích <= 150): dựng XAML `SettingsWindow` (WPF) là phần chính; phương án: tạo sẵn cửa sổ ẩn (đổi hành vi,
  cần quyết định) hoặc chấp nhận (thẻ WP-19b cũng ghi nhận chưa đạt).
- Chưa đo: số lần chép khung, GPU dedicated, zoom/pan WPF, `tune-matrix.ps1 -App Wpf|Shell`.
