---
id: NOWPF-WP08
order: 242
summary: |-
  WP-08 (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): thực thi C-14 trong App.Shared (không WPF, AOT-ready): JsonWindowPlacementStore (JSON source-gen đúng lược đồ window-placement.json, prefetch một lần, ghi nguyên tử), WindowPlacementRules (luật show-command thuần), FullscreenController (F11 theo HWND). WindowPlacementService và FullscreenWindowPlacer của app WPF thành adapter mỏng; hành vi WPF không đổi (kể cả #351/#372, P-1). Read theo hợp đồng lọc "thấy được", còn Prefetch/TakePrefetched/ReadRaw trả dữ liệu chưa lọc để cửa sổ Maximized lưu ngoài màn hình vẫn mở lại Maximized.
---

# NOWPF-WP08-PLACEMENT - placement và F11 theo HWND (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 5, C-14), thẻ WP-08 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Chữ ký C-14 đóng băng (`contracts.v1.txt` không đổi); gói này chỉ viết phần thực thi.

## 1. Đã làm

| Kiểu (App.Shared, `PhotoReview.App.Windowing`) | Vai trò |
|---|---|
| `WindowPlacementRules` | `NormalizeShowCommand`, `ResolveShowCommand(…, WindowShowState?)`, `PlanStateBeforeShow` - dời nguyên từ `WindowPlacementService`, thuần |
| `JsonWindowPlacementStore : IWindowPlacementStore` | `Read`, `Save`, `Prefetch`, `TakePrefetched` + `ReadRaw`, `IsVisible`, `WriteAtomically`; JSON qua `WindowPlacementJsonContext` (source-gen, `IncludeFields`, `WriteIndented`) |
| `FullscreenController : IFullscreenController` | logic F11 của `FullscreenWindowPlacer` theo `nint hwnd` + `WindowShowState`, P/Invoke bằng `LibraryImport` |
| `WindowPlacementVisibility` (internal) | luật 80x80 px, nhận work area (thuần, test được) |
| `IMonitorLayout`, `Win32MonitorLayout`, `ScreenRect` (internal) | dời từ `App/Coordinators/MonitorLayout.cs`; `EnumDisplayMonitors` dùng con trỏ hàm `UnmanagedCallersOnly` (L-AOT) |

App WPF: `WindowPlacementService` và `FullscreenWindowPlacer` giữ nguyên API nội bộ (tên, chữ ký, kiểu lồng `WindowPlacement`/`Rectangle`
mà test đang dùng) và chỉ còn nối `Window` WPF với phần dùng chung. `InitialViewportPredictor` nhận `WindowPlacementData?` và dùng
`WindowPlacementRules` (hết `System.Windows.WindowState`). `MainWindow.xaml.cs` KHÔNG phải sửa (thẻ dự kiến 3 method; adapter giữ API cũ).

## 2. Quyết định và chỗ lệch khỏi thẻ

1. **`Read` lọc "thấy được", `Prefetch`/`TakePrefetched`/`ReadRaw` không lọc.** Chú thích hợp đồng: `Read` trả null khi thiếu/hỏng/không thấy
   được. Nhưng `RestoreBeforeShow` (P-1) cần placement ngoài màn hình của cửa sổ đóng khi Maximized (monitor đã rút) để mở lại Maximized, và
   `InitialViewportPredictor` cũng vậy. Nếu `Prefetch` lọc, hành vi WPF đổi. Nên: `Read` đúng hợp đồng; ba đường còn lại trả dữ liệu thô,
   nơi dùng tự áp `WindowPlacementRules.PlanStateBeforeShow` + `IsVisible`. WP-20 (shell Win32) làm y như adapter WPF.
2. **File hỏng:** `Read` (hợp đồng) trả null; `ReadRaw` ném `JsonException` như `Read` cũ, để `RestoreBeforeShow` ghi log như trước.
3. **Hai cờ ngoài hợp đồng trên `FullscreenController`** (`EnterNeedsMaximizedState`, `ExitNeedsStateRestore`): `Enter/Exit` của
   `IFullscreenController` trả void nên adapter framework không biết controller có tự chuyển cửa sổ ở mức OS hay chưa (không HWND, không có
   thông tin monitor, không có bounds đã lưu). Adapter WPF đọc hai cờ này để đặt `WindowState` đúng như thứ tự cũ. Là thành viên của lớp
   cụ thể, không đổi chữ ký hợp đồng.
4. **Cửa sổ thu nhỏ:** WPF adapter tự đưa về Normal trước (như cũ, không nhấp nháy); controller chỉ `ShowWindow(SW_SHOWNORMAL)` khi
   `IsIconic` (cho shell Win32 không có framework hộ).
5. **`ScreenRect`/`IMonitorLayout` đổi namespace** từ `PhotoReview.App.Coordinators` sang `PhotoReview.App.Windowing` (hai file test chỉ thêm
   `using`, assert không đổi).
6. **App.Shared bật `AllowUnsafeBlocks`** (LibraryImport + con trỏ hàm callback), như `Shell.Interop`.

## 3. Test

- Giữ xanh không đổi assert: `WindowPlacementService*Tests`, `WindowPlacementPrefetchTests`, `WindowPlacementVisibilityTests`,
  `FullscreenWindowPlacer*Tests`, `InitialViewportPredictorTests`, `StartupFirstShow*`, `FullscreenTransitionFrameTests`,
  `WindowPlacementIsolationTests`, `ContractSurfaceTests`, `ShellRulesTests` (L-AOT/L-SHARED), `AppThreadAffinityTests` (ADR 0005).
- Mới: `JsonWindowPlacementStoreRoundTripTests` (file do code cũ ghi -> đọc -> ghi lại byte-identical sau chuẩn hoá xuống dòng, golden tên
  trường, mặc định `ShowCommand`, `Read` lọc, ngưỡng 80x80, prefetch một lần theo đường dẫn không phân biệt hoa thường, luật show-command,
  controller không HWND).
- Mutation thủ công (19 biến thể trên phần mới): 18 bị giết (gồm ranh giới 80x80, caption band 120x16/32, MinPosition, ShowCommand mặc định, prefetch một lần); 1 sống sót là tương đương (bỏ gán `ExitNeedsStateRestore = false` thừa trong nhánh native của `Exit`, cờ đã mặc định false).

## 4. Không làm / để gói sau

- `WM_DPICHANGED`, lưu placement khi đóng ở shell Win32: WP-20 (dùng `IWindowPlacementStore` + `IFullscreenController`).
- `MainWindow.xaml.cs`: không đổi (xung đột với WP-07 tránh được).
