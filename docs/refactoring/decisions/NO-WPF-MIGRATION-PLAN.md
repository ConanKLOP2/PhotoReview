---
id: NO-WPF-MIGRATION-PLAN
order: 181
summary: |-
  Kế hoạch bỏ WPF (giữ .NET): cửa sổ xem ảnh viết lại bằng Win32 + Direct2D (COM sinh mã nguồn, sẵn sàng Native AOT), cửa sổ phụ nạp WPF muộn ở giai đoạn đầu rồi mới bỏ hẳn; đo 2026-10-10: viewer tối giản Win32+WIC (.NET R2R) vẽ ảnh đầu sau ~180 ms, WPF tối giản ~400 ms, PhotoReview ~1050-1200 ms; lợi ích chỉ ở khởi động nguội (duyệt ảnh đã 1,4-2,2 ms P50); Native AOT chưa đo được vì máy thiếu MSVC + Windows SDK; còn chờ người dùng chốt NW-1..NW-5 và cho phép cài toolchain.
---

# NO-WPF-MIGRATION-PLAN - bỏ WPF, giữ .NET, hướng tới Native AOT (2026-10-10)

Trạng thái: **KẾ HOẠCH, chưa có mã nào thay đổi.** Người dùng chọn hướng "bỏ WPF trước" (thay vì viết lại bằng Rust,
xem [RUST-MIGRATION-EVAL](RUST-MIGRATION-EVAL.md)). Tài liệu này để người dùng quyết định từng cổng (gate); mọi con số
có ghi nguồn: **đo** (đo trên máy này hôm nay), **repo** (đường dẫn file đo trước), hoặc **ước** (ước lượng, kèm cơ sở).

## 1. Tóm tắt điều hành

- **Bỏ WPF chỉ làm nhanh phần khởi động nguội (mở ảnh từ Explorer), không làm nhanh việc duyệt ảnh.** Chuyển ảnh đã
  preload hiện mất 1,4-2,2 ms P50 / 2,5-8 ms P95 ([device-tuning](../perf/2026-10-07-device-tuning-results.md),
  [TieredPGO](../perf/2026-10-07-tieredpgo-confirmation.md)), dưới một khung hình 60 Hz (16,7 ms). Không UI framework nào cải
  thiện được điều người dùng không thấy.
- **Khởi động nguội (đo, mục 2):** từ lúc tạo tiến trình tới ảnh đầu hiển thị, cùng file JPEG 12,5 MP trong F4:

  | Biến thể (đo 2026-10-10, 8 lần, median [min-max]) | Ảnh đầu đã vẽ | Ghi chú |
  |---|---:|---|
  | PhotoReview 2.0.383, bản build (JIT) - Explorer đang chạy bản này | **1182** [1157-1206] | `RenderedFrame` 1207 |
  | PhotoReview 2.0.383, bản publish (ReadyToRun) | **1054** [1042-1556] | `RenderedFrame` 1067 |
  | Viewer WPF tối giản (1 Window + 1 Image, decode song song từ `Main`) | **403** [386-458] | sàn của WPF |
  | Viewer Win32 + WIC + GDI, .NET ReadyToRun (không WPF) | **182** [168-219] | sàn của ".NET không WPF" |
  | Cùng viewer, COM sinh mã nguồn (dạng tương thích AOT), R2R | **181** [173-185] | lô 2 (mục 2.3); bản COM cũ cùng lô: 175 |
  | Cùng viewer, **Native AOT** | **chưa đo** | máy thiếu MSVC linker + Windows SDK (mục 9) |

- **Kết luận:** WPF tự nó tốn khoảng **220 ms** cố định (403 - 182) và kiến trúc khởi động hiện tại của app tốn thêm khoảng
  **650 ms** (1054 - 403) - phần lớn phần sau **sửa được ngay trong WPF**. Bỏ WPF đưa app về khoảng **300-400 ms** (ước,
  R2R) hoặc **250-350 ms** (ước, Native AOT), tức nhanh hơn hiện tại **~700-850 ms**, nhưng nhanh hơn một bản WPF đã tối ưu
  (ước ~500-600 ms) chỉ **~200-300 ms**.
- **Stack chọn: Win32 thuần + Direct2D 1.1 (swap chain DXGI flip-model) + WIC + DirectWrite, gọi qua COM sinh mã nguồn
  (`[GeneratedComInterface]`, có sẵn trong .NET SDK, không cần gói NuGet)** cho cửa sổ xem ảnh. Cửa sổ phụ (Settings, Recovery, ...)
  **giữ WPF nhưng nạp muộn** ở giai đoạn đầu (chỉ khi mở), rồi mới thay hẳn để bật Native AOT (mục 3).
- **Công:** ~**12-19 người-tuần** tới bản "lai" (viewer Win32, cửa sổ phụ WPF nạp muộn, R2R) và ~**21-33 người-tuần** tới
  Native AOT hoàn toàn (mục 6). So với: tối ưu trong WPF ~**1-3 người-tuần**.
- **Mức chắc chắn:** cao cho "sàn" (đo trực tiếp); trung bình cho con số cả app sau khi chuyển (ước từ phân rã đo được);
  thấp cho phần Native AOT (chưa đo, cần cài toolchain).
- **Khuyến nghị thứ tự:** (1) làm spike S1 (Native AOT, cần cho phép cài toolchain) và song song làm "WPF nhanh" (giai đoạn
  P-1, rẻ, lợi ích ngay, phần lớn mã dùng lại cho bản Win32); (2) đo lại; (3) chỉ đi tiếp P1+ nếu cổng G0 đạt (mục 5).

**Điều có thể làm đổi kết luận:** spike AOT cho kết quả không tốt hơn R2R đáng kể (< 50 ms) -> dừng ở bản "lai" R2R;
bản WPF đã tối ưu đạt ≤ 450 ms -> chênh lệch còn lại không đáng 20+ người-tuần; người dùng thấy độ trễ khởi động không còn là
vấn đề sau P-1.

## 2. Hiện trạng đo được

### 2.1 Cách đo (2026-10-10)

- File: `C:\Xiuren\[[WALLPAPER]\...-114.jpg` (5,8 MB, 2887x4330, giữa thư mục F4 2867 file; fixture: `work/diag/fixtures.local.json`).
  Màn hình chính 1920x1080. OS file cache ấm (không xả được khi không có quyền admin); cache đĩa của app bị xoá trước mỗi lần (cold).
