---
id: NOWPF-WP01
order: 212
summary: |-
  WP-01 (đợt 0 của NO-WPF-EXEC-PLAN, 2026-10-10): chủ dự án chốt NE-1 (a) làm bản lai R2R không chờ spike S1, NE-2 (a) project PhotoReview.Shell.* + exe PhotoReview.exe (bản WPF giữ PhotoReview.App.exe), NE-5 (a) cửa sổ phụ cùng tiến trình qua PhotoReview.App.WpfWindows nạp muộn, NE-8 (b) fallback TurboJpeg cho JPEG + WIC thứ hai; NE-3/4/6/7/9 tạm theo (a), chốt trước đợt 3. Tạo 5 project src + 2 project test, khai báo hợp đồng C-01..C-18 (trừ C-07) không logic, khoá chữ ký bằng ContractSurfaceTests (file duyệt contracts.v1.txt sinh từ reflection) + luật L-SHELL/L-SHARED/L-AOT/L-AFFINITY; app WPF không đổi hành vi.
---

# NOWPF-WP01-SCAFFOLD - khung project + hợp đồng v1 + test kiến trúc (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 2, 3, 5), thẻ WP-01 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).

## 1. Quyết định của chủ dự án (2026-10-10)

| NE | Chốt | Ghi chú |
|---|---|---|
| NE-1 | **(a)** bắt đầu đợt 0-4 ngay (bản lai R2R), không chờ spike S1 | S1 (WP-32) chỉ quyết đợt 5 |
| NE-2 | **(a)** `PhotoReview.Shell.*`, exe `PhotoReview.exe`; bản WPF giữ `PhotoReview.App.exe` | `AssemblyName=PhotoReview` trong `Shell.Win32` |
| NE-5 | **(a)** cửa sổ phụ cùng tiến trình, nạp WPF muộn qua library `PhotoReview.App.WpfWindows` (WP-19b) | `Shell.WpfBridge` là project Shell.* duy nhất có WPF |
| NE-8 | **(b)** fallback decoder bản Win32: TurboJpeg cho JPEG, đường WIC thứ hai cho định dạng khác | = NW-4; bản WPF giữ fallback WPF |
| NE-3, NE-4, NE-6, NE-7, NE-9 | **chưa chốt** - mặc định tạm = khuyến nghị (a) của kế hoạch | **chờ chốt trước đợt 3** |

## 2. Đã tạo

Project mới (đều trong `PhotoReview.slnx`, Release 0 cảnh báo):

| Project | Loại | Nội dung WP-01 |
|---|---|---|
| `src/PhotoReview.App.Shared` | library, `IsAotCompatible`, không WPF | C-05, C-06 (`KeyId` sinh một lần từ WPF `Key`), C-08, C-12 (model), C-13 (`IZoomPromptService`), C-14, C-16 (shared) + 2 kiểu dời sớm (mục 3) |
| `src/PhotoReview.Shell.Interop` | library, `IsAotCompatible`, `AllowUnsafeBlocks` | `LibraryImport` user32/kernel32/gdi32 tối thiểu cho cửa sổ khởi động (internal) |
| `src/PhotoReview.Shell.Rendering` | library, `IsAotCompatible` | C-09, C-10, C-11 phần text |
| `src/PhotoReview.Shell.Win32` | WinExe `PhotoReview.exe`, `IsAotCompatible`, `app.manifest` PerMonitorV2 + comctl32 v6 | C-11 overlay, C-12 host, C-15, C-16 (`ShellStartupContext`); `BootstrapWindow` = cửa sổ trống, `--smoke` hiện (không kích hoạt) rồi thoát 0 |
| `src/PhotoReview.Shell.WpfBridge` | library `UseWPF` | C-13 `ISecondaryWindowHost`, `SecondaryWindowHostFactory` |
| `tests/PhotoReview.Shell.Tests` | xUnit, không WPF | tên assembly `PhotoReview`; manifest nhúng (đọc RT_MANIFEST) có PerMonitorV2 + comctl32 6.0 |
| `tests/PhotoReview.Shell.Integration.Tests` | xUnit, `Category=UI` | smoke: chạy `PhotoReview.exe --smoke` thật, exit 0 trong 60 s |

File hợp đồng trong project có sẵn: Core `Abstractions/IUiDispatcher.cs` (C-04), `Diagnostics/ShellPerfMarks.cs` (C-18); Imaging
`Pixels/PixelBuffer.cs`, `Pixels/PixelOps.cs`, `Decoding/{IPlatformImageCodec,PixelLease,PixelBufferImageCodec,DecodedImage}.cs` (C-01..C-03);
TestSupport `Golden/GoldenContracts.cs` (C-17). Mọi thân phương thức ném `NotImplementedException` (trừ `RectD.Right/Bottom` như bảng).

Test kiến trúc (Architecture.Tests, reflection trên assembly đã build, không đọc source):

- `ContractSurfaceTests.Contracts_MatchApprovedSurface` (L-CONTRACT) so với `Approved/contracts.v1.txt`; file duyệt sinh bằng test
  `WriteApprovedSurface` (`Category=Manual`, chỉ lead chạy). `ContractSurfaceFormatterTests` ghim cách viết chữ ký (nullability,
  tên tuple, giá trị mặc định, in/out, static abstract, init).
