---
id: FLAKY-FIXED-JOIN-CEILINGS
order: 1340
summary: |-
  CHƯA XÁC NHẬN: quét tests/** các trần đồng hồ cố định (Join/Wait/WaitAsync/SpinUntil) quanh khởi động WPF lạnh, HWND, STA, pipe; ~45 chỗ rủi ro cao/trung bình đổi thành giới hạn chống treo 60-120 s kèm chẩn đoán (StaTestHost.DefaultTimeout 30->120 s, WaitForAsync sàn 60 s, Wait.JoinOrFail); ca đang có PR khác chỉ ghi bảng.
---

# FLAKY-FIXED-JOIN-CEILINGS - trần cố định quanh khởi động WPF lạnh (2026-10-14, CHƯA XÁC NHẬN)

Cùng họ lỗi với FLAKY-INTEGRATION-APP / FLAKY-SHELL-TESTS: test dựng cửa sổ WPF/STA thật rồi chặn bằng trần đồng hồ cố định ngắn; lần dựng
WPF đầu tiên của tiến trình lạnh (theme/font, JIT) trên runner 2 vCPU bận có thể chậm hơn trần dù không có gì hỏng. Ca mới nhất:
`PlatformPrimitivesTests.WindowsDarkTitleBarEnablesDarkModeOnARealHandle` (`thread.Join(10 s)`, "Window handle creation timed out.").

Các trần này là GIỚI HẠN CHỐNG TREO, không phải khẳng định tốc độ; đổi sang bound hào phóng thì test vẫn đỏ khi hành vi thật sự treo (chỉ đỏ
muộn hơn). Không thêm `Sleep/Delay/Yield`; số `delay|sleep` trong allowlist TEST-FLAKY không đổi. Không có khẳng định hiệu năng có chủ ý nào bị đụng.

## Cách sửa (dùng chung)

- `Wait.JoinOrFail(thread, what, bound = 120 s)` (TestSupport): Join có giới hạn, lỗi nêu việc đang chờ, thời gian chờ thực và `ThreadState`;
  thread test bật `IsBackground = true` để một lần treo thật không giữ tiến trình test.
- `StaTestHost.DefaultTimeout` 30 -> 120 s (đồng hồ của cả thân test, gồm dựng MainWindow lạnh) và `StaTestHost.WaitForAsync` có sàn 60 s
  (~100 điểm gọi truyền 5-30 s; mọi điểm đều khẳng định "rồi sẽ đúng", không điểm nào mong trả `false`).
- Hằng `Bound`/`Timeout` của lớp test nâng lên 60-120 s (giá trị dùng làm `WaitAsync`/`Wait`/`SpinUntil`, luôn là chờ tín hiệu dương).

## Bảng quét (đầy đủ cho phần chạm WPF lạnh/HWND/STA/pipe/process)

Quét 360 file / 1487 chỗ gọi API chờ trong `tests/**`; sau khi loại `string.Join`/`Path.Join` và chờ không liên quan UI còn các nhóm dưới đây.

| # | file:dòng | Trần cũ | Test làm gì | Rủi ro | Xử lý |
|---|---|---|---|---|---|
| 1 | `Integration.Tests/PlatformPrimitivesTests.cs:79` | Join 10 s | tạo HWND WPF + bật dark title bar (STA riêng) | Cao | Sửa: `Wait.JoinOrFail` 120 s + thread nền |
| 2-5 | `App.Tests/CompositionRootTests.cs:303,563,620,668` | Join 15 s | dựng `MainWindow` qua DI trên STA riêng | Cao | Sửa: như trên |
| 6 | `App.Tests/ConvertersTests.cs:29` | Join 30 s | brush WPF trên STA riêng | Trung bình | Sửa: như trên |
| 7-9 | `App.Tests/Win32DialogGuardTests.cs:35,65,95` | Join 10 s (+ `sw.Elapsed < 10 s` ở :36) | MessageBox thật trên STA riêng bị guard đóng | Cao (cùng ca với StaTestHostSmoke đã sửa ở #513) | Sửa: bound 120 s; bỏ trần đồng hồ 10 s (guard vẫn phải ghi nhận + đóng hộp thoại, treo thì đỏ) |
| 10 | `Integration.Tests/Infrastructure/StaTestHost.cs:20` | 30 s cho cả thân `RunAsync` | mọi test MainWindow/Benchmark/Recovery (dựng cửa sổ + chờ) | Cao | Sửa: 120 s |
| 11 | `StaTestHost.WaitForAsync` (~100 điểm gọi, MainWindow*/ImageCrossfade/KeepZoom/FullscreenTransition/Golden/StartupFirstShow/...) | 5-30 s | chờ ảnh đầu được trình bày / fit hội tụ / cửa sổ loaded sau khi dựng MainWindow lạnh | Cao | Sửa: sàn 60 s trong hàm (một điểm thay vì ~100) |
| 12 | `Integration.Tests/InstanceForwardPipeTests.cs:17`, `InstanceForwardClientOutcomeTests.cs:17` (39 điểm dùng) | 10 s | pipe chuyển tiếp mở file giữa hai instance: chờ tín hiệu server/client | Trung bình (pool đói) | Sửa: 60 s (các ca "quá hạn" dùng mili-giây riêng nên không đổi ý nghĩa) |
| 13 | `Shell.WpfBridge.Tests/BridgeHarness.cs:123,128` | 20 s | chờ cửa sổ phụ WPF nạp muộn hiện/đóng | Cao (lần nạp WPF đầu tiên) | Sửa: 120 s |
| 14 | `Shell.WpfBridge.Tests/ScrollBarWpfParityTests.cs:391` | `Join()` không giới hạn | thân STA dựng WPF scrollbar | Thấp (treo vô hạn) | Sửa: `JoinOrFail` 120 s + thread nền |
| 15 | `App.Tests/{AppLogDisposeBranch,Diagnostics/PerfDispatcherHooks,Services/DispatcherUiScheduler,Services/WpfImageSurface}Tests.cs` `Bound` | 30 s | dispatcher STA riêng / điều khiển WPF | Trung bình | Sửa: 120 s |
| 16 | `App.Tests/Services/AppRound3GapTests.cs:221,227,267` | 10 s (+ kết quả `SpinUntil` bị bỏ qua -> NRE) | thread Dispatcher.Run riêng | Trung bình | Sửa: 60 s + khẳng định kết quả |
| 17 | `Integration.Tests/RecoveryWindowGapTests.cs:60,61,71,75` | 20 s | `RecoveryWindow` WPF + stat bị chặn | Trung bình | Sửa: 60 s |
| 18 | `Shell.Tests/Hosting/UiThread.cs:13` `Bound` (34 điểm dùng, link vào Shell.Integration) | 30 s | UI thread Win32 của test | Trung bình | Sửa: 120 s |
| 19 | `Shell.Tests/Rendering/GpuKeepAliveTests.cs:13` | 10 s | SpinUntil trạng thái worker giữ ấm GPU | Trung bình | Sửa: 60 s (SpinUntil trả ngay khi đúng) |
| 20 | `Shell.Tests/Dialogs/Native/{BenchmarkDialogTests,RecoveryDialogTestKit}.cs` `Bound` | 30 s | hộp thoại Win32 gốc trên UiThread | Thấp-TB | Sửa: 60 s |
| 21 | `Shell.Integration.Tests/**` 14 file `Bound`; `ShellRun.DefaultWait`; `ShellRun.Dispose` (15 s/5 s); `ShellProcessTests` (15 s/10 s); `PresentationPerfReportTests:114`; `ShellStartupTests:722`; `ShellViewingTests:266` | 30 s / 15 s / 5 s / 20 s | khởi động shell Win32 thật, chờ thoát sau WM_CLOSE; Dispose bỏ rơi thread -> mọi test sau đỏ dây chuyền | Trung bình | Sửa: 60 s; Dispose 45 s + 15 s; ShellStartup 30 s; ShellViewing 60 s |

