# NO-WPF spike S1 (WP-32): Native AOT cho `PhotoReview.exe` (shell Win32), đo trên máy dev (2026-10-12)

Gói: [NOWPF-WP32](../decisions/NOWPF-WP32-AOT-SPIKE.md). Ngưỡng: bảng G0.1..G0.7 của [NO-WPF-MIGRATION-PLAN](../decisions/NO-WPF-MIGRATION-PLAN.md) mục 5 và
dòng "AOT" của bảng gate đợt 5 trong [NO-WPF-EXEC-PLAN](../decisions/NO-WPF-EXEC-PLAN.md). Base = `origin/master` `5603be1c`. Toolchain: VS Build Tools 2022
(MSVC 14.44.35207 + Windows SDK 10.0.26100, người dùng cài), gói NuGet ILCompiler 10.0.12, .NET SDK 10.0.401.

## Kết luận ngắn

- **Publish được, chạy được, đo được.** Shell Win32 thật (không phải "minimal shell") publish Native AOT ra `PhotoReview.exe` 13,3 MB, smoke thoát mã 0 và
  chạy đủ 3 lô xen kẽ x 10 vòng (ảnh đầu, 12 phím/lần) không lỗi.
- **Ảnh đầu đã vẽ: median 355-390 ms** (3 lô) so với **821-933 ms** của bản R2R cùng commit đo xen kẽ ngay cạnh (-466..-543 ms, -57..-58 %). Máy bận 40-65 %
  CPU do các agent khác nên số tuyệt đối của cả hai bên bị đội lên (R2R lúc máy rảnh: 463-472 ms, `2026-10-12-nowpf-wave3.md`); chỉ so trong cùng lô.
- G0.1 (<= 200 ms) **hụt** (355-390); mục tiêu sau này NW-5 "AOT <= 350 ms" **sát ngưỡng, hụt vài chục ms trên máy bận**; G0.3 (AOT nhanh hơn R2R >= 40 ms)
  **đạt xa**; G0.4 (0 cảnh báo) **hụt**: 80 cảnh báo, toàn bộ ở 2 file của `Platform.Windows` (COM `dynamic`); G0.7 (<= 20 MB): exe 13,3 MB, thư mục 20,0 MB
  nếu bỏ native của WPF (27,9 MB hiện tại vì project còn tham chiếu WindowsDesktop); G0.5/G0.6 không đo được riêng cho AOT (xem dưới).
- **Chặn để AOT hoàn chỉnh**: (1) cửa sổ phụ WPF (cầu `Shell.WpfBridge` nạp muộn bằng `Assembly.Load` không thể có trong AOT) = WP-35; (2) `dynamic` COM ở
  `WindowsRecycleBin` và `ExplorerOrderService` = WP-33; (3) cấu hình publish (xem "Việc phải làm") = WP-34/WP-36.

## Cách tái lập

```powershell
# PATH phải có vswhere (ILCompiler gọi `vswhere.exe` theo tên): không thì link.exe hỏng với "vswhere.exe is not recognized".
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src/PhotoReview.Shell.Win32/PhotoReview.Shell.Win32.csproj -c Release -r win-x64 -p:ShellAot=true -p:TreatWarningsAsErrors=false -o <dir>
# R2R cùng commit: bỏ -p:ShellAot=true, thêm --self-contained false
powershell -File tools/diag/startup-probe.ps1 -SkipBuild -WpfExe <R2R>\PhotoReview.exe -ShellExe <AOT>\PhotoReview.exe -Rounds 10 -Warmup 1 -Seed 11 -OutDir <out>
```

Ở lệnh probe, cột/ứng viên tên `wpf` (baseline) là **bản R2R của shell**, `shell` là **bản AOT** (probe chỉ có hai khe cố định; hai exe cùng là shell Win32).
`-p:ShellAot=true` là công tắc thêm ở csproj của spike (đặt `PublishAot` và `EventSourceSupport` trong csproj, không dùng `-p:PublishAot=true`, xem mục 2).