- Probe (công cụ tạm trong scratchpad, **không commit**): `Process.Start` với đồng hồ QPC chung; PhotoReview đọc CSV
  `PHOTOREVIEW_PERF_TRACE` (`RenderedFrame` = tick render thứ 2 sau khi gán ảnh, `Presented` = tick 1); viewer tối giản ghi mốc vào
  file (`main`, `shown`, `decoded`, `painted`, `presented` = sau `DwmFlush`). Data root riêng (`PHOTOREVIEW_DATA_ROOT` +
  `PHOTOREVIEW_ISOLATE_CONFIG`), placement Maximized, `PHOTOREVIEW_DIAG_INSTANCE_LABEL=AGENT CHECK`, không Explorer mở F4 (sort Default),
  không SendInput/SetForegroundWindow, đóng bằng WM_CLOSE, mọi tiến trình đã dọn.
- Lô xen kẽ (thứ tự xáo mỗi vòng), 1 vòng khởi động bỏ + 8 vòng đo, CPU nền 4-8 %. Viewer tối giản: decode bắt đầu ngay trong
  `Main` trên thread riêng, cửa sổ Maximized, hộp decode = màn hình (DCT-scale 1/4 của WIC -> 722x1083).
- **Hạn chế:** không biết màn hình bật hay tắt (người dùng vắng); `DwmFlush` dao động lớn (192-1274 ms) nên dùng mốc `painted`
  cho Win32 và tick 1/2 cho WPF - hai mốc này **không đồng nhất tuyệt đối** (WPF tick 2 ≈ khung đã render; GDI `painted` = pixel đã ghi
  vào surface, DWM ghép ở khung kế tiếp, ≤ 16,7 ms khi màn hình bật). Sai khác này nhỏ hơn nhiều so với chênh lệch đo được.

### 2.2 Kết quả lô 1 (ms từ CreateProcess, median [min-max], n = 8)

| Biến thể | Cửa sổ thấy được | `main`/`appStartup` | Show xong | Decode xong (app: `Assign`) | Vẽ (tick 1 / painted) | Khung đã render |
|---|---:|---:|---:|---:|---:|---:|
| pr-build (2.0.383, JIT) | 1011 | 169 | 887 | 1112 | 1182 | 1207 |
| pr-r2r (publish) | 903 | 175 | 776 | 997 | 1054 | 1067 |
| wpf-jit (tối giản) | 378 | 87 | 405 | 283 | 410 | 423 |
| wpf-r2r (tối giản) | 376 | 91 | 399 | 289 | 403 | 425 |
| w32-jit (Win32+WIC+GDI) | 86 | 68 | 89 | 175 | 199 | 364 (DwmFlush) |
| w32-r2r | 79 | 68 | 88 | 173 | 182 | 284 (DwmFlush) |

### 2.3 Lô 2 - COM sinh mã nguồn (dạng AOT) so với COM có sẵn

Lô xen kẽ riêng (8 lần, CPU nền 5 %), cùng probe và file:

| Biến thể | `main` | Cửa sổ thấy được | Decode xong | Painted |
|---|---:|---:|---:|---:|
| w32-r2r (`[ComImport]`, delegate WndProc) | 65 [61-74] | 79 [77-208] | 166 [154-175] | **175** [174-232] |
| gen-r2r (`[GeneratedComInterface]` + `StrategyBasedComWrappers`, `LibraryImport`, `UnmanagedCallersOnly`) | 67 [65-71] | 79 [78-79] | 170 [165-177] | **181** [173-185] |

- COM sinh mã nguồn **không chậm hơn** COM có sẵn (khác biệt 6 ms, trong nhiễu). Với `IsAotCompatible=true`, bản `gen` build
  **0 cảnh báo** trim/AOT; bản `[ComImport]` có cảnh báo IL2072 (`Activator.CreateInstance(Type.GetTypeFromCLSID)`).
- `dotnet publish -p:PublishAot=true` cả hai bản: dừng ở "Platform linker not found" (thiếu MSVC) -> **chưa có số Native AOT**.
- PhotoReview R2R trong lô này: chỉ mốc "cửa sổ thấy được" hợp lệ (894 ms [877-949], khớp lô 1); các mốc CSV bị bỏ vì lỗi của probe
  (đọc nhầm CSV cũ còn trong thư mục trace) - không ảnh hưởng lô 1.
- **Ước Native AOT:** phần `main` (65-68 ms, gồm khởi động runtime + JIT `Main`) là phần AOT bớt được; hello-world AOT trên Windows thường
  vào `main` sau ~10-25 ms (ước, chưa đo trên máy này) -> sàn AOT ~**120-150 ms**. Decode WIC (~100 ms từ `main`, gồm tạo factory COM
  và DCT-scale) không đổi.

### 2.4 Phân rã khởi động của PhotoReview (từ CSV của chính app, lô 1, median, ms từ lúc tiến trình bắt đầu)

| Mốc | build | publish R2R | Bản chất | Bỏ WPF có xoá được? | Sửa được trong WPF? |
|---|---:|---:|---|---|---|
| `appStartup` (runtime + nạp assembly, trước code app) | 165 | 170 | .NET + WPF assembly | một phần (Win32 viewer: `main` ở 68 ms) | một phần (bớt assembly nạp sớm) |
| -> `servicesBuilt` | 188 | 189 | DI | không | - |
| -> `renderThreadConnected` (`StartupWarmup.ConnectRenderThread`) | 302 | 291 | **WPF cố định ~100 ms** | có | không (chỉ dời khỏi đường găng) |
| -> `settingsLoaded` (UI chờ task đọc config + ngôn ngữ) | 426 | 360 | thiết kế app | - | **có** (chồng lên việc khác) |
| -> `mainWindowCtor` (resolve đồ thị MainViewModel) | 521 | 422 | thiết kế app + JIT | - | **có** (R2R, lazy) |
| -> `xamlLoaded` (`InitializeComponent`) | 716 | 606 | **WPF ~185 ms** (XAML + theme 1480 dòng) | có | một phần (XAML nhẹ hơn, theme nạp muộn) |
| -> `placementRestoredBeforeShow` (tạo HWND, SourceInitialized) | 850 | 738 | WPF + app | có | một phần |
| -> `initialDecodeStarted` | 879 | 770 | **thiết kế app: decode ảnh đầu bắt đầu quá muộn** | - | **có** (decode từ `Main`, như viewer tối giản) |
| -> `windowShown` / `windowRevealed` (bỏ cloak ở tick render 2) | 887 / 1007 | 777 / 894 | WPF render | có | - |
| -> `Assign` (decode ảnh đầu ~230 ms kể cả JIT/khởi tạo WIC) | 1112 | 997 | WIC + JIT | không (WIC dùng chung) | **có** (bắt đầu sớm thì chồng lên WPF) |
| -> `RenderedFrame` | 1207 | 1067 | WPF render | có | - |

