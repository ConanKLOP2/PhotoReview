---
id: NOWPF-WP14
order: 292
summary: |-
  WP-14 (đợt 2 của NO-WPF-EXEC-PLAN, 2026-10-10): thực thi C-04 (Win32UiDispatcher + Win32UiSynchronizationContext), C-05 (TimerFrameClock, SwapChainFrameClock) và C-15 (ShellWindow) trên một MessageLoop MsgWaitForMultipleObjectsEx trong Shell.Win32/Hosting; hợp đồng v1 không đổi; interop tạm cục bộ trong Hosting/PendingInterop chờ WP-13a; app WPF không đổi.
---

# NOWPF-WP14-WINDOW-LOOP - cửa sổ, message loop, dispatcher, frame clock (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) mục 5 (C-04, C-05, C-15), thẻ WP-14 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Khung trước đó: [NOWPF-WP01-SCAFFOLD](NOWPF-WP01-SCAFFOLD.md). Không đổi `Approved/contracts.v1.txt`, không đổi app WPF, không thêm gói NuGet.

## 1. Đã làm

Tất cả trong `src/PhotoReview.Shell.Win32/Hosting/` (internal, chỉ shell dùng):

| File | Nội dung |
|---|---|
| `Win32UiDispatcher.cs` | C-04. 4 hàng đợi theo mức, gắn một UI thread; đánh thức bằng `PostMessage(hwndMessageOnly, WM_APP+1)` với giao thức cờ (tối đa một WM_APP+1 chờ mỗi lượt xả, không ngập hàng đợi 10.000 message). |
| `Win32UiSynchronizationContext.cs` | `Post` = Normal; `Send` chạy ngay trên UI thread, từ thread khác chờ qua `InvokeAsync(Send)` (huỷ khi dispatcher dừng, không treo). `Install` trả scope khôi phục context cũ. |
| `MessageLoop.cs` | Vòng lặp: xả Send/Normal/Render -> phát khung đã tới nhịp -> bơm tối đa 32 message (dừng ngay khi có việc mức cao) -> một việc Background khi hàng đợi message rỗng -> `MsgWaitForMultipleObjectsEx(QS_ALLINPUT, MWMO_INPUTAVAILABLE)` trên waitable của đồng hồ armed + hạn chót gần nhất. Điểm móc `IMessageFilter` cho WP-19b. |
| `FrameClockBase.cs` | Phần chung C-05: armed = có subscriber hoặc `RequestFrame` đang chờ; `FrameTick(Timestamp, RenderingTimeMs từ lúc tạo đồng hồ, DisplayTiming từ IDisplayClock)`. |
| `TimerFrameClock.cs` | Dự phòng: waitable timer độ phân giải cao (rơi về timer thường), hẹn đúng một lần mỗi khung, neo lưới vblank khi biết, trễ thì bỏ khung lỡ. |
| `SwapChainFrameClock.cs` | Chính: waitable của swap chain (WP-15 cấp, không đóng); báo dồn trong nửa chu kỳ bị hoãn tới hết chu kỳ; không Present thì hạn chót dự phòng 2 chu kỳ. |
| `ShellWindow.cs` | C-15. Top-level PerMonitorV2; DPI đọc ở WM_NCCREATE; WM_DPICHANGED áp rect gợi ý rồi mới phát `DpiChanged`; WM_GETMINMAXINFO = Min*Dip x DPI; WM_CLOSE -> `Closing` huỷ được -> DestroyWindow; `Invalidate` gộp thành một `Render` ở khung kế. |
| `WndProcThunk.cs` | WndProc `UnmanagedCallersOnly` (cửa sổ shell qua GCHandle trong GWLP_USERDATA, cửa sổ dispatcher qua dispatcher của thread). Ngoại lệ bị giữ trên thread và ném lại sau `DispatchMessage`/`SendMessage`/`CreateWindowEx`; sau lỗi trả 0 (không xử lý mặc định). |
| `PendingInterop/PendingNativeMethods.cs` | Khai báo **tạm** (mục 3). |

Ngữ nghĩa ưu tiên (đúng mục C-04): Send > Normal > Render chạy theo mức, cùng mức FIFO, trong một lượt xả trước message kế tiếp và
trước khi phát khung; việc post trong lượt cũng chạy trong lượt (như ưu tiên trên Input của WPF), lượt tự ngắt sau 50 ms để input
không chết đói. Background một việc mỗi lần, chỉ khi hàng đợi message rỗng. Trong vòng lặp modal của hệ thống (kéo resize, menu,
MessageBox) WM_APP+1 vẫn được dispatch nên hàng đợi vẫn chạy.

## 2. Lệch / bổ sung so với thẻ

1. Thêm `FrameClockBase.cs` và `PendingInterop/` (thẻ không liệt kê). `ShellWindow`, `ShellWindowOptions`, `MessageLoop`, đồng hồ đều
   `internal` (WP-20 cùng assembly); không thêm kiểu public nên hợp đồng v1 giữ nguyên.