## 1. Đo: ảnh đầu, cửa sổ, working set, key->frame (xen kẽ, 10 vòng + 1 vòng bỏ mỗi lô)

Phương pháp như [2026-10-12-nowpf-wave3](2026-10-12-nowpf-wave3.md): mỗi lần = tiến trình mới, data root + cache đĩa rỗng, thư mục 10 JPEG ~12 MB ở GIỮA F4, mở file đầu,
PostMessage phím (không SendInput), chỉ số lấy từ CSV perf của app. Lịch xen kẽ, seed 11 / 23 / 37. CPU toàn máy trong lô (median cột "CPU máy %" của probe):
R2R 42 / 50 / 51 %, AOT 40 / 45 / 46 %.

| Chỉ số (median [min-max]) | R2R lô 1 / 2 / 3 | **AOT** lô 1 / 2 / 3 | Chênh median |
|---|---:|---:|---:|
| Ảnh đầu đã vẽ, ms | 821 [668-1000] / 933 [773-1066] / 908 [840-1162] | **355** [327-387] / **390** [301-506] / **389** [380-425] | -466 / -543 / -519 (-57..-58 %) |
| Ảnh đầu P95, ms | 992 / 1058 / 1084 | 379 / 466 / 416 | |
| Cửa sổ hiện (windowShown), ms | 558 / 660 / 620 | 147 / 140 / 150 | -411 / -520 / -470 |
| Cửa sổ thấy được (windowRevealed), ms | 677 / 795 / 765 | 204 / 192 / 211 | -473 / -603 / -554 |
| Working set đỉnh, MB | 162 / 162 / 156 | **116 / 116 / 116** | -28..-29 % |
| key->frame trong cache P50 / P95, ms (n = 120) | 11,4 / 31,7; 11,2 / 33,2; 13,5 / 44,7 | 10,9 / 19,6; 10,8 / 17,0; 12,9 / 19,3 | P50 -0,2..-0,3 (không hồi quy) |

Ghi chú:

- Số R2R ở đây cao hơn 463-472 ms của wave 3 vì máy bận gấp đôi (CPU 40-65 % do nhiều agent cùng chạy; wave 3 là 26-39 %). Chênh AOT - R2R trên máy rảnh chưa đo
  (phải làm lại khi máy yên); số liệu chắc chắn là **thứ tự** (AOT nhanh hơn ở cả 3 lô, mọi vòng) và độ lớn >= 40 ms của G0.3.
- Ảnh đầu của AOT là sàn của shell hiện tại, không phải sàn của AOT: cửa sổ hiện sau ~145 ms, phần còn lại ~210-240 ms là đọc config + giải mã ảnh 24 MP + upload D2D
  (giống R2R). Hạ xuống <= 200 ms cần giảm việc, không phải đổi trình biên dịch.
- key->frame P95 17-20 ms của AOT (R2R 32-45 ms) vẫn > 3 ms (G0.5): cùng nguyên nhân khung đơn lẻ bị GPU Intel chặn (WP21-STALL) cộng tải nền, độc lập với AOT.
  Không kết luận AOT tốt hơn hay xấu hơn về key->frame; P50 như nhau.
- **G0.6 (pan 100 %, khung > 16,7 ms <= 1 %) không đo** cho AOT: cần `PresentationPerfReportTests` (Category=Manual, GPU thật, cửa sổ thật) trên bản AOT, chưa có
  host chạy test đó trong tiến trình AOT. Việc của WP-36 (CI/verify) nếu muốn.
- Lô 0 (loại, không tính): lần publish AOT đầu **không có sự kiện perf nào** (probe báo "timeout waiting for the first frame", CSV chỉ có tiêu đề) dù ứng dụng
  chạy bình thường (cache `.pv4` đã ghi, session đã lưu). Nguyên nhân: `EventSourceSupport` mặc định `false` khi publish AOT, nên `PhotoReviewPerf` thành no-op; bật
  `<EventSourceSupport>true</EventSourceSupport>` (+1,0 MB exe: 12,2 -> 13,3 MB) khắc phục. Đây là điều kiện bắt buộc để `PHOTOREVIEW_PERF_TRACE` và `startup-probe` dùng
  được trên bản AOT; WP-36 phải giữ.