Đọc: decode ảnh đầu (WIC, ~105 ms trong viewer tối giản, ~230 ms trong app vì nhiều lớp + JIT) là việc **không đổi** dù dùng
framework nào - Rust/C++ cũng gọi cùng WIC. App hiện chỉ bắt đầu decode ở ~770 ms; viewer tối giản bắt đầu ở ~70 ms. Đây là
nguồn chậm lớn nhất và **sửa được trong WPF** (giai đoạn P-1).

### 2.5 Duyệt ảnh, preload, cache, RAM (repo)

| Chỉ số | Giá trị | Nguồn |
|---|---|---|
| Chuyển ảnh (S2 Next chậm / S3 burst / S4 nhảy) P50 | 2,19 / 1,39 / 1,60 ms | [tieredpgo](../perf/2026-10-07-tieredpgo-confirmation.md) |
| P95 | 8,32 / 2,50 / 2,95 ms | như trên |
| Peak working set (F4, preload cửa sổ 32/8) | ~14,3-14,8 GB | [device-tuning](../perf/2026-10-07-device-tuning-results.md) |
| Decode WIC 24 MP -> FHD (P50) | ~8 ms | [ADR 0001](../../adr/0001-image-decoder.md) |
| Mở folder: ảnh đầu (S1, 1625 file) | 323 ms first / 421 ms final | [baseline](../perf/2026-09-23-baseline-ar02e.md) |

RAM gần như toàn bộ là pixel ảnh (bộ nhớ native của MIL/WIC), không phải WPF. Bỏ WPF có thể bớt vài chục MB (chưa đo) - không đáng kể
so với 14 GB. Thời gian chuyển ảnh bị chi phối bởi cache hit + gán bitmap; bỏ WPF **có thể** giảm vài trăm µs (không còn render
thread riêng, không layout) nhưng chưa có bằng chứng và không phải mục tiêu.

## 3. So sánh stack thay thế WPF

Tiêu chí theo thứ tự ưu tiên của dự án (AGENTS.md: tốc độ duyệt/hiển thị trước).

| Tiêu chí | **Win32 + Direct2D (COM sinh mã)** | WinUI 3 (Windows App SDK) | Avalonia 11 | WinForms + Direct2D | Ở lại WPF (tối ưu) |
|---|---|---|---|---|---|
| Khởi động nguội tới ảnh đầu | **đo: sàn 182 ms (R2R)**; AOT ước 120-150 | chưa đo; khởi tạo WinAppSDK + XAML runtime thường vài trăm ms (ước 350-600) | chưa đo; AOT + Skia (ước 200-350) | chưa đo (ước 250-350, WinForms nạp nhanh hơn WPF) | **đo: sàn 403 ms**; app tối ưu ước 500-600 |
| Native AOT | **có** (COM sinh mã + `LibraryImport` + `UnmanagedCallersOnly`; analyzer AOT: 0 cảnh báo ở lô 2 - mục 2.3) | có từ Windows App SDK 1.6 (cần CsWinRT/AOT-safe binding) | có (tài liệu chính thức; compiled bindings bắt buộc) | **không được hỗ trợ chính thức** (trimming WinForms chưa hỗ trợ) | **không** (WPF dùng reflection/XAML runtime, không trim được) |
| Zoom/pan/kinetic mượt | Direct2D GPU, tự điều khiển hoàn toàn; phải tự viết lại kinetic/touchpad (đã có logic thuần trong `KineticPan`, `TouchpadGestures`) | tốt (ScrollViewer + DirectManipulation sẵn) | tốt (Skia GPU), tự viết kinetic | được nhưng thô | đã có, đã tinh chỉnh (Q-R40) |
| DPI / đa màn hình / F11 | tự xử lý Per-Monitor V2 (`WM_DPICHANGED`), F11 bằng `SetWindowPos` - đã có logic `FullscreenWindowPlacer` | tốt, nhưng API cửa sổ hạn chế (AppWindow) | tốt | PerMonitorV2 có nhưng nhiều lỗi biết trước | đã có |
| Tái dùng ViewModel/Core/Imaging | ViewModels/Coordinators **không phụ thuộc WPF** (0/9 và 2/25 file) -> dùng lại; Imaging phải tách `BitmapSource` (mục 4) | dùng lại, nhưng binding XAML khác WPF | dùng lại tốt nhất (XAML gần WPF) | dùng lại | 100 % |
| i18n hiện có (JSON + source generator trong Core) | dùng lại; chuỗi gán bằng code | dùng lại | dùng lại | dùng lại | có |
| Test UI hiện có (45 file UI trong Integration.Tests) | viết lại trên HWND thật + desktop ẩn (`run-tests-hidden.ps1` dùng được cho mọi Win32); render offscreen WARP để so pixel | khó (cần UIA/WinAppDriver, không headless) | **tốt nhất**: `Avalonia.Headless` | được | giữ nguyên |
| Độ trưởng thành | Win32/D2D/WIC rất ổn định (OS); phần tự viết nhiều | trẻ, còn lỗi lớn, phụ thuộc runtime ngoài | ổn định, cộng đồng lớn | rất ổn định nhưng cũ | rất ổn định |
| Kích thước phát hành | AOT ước 5-15 MB, không cần runtime | + Windows App Runtime (~30-60 MB nếu tự chứa) | AOT ước 25-45 MB (Skia) | cần .NET runtime | cần .NET Desktop runtime |
| Gói NuGet mới | **không** (dùng generator sẵn trong SDK; CsWin32 là tuỳ chọn) | WindowsAppSDK, CsWinRT | Avalonia (+ Skia native) | không | không |
| Rủi ro chính | công tự viết control/menus/dialog; dark mode của common controls | khởi động có thể **chậm hơn WPF**; đóng gói | thêm một runtime render (Skia) giữa WIC và màn hình (copy thêm một lần) | không AOT -> mất nửa lợi ích | trần ~500 ms |