### Bỏ qua (kèm lý do)

| file:dòng | Trần | Lý do |
|---|---|---|
| `Shell.Integration.Tests` `ShellInputEndToEnd:75`, `ShellContextMenuEndToEnd:113`, `ShellScrollBarEndToEnd:44` (90 s), `ShellWindowLifecycleLeak:54` (110 s), `ShellSmokeTests` ExitBound 60 s | >= 60 s | đã là giới hạn chống treo |
| `App.Tests/MutationGapSupport.cs:68` (60 s), `AppStartupBranchTests` Bound 60 s, `Shell.Tests` NativeDialogTestKit 60 s, ShellComInteropGraphics 60 s, NativeShellFolderDialog 60 s, `Integration.Tests/Golden/GoldenWpfView` 5 phút | >= 60 s | đã hào phóng, có thông báo |
| `App.Tests/Services/WpfPresentationSinkTests.cs:86`, `WpfServicesMutationGapTests.cs:342` (`Join(5 s)` trong Dispose) | 5 s | dọn dẹp, kết quả bị bỏ qua: không bao giờ làm test đỏ |
| `App.Tests/Coordinators/*`, `ViewModels/*` (`WaitAsync(10-30 s)` ~60 điểm) | 10-30 s | logic thuần với fake, không dựng WPF/HWND; cùng họ "đói pool" nhưng ngoài phạm vi (nếu muốn: một PR riêng nâng `StalePreviewHarness.Bound`) |
| `Integration.Tests/ExplorerNativeViewTests.cs`, `ShellComWp33Tests.cs` `TryGetSnapshotAsync(30 s)` | 30 s | tham số timeout của sản xuất (Explorer COM), không phải trần của test |
| `Integration.Tests/ExplorerOrderService*Tests.cs` | - | đã sửa ở FLAKY-INTEGRATION-APP (#513) |
| `Integration.Tests/BenchmarkCliJunctionTests.cs:45`, `PhysicalFileSystemReparseTests.cs:52` (30 s `cmd /c mklink`) | 30 s | tiến trình `cmd` thuần, chỉ tạo junction, không WPF |
| `Integration.Tests/WindowsDisplayClockTests.cs:72,103` | 20/30 s | đồng hồ vblank giả, chờ semaphore; không WPF |
| `Integration.Tests/RecoveryPathPanelTests.cs:145,161` | 10 s | chờ thread giữ clipboard (thread thường); thân STA đã có 60 s |
| `Imaging.Tests/**`, `Core.Tests/**` (`Timeout`/`Bound` 10-30 s) | 10-30 s | không WPF/HWND (decoder, cache, file log); ngoài phạm vi |
| `Architecture.Tests/**` | - | đọc mã nguồn/assembly, không có thread UI |
| `Shell.Tests/Dialogs/Native/Wp27VisualCheckTests.cs:597` (`worker.Wait(20 s)`) | 20 s | kiểm tra thị giác thủ công, không chạy trong CI |
| `Shell.Tests/**/DWriteTextRendererTests.cs` | - | thuộc PR #516 đang mở (ghi bảng, không sửa) |
| `Shell.Tests/Dialogs/SettingsUi/*`, `SettingsScrollPaintTests.cs`, `SettingsScrollRepaintFixTests.cs` | - | thuộc PR #501 / nhóm kịch bản Tab-căn giữa đang mở (ghi bảng, không sửa) |
| `Integration.Tests/AppForwardedOpenTests.cs` (A3) | - | nguyên nhân chưa tìm ra (xem FLAKY-INTEGRATION-APP); `WaitForAsync` hưởng sàn 60 s |

## Kiểm chứng

Chạy lại các lớp đã đụng bằng `tools/run-tests-hidden.ps1` (`--no-build`, `--blame-hang-timeout 5m`), 10 lượt x 5 dự án dưới 4 burner CPU:
50/50 xanh; lượt toàn bộ CI-filter App/Integration/WpfBridge/Shell.Integration/Shell.Tests xanh; Architecture.Tests 160/160.
Đột biến: probe tạm (đã xoá) cho `Wait.JoinOrFail` với thread không bao giờ kết thúc + bound 1 s -> ném `TimeoutException` kèm
"thread state": test vẫn đỏ khi treo thật.

## Rollback

Chỉ đổi code test + một hàm trợ giúp trong `PhotoReview.TestSupport/Wait.cs`; khôi phục từ git. Không đổi mã sản xuất.