## 2. Kết quả publish và cảnh báo IL2xxx/IL3xxx theo nhóm

Trình tự lỗi gặp khi thử đúng lệnh trong thẻ:

| Bước | Kết quả | Xử lý |
|---|---|---|
| `dotnet publish ... -p:PublishAot=true` (CLI) | **NETSDK1207** "AOT không hỗ trợ target framework" ở `PhotoReview.Localization.Generator` (netstandard2.0): thuộc tính toàn cục chảy vào mọi ProjectReference | đặt `PublishAot` trong csproj của shell (có điều kiện `ShellAot`), không đặt trên CLI |
| publish với `PublishAot` trong csproj, Release | ILC chạy 1 phút 40 s, 80 cảnh báo, **bị coi là lỗi** (`Directory.Build.props` Release: `TreatWarningsAsErrors=true`; ILC: "exited with return value 0, but errors were detected", IL3053) | `-p:TreatWarningsAsErrors=false` cho spike; gói thật phải có 0 cảnh báo (WP-33/34) |
| link | `link.exe` hỏng: "'vswhere.exe' is not recognized" (targets của ILCompiler gọi `vswhere.exe` theo tên, không theo đường dẫn) | thêm thư mục Installer của VS vào PATH (CI `windows-latest` đã có sẵn trong PATH; máy dev thì không) |
| publish xong | `PhotoReview.exe` 13,3 MB, tự chứa (không cần cài .NET runtime); thư mục 27,9 MB không tính `.pdb` (pdb 52,9 MB không phát hành) | xem mục 3 |
| `--smoke` | thoát mã 0 | |

80 cảnh báo, gom theo project / nguyên nhân (không có cảnh báo nào từ DI `Microsoft.Extensions.DependencyInjection`, CommunityToolkit.Mvvm, `System.Text.Json`,
`Enum.Parse`, GeneratedCom WIC, Core, Imaging, App.Shared, Shell.*: ILC không phát hiện gì ở các nhóm đó):

| Nhóm | Project / file | Số cảnh báo | Mã | Nguyên nhân |
|---|---|---:|---|---|
| COM `dynamic` Recycle Bin | `Platform.Windows/WindowsRecycleBin.cs` (`RestoreViaShell`, dòng 137-195) | 70 | 46 IL3050, 23 IL2026, 1 IL2072 (Activator.CreateInstance của `GetTypeFromProgID("Shell.Application")`) | `dynamic` trên `Shell.Application`/`Folder`/`FolderItem` (late-bound IDispatch qua `Microsoft.CSharp` RuntimeBinder + `System.Linq.Expressions`) |
| COM `dynamic` thứ tự Explorer | `Platform.Windows/Explorer/ExplorerOrderService.cs` (`ShellAccess`, `QueryShell`, dòng 112-113, 339) | 8 | 4 IL3050, 2 IL2026, 1 IL2075, 1 IL2072 | `dynamic window.LocationURL`, `Activator.CreateInstance(GetTypeFromProgID(...))` + `GetType().GetMethod` |
| Gom theo assembly | `Microsoft.CSharp.dll`, `System.Linq.Expressions.dll` | 2 | IL3053 | hệ quả của hai nhóm trên (RuntimeBinder bị kéo vào) |

Số lượng trên là **toàn bộ** danh sách việc của WP-33 (theo ILC thật; thay cho analyzer `IsAotCompatible` chưa bật được vì NETSDK1210 ở `net10.0-windows`,
[WP-12](../decisions/NOWPF-WP12-WIC-GENERATED-COM.md)). Sau WP-33, chạy lại đúng lệnh này nên cho 0 cảnh báo ở mã của dự án.