**Chọn: Win32 + Direct2D cho cửa sổ xem ảnh**, vì: (1) là sàn nhanh nhất đã đo (182 ms, nhanh hơn WPF tối giản 220 ms); (2) AOT
không cần thư viện ngoài; (3) WIC -> `ID2D1Bitmap1` không qua lớp trung gian (Avalonia phải copy sang Skia); (4) ADR 0001 đã chọn WIC
và [Q-FMT-WEBP-HEIC](Q-FMT-WEBP-HEIC.md) không đóng gói codec - giữ nguyên. **Cửa sổ phụ** (Settings 469 dòng XAML + 1257 dòng code,
Recovery, ActionProfiles, Diagnostics, BatchReview, Benchmark, ClickZoomCustom, SkippedFiles): giai đoạn đầu **giữ WPF, nạp muộn**
(chỉ khi người dùng mở, trả ~300-400 ms lúc đó); muốn Native AOT thì phải thay chúng - lựa chọn cho phần này để ở quyết định NW-3
(Win32 common controls tự dựng vs Avalonia cho riêng cửa sổ phụ), quyết bằng spike S3 khi tới đó.

**Đã loại:** WinForms (không AOT, nên không đạt mục tiêu); WinUI 3 (rủi ro khởi động chậm hơn WPF, chính là điều cần tránh; đóng gói
phức tạp; test UI khó); Avalonia cho cửa sổ chính (hợp lý nhưng chậm hơn Win32 ở đúng chỉ số cần cải thiện - vẫn là phương án dự
phòng cho cửa sổ phụ).

## 4. Kiểm kê phụ thuộc WPF (grep 2026-10-10, origin/master `f3f62728`)

Lệnh: `grep -rlE "using System\.Windows|System\.Windows\.Media|BitmapSource|ImageSource|Dispatcher|PresentationFramework"` theo
project (loại `obj/`), rồi đọc tay các file trúng.

| Project | `UseWPF` | File dính WPF / tổng | Dính gì | Kế hoạch tách |
|---|---|---|---|---|
| Core (net10.0) | không | 3/126 - **chỉ trong comment** (`ImmediateUiScheduler`, `ReviewMetrics`, `ShortcutKeyCanonical`) | không | không cần |
| PerfAnalysis (net10.0) | không | 5/7 - chỉ chữ "Dispatcher" trong tên sự kiện | không | không cần |
| Platform.Windows | không | 0/18 | không WPF, nhưng **COM có sẵn + late-bound** (xem dưới) | sửa cho AOT (P2) |
| Imaging | **có** | 16/50 | `BitmapSource` (46 chỗ), `PixelFormats` (21), `BitmapMetadata` (11), `Freeze` (10), `BitmapImage`, `TransformedBitmap`, `BitmapFrame`, `FormatConvertedBitmap`, `BitmapDecoder`, `WriteableBitmap`; encoder `JpegBitmapEncoder` (`PreviewCacheFile`), `PngBitmapEncoder` (`DiskCacheStore`) | **P1** (chính) |
| Imaging.Raw | có | 0/28 trực tiếp (qua kiểu của Imaging) | gián tiếp | bỏ `UseWPF` sau P1 |
| Imaging.LibRaw | có | 1/7 (`RgbBgraResampler`) | tạo `BitmapSource` | P1 |
| Imaging.TurboJpeg | có | 1/5 (`TurboJpegDecoder`) | tạo `BitmapSource` | P1 |
| Benchmarking | có | 0/9 trực tiếp | gián tiếp | P1 |
| App | có | 35/96 `.cs` + 14 XAML (1480 dòng) | View, input WPF, sink, dialog, theme | thay bằng `App.Win32` (P3) |
| tools/Benchmark.Cli | (qua App) | 7/18 (`WpfTestHost`, `LocalUiNextProbe`, ...) | host WPF cho perf session | P3: host mới |
| Localization.Generator | không | 0/3 | - | - |

Chi tiết App (file .cs dính WPF / tổng): ViewModels **0/9**, Coordinators 2/25 (`FullscreenWindowPlacer` + 1), Composition 0/5,
Input 5/9 (`PointerInputController`, `MouseGestures`, `WheelMessageSource`, ...; logic `KineticPan`, `TouchpadGestures` thuần),
Services 10/18 (`WpfPresentationSink`, `WpfImageSurface`, `WpfDialogService`, `WpfFolderPicker`, `WpfClipboardService`,
`WpfKeyNameValidator`, `DispatcherUiScheduler`, `StartupWarmup`, `StartupWindowReveal`, `RenderFrameTrace`), gốc: `MainWindow.xaml.cs`
1139 dòng, `SettingsWindow.xaml.cs` 1257, `App.xaml.cs` 516, `WindowPlacementService` 234, Recovery 363+249, Benchmark 434, ...

**Seam đã có (giảm công):** `IDecodedImage.PlatformImage` là `object` (Imaging không ép kiểu trả về là WPF ở giao diện),
`IImageSurface`, `IPresentationSink`, `IDialogHost`, `IClipboardService`, `IUiScheduler`, `ViewportSizeSource`, `IPresentationObserver`;
ADR 0005 (UI-thread affinity) và K-2 (ViewModel độc lập WPF) đã được test kiến trúc khoá.

**Chặn Native AOT ngoài WPF (phải sửa dù chọn UI nào):**
- `[ComImport]` (COM có sẵn của runtime, **không chạy dưới Native AOT**): `Imaging/Decoding/Wic/WicInterop.cs` (12 interface),
  `WicCodecAvailability.cs`, `Platform.Windows/ComEnumeration.cs`.
- Late-bound COM: `Type.GetTypeFromProgID("Shell.Application")` + `InvokeMember` (`ExplorerOrderService`), `dynamic`
  (`WindowsRecycleBin`, 5 chỗ) + `Microsoft.VisualBasic.FileIO` -> viết lại bằng `IShellWindows`/`IShellBrowser`/`IFolderView`
  (vtable) và `IFileOperation`.