2. `IMessageFilter.PreFilterMessage(in WindowMessage)` không mang `time`/`pt` của MSG; WP-19b mở rộng nếu `ComponentDispatcher` cần.
3. Ngoại lệ: việc `Post` -> log (`ILog.Error`), vòng lặp sống; `InvokeAsync` -> vào Task; handler `Frame` và WndProc -> lan ra
   `MessageLoop.Run` (fail fast như WPF). `SynchronizationContext.Post` sau khi dispatcher dừng bị bỏ (như WPF sau shutdown).
4. `InvokeAsync(Send)` vẫn xếp hàng (mức cao nhất), không chạy inline - như `Dispatcher.InvokeAsync` của WPF. Task của `InvokeAsync`
   dùng `RunContinuationsAsynchronously` (continuation của thread pool không chạy trên UI thread); `YieldAsync` thì inline đúng mức.
5. `IsLoaded` = sau WM_CREATE, trước WM_DESTROY và `SetSurfaceReady(true)` (mặc định true cho tới khi WP-15/WP-20 gọi).
6. `ShellWindow` thêm nội bộ: `Show(activate)`, sự kiện `Render`, `ClientSizePixels`, `RenderCount`; `MessageLoop.PassCount/WaitCount`,
   `Win32UiDispatcher.WakeMessagesPosted` (chẩn đoán cho test).
7. `Program.cs`/`BootstrapWindow` (smoke WP-01) không đổi - thuộc WP-20.
8. `tests/PhotoReview.Shell.Integration.Tests/*.csproj`: bật `AllowUnsafeBlocks` và link `Shell.Tests/Hosting/UiThread.cs` (harness UI thread).

## 3. Interop: dùng Shell.Interop (WP-13a), phần còn thiếu để tạm

WP-13a (#400) merge trong lúc làm: Hosting/* đã chuyển sang `PhotoReview.Shell.Interop` (`User32`, `Kernel32`, `WindowMessages`, `Rect`,
`Point`, `Msg`, `WndClassEx`; tiện ích `Hosting/NativeRect.cs` cho Width/Height). Những thứ WP-13a chưa khai báo vẫn ở
`Hosting/PendingInterop/PendingNativeMethods.cs` (WP-14 không sửa file của WP-13a); lead chuyển sang `Shell.Interop/Win32/*` rồi xoá thư
mục (L-PINV, bật sau, sẽ đỏ tới khi chuyển):

- user32: `IsWindow`, `SendMessageW`, `GetCursorPos`, `WindowFromPoint` (POINT theo giá trị, tạm khai báo `long` đóng gói vì `Point` của
  assembly khác không được coi là blittable - SYSLIB1051), `GetCapture`, `ScreenToClient`, `SetWindowTextW`, `ValidateRect`.
- kernel32: `CreateWaitableTimerExW`, `SetWaitableTimer`, `CancelWaitableTimer`, `WaitForSingleObject`, `CloseHandle`.
- Struct: `MINMAXINFO`, `CREATESTRUCTW`. Hằng: `HWND_MESSAGE`, `HTCLIENT`, `WA_INACTIVE`, `IDC_WAIT`, `IDC_SIZEALL`,
  `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, `TIMER_ALL_ACCESS`.

Test tự khai báo riêng (không thuộc shell): `GetWindowRect`, `ClientToScreen`, `GetWindowTextW`, `GetGuiResources`, `GetCurrentProcess`,
`GetCurrentThread`, `GetThreadTimes`.

## 4. Kiểm chứng

- `tests/PhotoReview.Shell.Tests/Hosting/`: `Win32UiDispatcherTests` (1000 việc xen kẽ từ UI thread và từ thread pool khi UI bận -> đúng
  thứ tự mức + FIFO, 1 WM_APP+1; Background sau mọi message đã xếp, Normal trước message kế tiếp; `await` quay về UI thread; InvokeAsync/Post
  từ pool; ngoại lệ được log, loop sống; huỷ/dừng không treo; vòng lặp modal lạ vẫn chạy hàng đợi), `FrameClockTests` (đồng hồ giả: armed,
  <= 1 tick/khung, bỏ khung lỡ, dừng khi hết subscriber, lưới vblank, báo dồn swap chain, hạn chót dự phòng),
  `MessageLoopFrameClockTests` (thời gian thật: tick trên UI thread <= 1/chu kỳ, dừng khi bỏ đăng ký - đo bằng đồng hồ nhân chứng, handle
  của đồng hồ không armed không bị tiêu, vòng lặp nhàn rỗi chặn trong MsgWait thay vì quay rỗng).
- `tests/PhotoReview.Shell.Integration.Tests/Hosting/`: `ShellWindowTests` (Closing huỷ, WM_CLOSE từ thread khác, WM_DPICHANGED giả lập
  bằng SendMessage, WM_GETMINMAXINFO, kích hoạt, thứ tự handler, Invalidate gộp, tiêu đề tiếng Việt, toạ độ DIP, con trỏ, capture,
  ngoại lệ WndProc ném lại từ vòng lặp và từ Close), `ShellWindowLifecycleLeakTests` (50 vòng mở/vẽ/đóng trên một thread: USER/GDI handle
  không tăng; collection tuần tự), `ShellIdleCpuReportTests` (Manual: CPU UI thread nhàn rỗi với cửa sổ trống hiện = 0,000 % trong 3 s,
  1 lần chờ).
- 20 lần liên tiếp hai project (desktop ẩn): xem PR. Mutation: xem PR.