- `ShellRulesTests`: L-SHELL (Shell.Interop/Rendering/Win32 không tham chiếu WPF/WinForms/App/WpfWindows/Imaging.Wpf; chỉ kiểu
  `Dialogs.WpfBridgeLoader` được phụ thuộc `Shell.WpfBridge`), L-SHARED, L-AOT (metadata `IsTrimmable`, không `[ComImport]`,
  P/Invoke chỉ qua `LibraryImport`, không `Microsoft.CSharp`).
- `ContractMirrorTests`: `KeyId`/`PointerButton`/`KeyModifiers` = WPF `Key`/`MouseButton`/`ModifierKeys` (tên + giá trị, cả bí danh);
  tên `UiPriority` có trong `DispatcherPriority`.
- `AppThreadAffinityTests` (ADR 0005) quét thêm `App.Shared`, `Shell.Win32`, `Shell.WpfBridge`.

CI (`ci.yml`), `tools/verify-all.ps1`, `tools/coverage.ps1` chạy thêm 2 project test (cùng `TEST_FILTER`, có trong bước Integration+Slow).

## 3. Lệch khỏi kế hoạch (đã sửa kế hoạch trong cùng PR nếu cần)

1. **App tham chiếu App.Shared ngay ở WP-01; dời sớm 2 kiểu thuần** `ViewerStretchMode` (tách khỏi `ViewerState.cs`) và
   `IPresentationSink` (namespace giữ nguyên). Lý do: C-08 (`ViewportInput.Stretch`) và C-16 (`IPresentationSinkFactory`) dùng chúng,
   App.Shared không được tham chiếu App. Không đổi hành vi (chỉ dời khai báo). WP-09 dời phần còn lại.
2. **`Shell.Rendering` tham chiếu `App.Shared`** (kế hoạch 3.2 ghi Rendering -> Imaging, Core): C-09/C-11 dùng `PointD/RectD/SizeD`
   (C-06) nằm ở App.Shared. Đã sửa mục 3.2. Nếu sau này muốn Rendering nhẹ hơn: dời 3 struct sang assembly nhỏ hơn = đổi hợp đồng (lead).
3. **Không khoá trong v1:** C-07 (`IImageSurface`/`IFitSurface`/`ViewportSnapshot` là seam có sẵn, WP-07 sửa tại chỗ), phần thuần của
   `ExifOrientation` (C-03; `Normalize` chưa có, các thành viên WPF dời ở WP-03), `IClipboardService` (C-13, "dời" ở WP-09). Chúng vào
   file duyệt ở v1.1 do gói sở hữu đề xuất qua lead. `IDialogService`/`IFolderPicker` "giữ nguyên" nên không khoá.
4. **L-AFFINITY và L-SHARED bật ngay** (kế hoạch: WP-09/WP-14) vì rẻ và đúng ngay từ đầu; L-SHARED kiểm theo tên assembly tham chiếu
   (không theo namespace `System.Windows`, vì `ICommand` của MVVM nằm ở `System.Windows.Input` nhưng không phải WPF).
5. **L-AOT không áp cho `Shell.WpfBridge`** (WPF không AOT được); áp cho App.Shared, Shell.Interop, Shell.Rendering, Shell.Win32.
6. **L-SHELL "đúng một file"** kiểm theo kiểu (`PhotoReview.Shell.Win32.Dialogs.WpfBridgeLoader`), không theo tên file (không test source).
7. **C-17 `ViewportInputDto`/`ViewportLayoutDto`** do WP-01 định nghĩa (kế hoạch chỉ ghi "bản phẳng"): enum ghi bằng tên, `ImageRect`
   tách `ImageX/ImageY/ImageWidth/ImageHeight`. `JsonSerializerContext` (cần `AllowNamedFloatingPointLiterals` cho NaN của Fit) để WP-10.
8. **CA1716** (`IFullscreenController.Exit` trùng từ khoá VB) và **CA1069/CA1720** (`KeyId` có bí danh và phím `Decimal`) tắt bằng
   `#pragma` có lý do một dòng: tên là chữ ký đóng băng / từ vựng WPF.
9. **Smoke test** gắn `Category=UI` **và** `Category=Integration` (luật TEST-OS: test gọi `Process.Start` phải có category opt-out;
   Integration vẫn chạy trong bộ lọc mặc định).
10. **Đã đo:** `PhotoReview.runtimeconfig.json` của shell vẫn kéo framework `Microsoft.WindowsDesktop.App` vì `FrameworkReference` WPF của
    `PhotoReview.Imaging` (còn `UseWPF`) truyền qua App.Shared/Rendering. Không nạp assembly WPF khi chạy, nhưng chỉ hết sau WP-06; perf
    gate đợt 2 cần biết điều này (mục 10 kế hoạch).
11. `IRenderSurface` giữ `static abstract` như bảng (không dùng được làm đối số generic/DI - WP-15 được đề xuất factory qua lead).

## 4. Điều kiện mở đợt 1 (cho lead)

- PR WP-01 merge vào `master` = **hợp đồng v1 đóng băng** ở `tests/PhotoReview.Architecture.Tests/Approved/contracts.v1.txt`
  (C-01..C-06, C-08..C-18; mục 3.3 ở trên là phần chưa khoá).
- Sau merge: lead build Release `PhotoReview.slnx` 0 cảnh báo, `tools/verify-all.ps1 -Hidden` xanh trên `master`.
- Gói đợt 1 (WP-02, WP-07, WP-08, WP-10, WP-11) chỉ thêm phần thực thi; `ContractSurfaceTests` phải xanh mà không sửa file duyệt.
- NE-3/4/6/7/9 chưa cần cho đợt 1-2; phải chốt trước khi giao đợt 3.