- `System.Text.Json`: 29 lời gọi Serialize/Deserialize, chỉ `AppSettingsJsonContext` dùng source generation -> chuyển hết sang
  `JsonSerializerContext` (journal, session, placement, export/import, ...).
- `Microsoft.Extensions.DependencyInjection` + `CommunityToolkit.Mvvm`: chạy được dưới AOT (generator), cần kiểm cảnh báo trimming.
- Phím tắt lưu bằng **tên `System.Windows.Input.Key`** (`ShortcutKeyCanonical`, `WpfKeyNameValidator`): bản mới cần bảng ánh xạ
  tên WPF Key <-> mã VK để đọc đúng `config.json` cũ (không đổi định dạng).

## 5. Spike S1 (đầu tiên) - tiêu chí go/no-go

**Mục tiêu:** chứng minh bằng số rằng stack đã chọn, biên dịch **Native AOT**, mở 1 JPEG trong F4 nhanh hơn PhotoReview hiện tại
đủ nhiều để đáng 20+ người-tuần.

**Phạm vi (dự án tạm trong scratchpad, không commit):** dựng từ viewer `w32gen` đã viết hôm nay (Win32 + `LibraryImport` +
`[GeneratedComInterface]` WIC + `UnmanagedCallersOnly` WndProc) và thêm: (a) Direct2D 1.1 device context trên swap chain DXGI
flip-model (`DXGI_SWAP_EFFECT_FLIP_DISCARD`), bitmap `ID2D1Bitmap1` tạo từ pixel WIC PBGRA; (b) đọc `config.json` thật bằng
`AppSettingsJsonContext` (để tính chi phí runtime JSON); (c) liệt kê thư mục F4 (2867 file) song song; (d) zoom 100 % bằng con lăn,
pan bằng kéo (đo thời gian khung khi pan); (e) điều hướng Next/Prev trong cache RAM 40 ảnh (đo key->present).

**Đo:** 3 biến thể x 10 lần xen kẽ, cùng probe/cùng file: S1-AOT, S1-R2R, PhotoReview publish R2R (và bản build Explorer đang dùng).
Mốc: cửa sổ thấy được, ảnh đầu vẽ xong (`Present` trả về + `DwmFlush`), working set lúc ảnh đầu, kích thước thư mục phát hành.

| # | Tiêu chí GO (tất cả phải đạt) | Ngưỡng |
|---|---|---|
| G0.1 | Ảnh đầu (S1-AOT), median | ≤ **200 ms** |
| G0.2 | Nhanh hơn PhotoReview publish hiện tại | ≥ **700 ms** (median) |
| G0.3 | AOT nhanh hơn R2R cùng mã | ≥ **40 ms** - nếu không đạt: đi tiếp nhưng **bỏ mục tiêu AOT** (dừng ở bản "lai" R2R) |
| G0.4 | Analyzer AOT/trim | 0 cảnh báo IL2xxx/IL3xxx trong mã spike |
| G0.5 | Chuyển ảnh trong cache (key -> present, D2D), P95 | ≤ **3 ms** (không tệ hơn WPF hiện tại 2,5-8 ms) |
| G0.6 | Pan ảnh 100 %, khung > 16,7 ms | ≤ 1 % khung |
| G0.7 | Kích thước bản AOT | ≤ 20 MB |

NO-GO nếu G0.1 hoặc G0.2 trượt -> ghi kết quả, dừng chuyển đổi, làm tiếp P-1 (WPF nhanh) thôi.
**Thời gian:** 3-5 ngày công (2 ngày D2D/swap chain, 1 ngày đo + viết). **Chi phí:** cài VS C++ workload + Windows SDK (mục 9),
~4-6 GB đĩa (ổ C: còn **14 GB trống**, xem rủi ro).

## 6. Kế hoạch theo giai đoạn

Giả định công: 1 người (chủ dự án) + AI agent, quen .NET, chưa quen Direct2D; "người-tuần" = 5 ngày làm việc tập trung kể cả test và
review; dải = lạc quan - thận trọng. Mọi giai đoạn là PR riêng vào `master`, CI xanh, 0 cảnh báo, test UI không bị bỏ.

### P-1 (khuyến nghị làm ngay, độc lập với quyết định bỏ WPF): "WPF nhanh" - 1-3 người-tuần

- **Mục tiêu:** ảnh đầu ≤ 600 ms (từ 1054/1182 ms), không đổi hành vi.
- **Phạm vi:** (1) quyết định P-STARTUP B (Explorer chạy bản publish R2R, -130 ms đo); (2) decode ảnh khởi chạy bắt đầu từ
  `App_Startup`/`Main` với hộp = vùng làm việc của màn hình đã lưu trong placement (hiện bắt đầu ở 770 ms) - chồng lên XAML/render thread;
  (3) đọc settings chồng lên `ConnectRenderThread` thay vì UI chờ (`settingsLoaded` -66..-96 ms); (4) nạp muộn phần XAML không cần ở khung
  đầu (theme menu/scrollbar, toolbar con); (5) resolve đồ thị `MainViewModel` lười cho service không dùng ở khung đầu.
- **Hoàn thành khi:** lô xen kẽ ≥ 8 lần (probe như mục 2) cho median `RenderedFrame` ≤ 600 ms; test StartupFirstShow* vẫn xanh,
  mutation check các nhánh mới; T89/Fit không đổi.
- **Dùng lại cho bản Win32:** (2), (3), (5) là logic ở App/Core, chuyển nguyên sang bản mới.

### P0: Toolchain + Spike S1 - 1 người-tuần (sau khi người dùng cho phép cài, mục 9)

Tiêu chí: mục 5. Kết quả ghi vào một file mới trong thư mục `perf/` (tên `<ngày>-no-wpf-spike`).

### P1: Tách Imaging khỏi WPF - 2-3 người-tuần