Cầu WPF: `Shell.WpfBridge` / `App.WpfWindows` là ProjectReference của shell nhưng KHÔNG nằm trong `PhotoReview.exe` (map của ILC: 0 phương thức `WpfBridgeLoader`,
`SettingsWindow`, `PresentationFramework`; không có DLL managed nào trong thư mục publish). Hệ quả: `ShowSettings`/Diagnostics/Recovery chạy qua `SecondaryWindowPorts.TryLoad`
chỉ tới nhánh "assembly vắng -> null -> không mở" (**chưa kiểm ở runtime**; nhánh này là hành vi thiết kế của WP-19b khi thiếu file). Ngoài ra FrameworkReference
`Microsoft.WindowsDesktop.App` làm publish AOT kéo thêm 7,8 MB native của WPF (`D3DCompiler_47_cor3`, `wpfgfx_cor3`, `PresentationNative_cor3`, `PenImc_cor3`,
`vcruntime140_cor3`) + `Languages/` vô ích.

## 3. Kích thước (G0.7), MB, không tính `.pdb`

| Bản | `PhotoReview.exe` | Cả thư mục | Ghi chú |
|---|---:|---:|---|
| R2R, `--self-contained false` | 0,16 (+ `PhotoReview.dll` 0,75) | **14,9** (46 file) | cần .NET 10 runtime đã cài; đây là bản `.jpg` hiện dùng (STARTUP-1) |
| AOT (`EventSourceSupport=true`) | **13,3** | **27,9** (31 file) | tự chứa; gồm 7,8 MB native WPF + 0,25 `Languages/` không cần |
| AOT bỏ native WPF | 13,3 | **20,0** | = exe 13,3 + `LibRaw-SOURCE.zip` 3,5 (nghĩa vụ giấy phép) + `turbojpeg.dll` 3,0 + `libraw.dll` 1,15 + ghi chú |
| AOT lần 1 (`EventSourceSupport=false`) | 12,2 | | không dùng được cho perf trace |

G0.7 (<= 20 MB): exe đạt; cả thư mục **sát 20,0 MB** sau khi bỏ WPF, đạt/hụt phụ thuộc có tính `LibRaw-SOURCE.zip` hay không. AOT tự chứa = không phụ thuộc runtime,
so với 174 MB của self-contained R2R composite (STARTUP-1).

## 4. Việc để AOT hoàn chỉnh (đề xuất, chưa thực hiện; xem fragment WP-32)

| Gói | Việc | Ghi chú đo được |
|---|---|---|
| WP-33 | Bỏ `dynamic`/ProgID late-bound ở `WindowsRecycleBin.RestoreViaShell` và `ExplorerOrderService` (vtable `IFileOperation`/`IShellWindows`) | 78 cảnh báo ở 2 file; phải làm trước vì ILC không thể sinh code `dynamic` |
| WP-34 | `IsAotCompatible` + chạy lại publish làm cổng (0 IL2xxx/IL3xxx); giữ `EventSourceSupport=true`; rà `Enum.Parse`/DI sau khi WP-33 xong | analyzer vẫn bị NETSDK1210: dùng chính lệnh publish AOT làm cổng |
| WP-35 | Thay cửa sổ phụ WPF (Settings, Diagnostics, Recovery, ...) | cầu `Assembly.Load` không dùng được trong AOT; không có cửa sổ phụ thì AOT chạy nhưng thiếu tính năng |
| WP-36 | `PublishAot`/`EventSourceSupport` trong csproj; bỏ `FrameworkReference WindowsDesktop` khỏi bản AOT (WpfBridge/WpfWindows ra khỏi graph) để mất 7,8 MB native; CI windows-latest publish AOT; script/CI thêm `vswhere` vào PATH khi cần; `verify-release.ps1` kiểm artifact AOT | ILC cold ~1,7 phút, tăng dần ~20 s |
| Đo lại | A/B R2R vs AOT lúc máy rảnh (< 15 % CPU), pan G0.6 trên AOT, Settings/Diagnostics sau WP-35 | số hiện tại bận 40-65 % |