- **Mục tiêu:** Imaging/Raw/LibRaw/TurboJpeg/Benchmarking không còn `UseWPF`; WPF app vẫn chạy y như cũ.
- **Phạm vi:** kiểu mới `PixelBuffer` (Imaging, không phụ thuộc UI: BGRA32 premultiplied hoặc BGR32, stride, w/h, bộ nhớ native
  `NativeMemory.AlignedAlloc`, đếm tham chiếu/Dispose, `EstimatedBytes`) làm `IDecodedImage.PlatformImage`. `WicDirectDecoder` copy pixel
  WIC thẳng vào `PixelBuffer` (hiện `BitmapSource.Create` copy một lần - giữ đúng một lần copy). `PreviewCacheFile`/`DiskCacheStore`
  dùng encoder WIC (`IWICBitmapEncoder` JPEG chất lượng giữ `DefaultJpegQuality`, PNG) - **định dạng file cache không đổi**.
  `WpfExifReader`/`ExifQueryInterpreter` -> `IWICMetadataQueryReader` (đã có trong `WicInterop`). `WpfBitmapImageDecoder` (fallback INV-12)
  chuyển sang project App WPF làm adapter; bản mới dùng fallback WIC thứ hai (`WICConvertBitmapSource`) - cần quyết định NW-4.
  Adapter `PixelBuffer -> BitmapSource` (một `WriteableBitmap`/`BitmapSource.Create` trên luồng decode, `Freeze`) nằm trong App WPF.
- **Hoàn thành khi:** `Imaging.csproj` không có `UseWPF`; test kiến trúc mới cấm `System.Windows` trong Imaging*; toàn bộ Imaging.Tests
  (2992) + quality gate (QG-1..6) xanh với tham chiếu pixel từ WIC; perf gate `tune-matrix` S2/S3/S4 (Repeat ≥ 8): P50 và P95 không tệ hơn
  baseline quá nhiễu A/A; RAM peak F4 không tăng > 5 %.
- **Rủi ro:** thêm một lần copy nếu adapter làm sai (đo bằng `ReviewMetrics`); vòng đời bộ nhớ native (rò rỉ) -> test đếm buffer sống.

### P2: Interop sẵn sàng AOT - 2-3 người-tuần (song song được với P3)

- `[ComImport]` -> `[GeneratedComInterface]` + `StrategyBasedComWrappers` (WIC, MF probe, shell); `ExplorerOrderService` viết lại bằng
  `IShellWindows` (vtable) + `IFolderView::Items` (đã dùng) ; `WindowsRecycleBin` bỏ `dynamic`/VB FileIO, dùng `IFileOperation`
  (`FOFX_RECYCLEONDELETE`) - **chỉ test bằng fake** (quy tắc Recycle Bin trong AGENTS.md), Native test chỉ xoá item của chính nó.
- JSON: mọi serializer qua `JsonSerializerContext`; bật `IsAotCompatible=true` cho Core/Imaging*/Platform.Windows -> 0 cảnh báo.
- **Hoàn thành khi:** analyzer AOT 0 cảnh báo; fault-injection + journal tests xanh; Explorer order đo lại trên F4 (~60 ms như hiện tại).

### P3: `PhotoReview.App.Win32` - cửa sổ xem ảnh - 6-9 người-tuần

Project mới, tham chiếu Core/Imaging*/Platform.Windows và **dùng lại ViewModels/Coordinators/Composition** (tách khỏi App WPF thành
project `PhotoReview.App.Shared` net10.0-windows không WPF - việc dời file, không viết lại). Hạng mục và ước lượng:

| Hạng mục | Ước (ngày) | Ghi chú / rủi ro |
|---|---:|---|
| Vòng đời cửa sổ, message loop, `IUiScheduler` trên Win32 (`PostMessage` + hàng đợi), ADR 0005 giữ nguyên | 3-4 | thay `DispatcherUiScheduler`; continuation `await` cần `SynchronizationContext` tự viết |
| Renderer D2D: swap chain, device lost (`D2DERR_RECREATE_TARGET`), vẽ ảnh Fit/zoom, nội suy HighQuality/Linear (map `ScalingQuality`) | 5-7 | ảnh > 16384 px phải chia tile (giới hạn bitmap D2D) |
| Zoom theo pixel nguồn (ADR 0008), Fit/FitWidth/FitWidth2/FitHeight, T89 | 4-6 | **rủi ro cao**: quy tắc "không ApplyFitViewAsync single-pass khi chưa có bằng chứng T89" - phải port test trước |
| Pan, kinetic (Q-R40), con lăn, click-zoom, chuột giữa, touchpad 2 ngón (WM_POINTER/`WM_GESTURE` hoặc DirectManipulation) | 5-7 | logic thuần đã có (`KineticPan`, `TouchpadGestures`), phần nhận input viết lại |
| Overlay/HUD/thông tin EXIF/toast, toolbar tự ẩn, crossfade (Q-R37) bằng DirectWrite + D2D | 4-6 | font tiếng Việt, ClearType; không còn layout engine |
| Menu chuột phải tuỳ biến (CTX-MENU-CUSTOMIZE) | 2-3 | `TrackPopupMenu` native: dark mode không chính thức (uxtheme ordinal) hoặc menu tự vẽ |
| Phím tắt + action profiles, ánh xạ tên WPF Key <-> VK | 2-3 | giữ định dạng `config.json` |
| Placement trước khi hiện, cloak tới khung đầu, F11, đa màn hình, Per-Monitor V2 DPI, title bar tối, nhãn instance | 3-4 | logic `WindowPlacementService`/`FullscreenWindowPlacer` dùng lại |
| Kéo-thả file/thư mục, clipboard (Copy name/path), mở bằng editor ngoài, single-instance forward | 2-3 | Platform.Windows có sẵn phần lớn |
| Compare view, batch review | 3-5 | |
| Host cửa sổ phụ WPF nạp muộn (Settings, Recovery, ...) với owner = HWND Win32 | 2-3 | **rủi ro trung bình**: vòng lặp message lồng (`ShowDialog`), phím tắt, focus |
| Perf trace (`PhotoReviewPerf` các mốc giống WPF để so), `Benchmark.Cli` host mới | 2-3 | cần cho mọi gate sau |

- **Hoàn thành khi:** tính năng bằng bản WPF theo checklist (mục 3 của [RUST-MIGRATION-EVAL](RUST-MIGRATION-EVAL.md) liệt kê đủ);
  ảnh đầu (R2R) ≤ 450 ms median; S2/S3/S4 P50/P95 không tệ hơn WPF; T89/Fit test xanh; người dùng xem bằng mắt (zoom, pan, F11, đa màn hình,
  touchpad, menu, dark theme).

### P4: Phát hành song song (beta "lai", R2R) - 1-2 người-tuần

- Hai exe trong cùng thư mục phát hành: `PhotoReview.App.exe` (WPF, như cũ) và `PhotoReview.exe` (Win32 + cửa sổ phụ WPF nạp muộn).
  Cùng `%LOCALAPPDATA%\PhotoReview` (config, journal, Sessions, cache, thumbnails, window-placement) - **không migrate gì**, vì định dạng
  không đổi (bất biến P1). Kiểm tra bằng test: bản mới đọc config/journal/session do bản cũ ghi và ngược lại (round-trip).
- Single-instance: trong beta dùng **tên mutex/pipe khác** cho bản mới (tránh hai giao thức forward lệch phiên bản - [SEC-02](SEC-02-forward-protocol-review.md));
  journal dùng chung đã an toàn đa tiến trình (Q-R27 marker, P02 optimistic append).
- Registry: liên kết `.jpg` (HKCU `Applications\...`) và lệnh "Browse with PhotoReview" trỏ vào exe nào là **người dùng chọn**
  (nút "Cập nhật đường dẫn" đã có). Rollback = trỏ lại `PhotoReview.App.exe`.
- **Hoàn thành khi:** người dùng dùng bản mới ≥ 2 tuần, không lỗi mất dữ liệu; ảnh đầu đo trên máy người dùng.

### P5: Thay cửa sổ phụ, bật Native AOT - 5-8 người-tuần

- Theo lựa chọn NW-3 (Win32 common controls v6 + dark mode tự vẽ, hoặc Avalonia chỉ cho cửa sổ phụ). Settings là phần lớn nhất
  (~60 setting, tab, combo, slider, danh sách phím tắt, export/import, ngôn ngữ, tích hợp Explorer).
- Bật `PublishAot=true` cho `PhotoReview.exe`; CI publish AOT (runner `windows-latest` có MSVC); `verify-release.ps1` kiểm artifact AOT.
- **Hoàn thành khi:** 0 cảnh báo AOT; ảnh đầu AOT ≤ 350 ms median trên máy này; mọi test xanh.

### P6: Gỡ WPF - 1-2 người-tuần

- Xoá `PhotoReview.App` (WPF) sau ≥ 2 bản phát hành ổn định; ADR mới thay [ADR 0002](../../adr/0002-ui-framework.md); cập nhật
  architecture.md, APP-MECHANISMS-VI.md, TESTING.md, MUTATION-TESTING.md (Stryker chạy được trên App không WPF mà không cần mẹo hiện tại).

### Tổng công

| Đường | Giai đoạn | Người-tuần |
|---|---|---:|
| Chỉ tối ưu WPF | P-1 | 1-3 |
| Bản "lai" R2R (cửa sổ chính Win32, cửa sổ phụ WPF nạp muộn) | P0 + P1 + P2 (một phần) + P3 + P4 + test | 12-19 |
| Native AOT hoàn toàn | + P2 đủ + P5 + P6 | 21-33 |

## 7. Chuyển test

| Loại test hiện có | Số (CI 2026-10-10) | Bản mới | Công |
|---|---:|---|---|
| Core.Tests | 2918 | giữ nguyên (net10.0, không WPF) | 0 |
| Imaging.Tests | 2992 | giữ; ~40 file dùng `BitmapSource` trong helper/assert -> `PixelBuffer`; quality gate so với tham chiếu WIC thay vì WPF | 1-1,5 tuần (trong P1) |
| Architecture.Tests | 65 | thêm luật: Imaging*/App.Shared/App.Win32 cấm `System.Windows`; AOT-compatible; ADR 0005 quét cả App.Win32 | 2-3 ngày |
| App.Tests | 2238 | ViewModels/Coordinators/Composition giữ (dời sang test của App.Shared); ~41 file dính WPF (sink, surface, dialog, input) viết lại trên interface Win32 tương ứng | 1,5-2 tuần |
| Integration.Tests | 1113 (+15 Slow) | 45 file `Category=UI` mở MainWindow WPF thật -> harness mới: HWND thật trên desktop ẩn (`run-tests-hidden.ps1` dùng được), gửi input bằng `PostMessage` vào chính cửa sổ (không SendInput), đọc kết quả qua trạng thái ViewModel + render offscreen (D2D WARP) để so pixel; StartupFirstShow, T89/Fit, F11, crossfade viết lại trước khi port tính năng | 2-3 tuần |
| Native/Manual/RAW corpus | - | không đổi | 0 |
| Mutation (Stryker) | baseline [MUTATION-TESTING](../../MUTATION-TESTING.md) | chạy lại cho Imaging sau P1, App.Win32 sau P3; không cần mẹo NETSDK1022 của App WPF | theo vùng |

Trong P1-P4, **cả hai app chạy chung bộ test Core/Imaging**; test UI của bản WPF giữ tới P6.

## 8. CI, phát hành, rollback, rủi ro

- **CI** (`.github/workflows/ci.yml`, `windows-latest`): thêm job build/test App.Win32; từ P5 job `dotnet publish -p:PublishAot=true`
  (runner có VS C++ - không cần cài thêm trên CI); release.yml đóng gói cả hai exe tới P6.
- **Phiên bản:** giữ `2.0.N` tự động; bản Win32 cùng số phiên bản.
- **Rollback:** mọi giai đoạn trước P6 giữ bản WPF chạy được với cùng dữ liệu; rollback = đổi liên kết Explorer về exe cũ.

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| Lợi ích thật nhỏ hơn ước (cả app chậm hơn sàn nhiều) | TB | Gate G0 + đo sau P3 với ngưỡng 450 ms; dừng ở P-1 nếu không đạt |
| Hồi quy zoom/Fit/T89, kinetic, touchpad (đã tinh chỉnh nhiều vòng) | Cao | port test T89/Fit trước, so sánh khung hình với bản WPF cùng input |
| Dữ liệu người dùng (journal/config/session) | Thấp | định dạng không đổi; test round-trip hai chiều; file action dùng chung Core |
| Recycle Bin khi viết lại bằng `IFileOperation` | TB | chỉ fake + Native test tự dọn (AGENTS.md); không mutation trên đường xoá thật |
| Ảnh > 16384 px khi zoom gốc (giới hạn D2D) | TB | tile; test với ảnh panorama tổng hợp |
| GPU/driver: device lost, máy không GPU (RDP) | TB | D2D tự rơi về WARP; test WARP trong CI |
| Dark mode menu/control Win32 không chính thức | TB | menu tự vẽ bằng D2D nếu cần |
| Bảo trì hai UI song song (P3-P6) | Cao | đóng băng tính năng UI mới trên bản WPF từ P3, chỉ sửa lỗi |
| Ổ C: chỉ còn 14 GB (98 % đầy) khi cài toolchain | TB | dọn `work/diag/tune-runs`, cài vào ổ khác nếu có |
| Năng lực Direct2D/Win32 của người duy trì | TB | spike S1 là bài kiểm tra; tài liệu hoá renderer nhỏ gọn |

## 9. Spike cần người dùng cho phép (chưa cài gì)

| Thứ cần | Nguồn chính thức | Dung lượng ước | Dùng cho |
|---|---|---|---|
| Visual Studio 2026 Community (đã cài, **thiếu** workload) -> thành phần "MSVC v14x - x64/x86 build tools" (hoặc cả workload "Desktop development with C++") | Visual Studio Installer có sẵn trên máy (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`), Microsoft | ~2-3 GB (chỉ MSVC x64) / ~6-8 GB (cả workload) | `link.exe` cho Native AOT (đã xác minh: `dotnet publish -p:PublishAot=true` báo "Platform linker not found") |
| Windows 11 SDK (10.0.26100) | cùng Installer, hoặc developer.microsoft.com/windows/downloads/windows-sdk | ~1,5-2,5 GB | thư viện `kernel32.lib`... cho link; header D2D/DXGI không cần (interop C#) |
| (Tuỳ chọn, chỉ khi NW-3 = Avalonia) gói NuGet Avalonia 11.x + Avalonia.Headless | nuget.org (AvaloniaUI) | ~60-100 MB cache | spike S3 cửa sổ phụ |

Máy hiện có: .NET SDK 10.0.401, runtime 10.0.12, gói `Microsoft.DotNet.ILCompiler` 10.0.12 và `crossgen2` trong NuGet cache
(R2R build được offline), VS 2026 Community **không** có `VC\Tools\MSVC`, **không** có `Windows Kits\10`; không có `cl`/`clang`.

## 10. Quyết định người dùng cần chốt

**NW-1 - Có làm P-1 (WPF nhanh) trước không?**
Hiện trạng: ảnh đầu 1054-1182 ms, trong đó ~650 ms là do thiết kế khởi động sửa được trong WPF.
- (a) Làm P-1 ngay, song song spike S1. Ưu: lợi ích ~450-550 ms (ước) sau 1-3 tuần, rủi ro thấp, mã dùng lại cho bản Win32. Nhược: thêm
  việc trên mã WPF sẽ bị thay; phải test StartupFirstShow lại.
- (b) Bỏ qua, dồn sức vào Win32. Ưu: không làm việc "bỏ đi". Nhược: người dùng chờ 3-5 tháng mới thấy nhanh hơn; không có mốc so sánh WPF-tối-ưu
  để biết bỏ WPF có đáng không.
- **Khuyến nghị: (a)**. Sau khi chọn: mở PR P-1 với 5 hạng mục mục 6, đo theo mục 2.

**NW-2 - Cho phép cài toolchain AOT để chạy spike S1?** (mục 9)
- (a) Cài chỉ "MSVC x64 build tools" + Windows 11 SDK (~4-5 GB). Ưu: đủ cho AOT, nhỏ nhất. Nhược: ổ C: còn 14 GB.
- (b) Cài cả workload "Desktop development with C++" (~6-8 GB). Ưu: đủ cho mọi thứ C++ sau này (kể cả so sánh C++/Rust). Nhược: tốn đĩa hơn.
- (c) Không cài; spike chạy trên CI (runner có MSVC), đo khởi động trên runner. Ưu: không đụng máy. Nhược: runner ảo, số đo không đại diện máy người dùng, không có F4.
- **Khuyến nghị: (a)**, sau khi dọn `work/diag/tune-runs`. Sau khi chọn: người dùng tự cài qua Visual Studio Installer (agent không cài);
  agent dựng S1 và báo theo bảng G0.

**NW-3 - Cửa sổ phụ khi tiến tới AOT (P5):**
- (a) Win32 common controls v6 + dark mode tự xử lý. Ưu: không thư viện ngoài, khởi động cửa sổ phụ nhanh, AOT nhỏ. Nhược: công lớn nhất
  (Settings), dark mode control không chính thức, khó test.
- (b) Avalonia chỉ cho cửa sổ phụ (nạp muộn, AOT được). Ưu: XAML gần WPF -> port Settings nhanh, test headless. Nhược: thêm Skia (~20-30 MB),
  hai hệ UI trong một tiến trình.
- (c) Giữ WPF nạp muộn mãi (không AOT). Ưu: không công thêm. Nhược: mất phần lợi của AOT (G0.3 đo bao nhiêu).
- **Khuyến nghị:** quyết sau spike S1 - nếu G0.3 < 40 ms chọn (c); nếu không, làm spike S3 nhỏ (port tab "Chung" của Settings theo (a) và (b),
  so công và thời gian mở) rồi chọn.

**NW-4 - Fallback decoder khi bỏ WPF (INV-12: WIC lỗi -> WPF):**
- (a) Fallback sang đường WIC thứ hai (`WICConvertBitmapSource` + decoder mặc định). Ưu: không WPF. Nhược: WPF cũng dùng WIC bên dưới, nên
  phần lớn lỗi WIC Direct cũng lỗi ở đây; phải có corpus ảnh lỗi để chứng minh không mất ảnh xem được.
- (b) Fallback sang TurboJpeg cho JPEG, WIC thứ hai cho định dạng khác. Ưu: decoder độc lập thật cho JPEG. Nhược: thêm phụ thuộc vào đường fallback.
- **Khuyến nghị: (b)**; kiểm bằng `DecoderQualityGateTests` + fuzz hiện có.

**NW-5 - Điều kiện dừng:**
- (a) Dừng ở bản "lai" R2R nếu ảnh đầu ≤ 450 ms. (b) Luôn đi tới AOT. (c) Dừng sau P-1 nếu ≤ 600 ms và người dùng hài lòng.
- **Khuyến nghị: (c) rồi (a)** - mỗi cổng hỏi lại người dùng với số đo thật.

Sau khi người dùng chốt, ghi kết quả vào frontmatter `summary` của file này (OPEN-DECISIONS tự sinh lại).
