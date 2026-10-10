# P-1 "WPF nhanh": mở ảnh từ Explorer, đo từng bước, thí nghiệm triển khai và đường găng còn lại (2026-10-10)

Nhánh `perf/p1-startup-in-wpf` (base `f3f62728` = 2.0.383, commit mã `4e6d5e55`). Quyết định còn mở: [`P1-STARTUP-IN-WPF`](../decisions/P1-STARTUP-IN-WPF.md).
Bối cảnh: giai đoạn P-1 và cổng NW-5 của kế hoạch `decisions/NO-WPF-MIGRATION-PLAN.md` (PR #395 trên `master`): mục tiêu ảnh đầu <= 600 ms, publish R2R.
Tiền đề: [`2026-10-10-startup-first-image.md`](2026-10-10-startup-first-image.md) (placement trước khi hiện, cloak tới khung đầu, decode khi cửa sổ hiện).

## Kết luận ngắn

- Ảnh đầu (`RenderedFrame`, từ `CreateProcess`): **R2R 1127 -> 782 ms (-345, -31 %)**, **build 1321 -> 915 ms (-406, -31 %)** trong lô cuối (CPU nền 6-11 %);
  lô xác nhận mã cuối (người dùng đang dùng máy, CPU nền 18-22 %): R2R 1211 -> 849 (-362, -30 %), build 1359 -> 973 (-386, -28 %).
- **Mục tiêu NW-5 (<= 600 ms, publish R2R) CHƯA đạt:** 782 ms (lô yên tĩnh) / 849 ms (lô bận). Chưa có bước nào trong WPF còn bớt được >= 180 ms mà không đổi hành vi
  hoặc đổi cách triển khai; xem "Bước tiếp theo".
- Không đổi hành vi nhìn thấy được (xem "Người dùng cần xem bằng mắt"); mọi thay đổi nằm trước khung đầu hoặc chạy nền.

## Cách đo

- **Kịch bản:** tiến trình mới mở **một** JPEG trong **F4** = `C:\Xiuren\[[WALLPAPER]` (2867 file, 2705 jpg): file `...MissKON.com-114.jpg`, 2887x4330 = 12,5 MP, 5,8 MB, giữa thư mục theo tên.
- **Mỗi lần chạy:** data root riêng (`PHOTOREVIEW_DATA_ROOT` + `PHOTOREVIEW_ISOLATE_CONFIG`; `config.json` mặc định + `window-placement.json` Maximized màn hình chính) =>
  cache preview/thumbnail đĩa rỗng (**cold**); OS file cache ấm. `PHOTOREVIEW_PERF_TRACE` (CSV), `PHOTOREVIEW_DIAG_INSTANCE_LABEL=AGENT CHECK`.
- **Probe** (scratchpad, không commit): `Process.Start`, thăm dò cửa sổ top-level (hiện + không bị DWM cloak), đọc CSV của app tới `RenderedFrame`, đợi 1,2 s, WM_CLOSE.
  Không SendInput/SetForegroundWindow.
- **Xen kẽ:** mọi biến thể trong một lô chạy xen kẽ, thứ tự xáo mỗi vòng; 1 vòng khởi động bỏ + **8 vòng đo**; báo **median [min-max]** (ms từ `CreateProcess`).
- **CPU nền** đo 5 s trước/sau mỗi lô (ghi ở từng bảng). Máy: i7-9750H, AC, power plan Turbo, màn hình bật (người dùng có lúc đang làm việc).
- **build** = `dotnet build -c Release` (bản Explorer chạy hôm nay); **R2R** = `dotnet publish -r win-x64 --self-contained false` (`PublishReadyToRun=true` từ csproj).
- **Chỉ số NW-5:** `RenderedFrame` = tick render thứ 2 sau khi ảnh đầu được gán = ảnh đã trên màn hình.

## Bảng cuối: lô `final` (CPU nền 6,3 % / 10,6 %), mọi bước xen kẽ

| Biến thể | settingsLoaded | mainWindowCtor | xamlLoaded | windowShown | Assign | RenderedFrame |
|---|---:|---:|---:|---:|---:|---:|
| base build (`f3f62728`, 2.0.383) | 485 | 591 | 799 | 1018 | 1212 | 1321 [1285-1407] |
| base R2R | 399 | 468 | 669 | 878 | 1040 | 1127 [1112-1140] |
| v1 R2R (decode sớm + prefetch placement) | 387 | 437 | 645 | 821 | 850 | 965 [928-2197] |
| v2 R2R (+ làm nóng parser settings) | 292 | 344 | 559 | 732 | 757 | 869 [858-884] |
| v3 R2R (+ bỏ `Window.Icon`; **đã hoàn lại**) | 287 | 342 | 554 | 722 | 751 | 862 [834-918] |
| v6 R2R (+ index so sánh `GeneratedRegex`, không LINQ) | 290 | 343 | 550 | 727 | 752 | 842 [828-921] |
| v7 R2R (+ hoãn preload kick đầu) | 287 | 343 | 549 | 716 | 743 | **782** [757-862] |
| v7 build | 353 | 435 | 638 | 819 | 870 | **915** [892-975] |

Từng bước (R2R, delta median `RenderedFrame` giữa hai dòng liền kề của bảng): v1 -162, v2 -96, v3 -7 (hoàn lại), v6 -20, v7 -60.
Tổng base -> v7: R2R 1127 -> 782 (-345, -31 %); build 1321 -> 915 (-406, -31 %).

## Lô xác nhận mã cuối `final-v8` (v8 = v7 không có thay đổi `Window.Icon`; CPU nền 22,1 % / 17,8 %, người dùng đang dùng máy nên nhiễu hơn)

| Biến thể | median [min-max] | delta |
|---|---:|---:|
| base build | 1359 [1310-1499] | |
| v8 build | 973 [923-1114] | -386 (-28 %) |
| base R2R | 1211 [1110-1515] | |
| v8 R2R | 849 [793-1153] | -362 (-30 %) |

Mốc v8 R2R: settingsLoaded 298, mainWindowCtor 357, xamlLoaded 577, windowShown 764, Assign 809.
Mốc base R2R cùng lô: settingsLoaded 427, mainWindowCtor 504, xamlLoaded 720, windowShown 955, Assign 1130.

## Từng thay đổi (commit `4e6d5e55`)

| # | Thay đổi (file / lớp) | Cơ chế | Bằng chứng (lô) | Delta `RenderedFrame` |
|---|---|---|---|---|
| 1 | Decode ảnh khởi chạy từ `App_Startup`: `App.PrepareEarlyDecodeAsync` / `StartEarlyDecode`, `InitialViewportPredictor` (đoán hộp decode từ placement đã lưu), `InitialImagePrewarm`, `PreviewImageService.GetCurrentCacheKey(path, targetBox)`, `ViewportSizeSource.StartupPrediction` | Trước đây decode bắt đầu ở ~839 ms (`initialDecodeStarted`); nay ngay sau khi đọc `config.json` (~396 ms, `earlyDecodeStarted`), thread pool, chỉ **sau khi instance này giữ khoá** (`instanceLock`). Presenter nhập vào decode đang chạy; hộp đoán khớp hộp thật (`earlyDecodeBoxMatched` 100 % lần chạy). Sai hộp = một decode nền thừa, không bao giờ ảnh sai | b1 và `final` | -162 (`final`); b1: R2R -182, build -191 |
| 1b | `WindowPlacementService.Prefetch` / `Read` / `TakePrefetched` | Đọc `window-placement.json` trên pool (JSON reflection lần đầu ~27 ms trên UI trong `Show()`); `RestoreBeforeShow` dùng lại kết quả một lần, không xong/lỗi thì đọc lại như cũ | trace + `final` (gộp trong v1) | (gộp vào bước 1) |
| 2 | `StartupWarmup.WarmSettingsParser` gọi từ static ctor của `App` | Parse một lần `AppSettings` mặc định trên pool từ đầu tiến trình (metadata `System.Text.Json` ~100 thuộc tính + JIT converter: `AppSettingsPropInit` ~63 ms + Deserialize ~42 ms); UI không còn chờ ~75-95 ms sau `ConnectRenderThread`. Không đọc/ghi file nào | `final`, trace | -96; `settingsFileLoaded` từ ~360-380 xuống ~250-270 (dưới `renderThreadConnected` ~250-275) |
| 3 | `ComparePairService`: `GeneratedRegex` + `BuildIndex` không LINQ | Bỏ khởi tạo Regex Compiled (~13 ms) và JIT GroupBy/OrderBy (tổng `BuildIndex` ~44 ms) trên UI thread sau `Assign` | b6, `final` | -20 (`final`); b6 -32, Assign -> Presented 85 -> 59 ms |
| 4 | `ImagePresenter.DeferNextPreloadKick` (đặt trong `MainWindow` cho file khởi chạy) | Preload kick đầu tiên của tiến trình tốn ~16-33 ms trên UI trước khung; nay chạy sau `YieldAsync` (Background), bị bỏ nếu đã chuyển ảnh khác. Chỉ ảnh khởi chạy | b7, `final` | -60 (`final`); b7 -39, Assign -> Presented 80 -> 36 ms |

Giữ nguyên: thứ tự cuối của danh sách, khoá cache (cùng `ImageCacheKey`), file RAW không decode sớm (`InitialImagePrewarm` bỏ qua RAW).

## Đã thử và KHÔNG giữ (không có bằng chứng >= nhiễu)

| Thử | Số đo | Kết luận |
|---|---|---|
| Resolve trước các singleton view model trên pool trong lúc chờ render thread (b4, CPU 6,4/6,6 %) | v3 740 -> v4 761 (+21) | Tệ hơn/nhiễu; việc resolve chỉ còn ~13 ms sau bước 1 |
| Làm nóng font WPF (`Typeface`/`GlyphTypeface`) trên pool (b4) | v5 749 vs v3 740; `Show()` 143 vs 140 | Không lợi |
| Bỏ `Window.Icon` (dùng icon của exe) (b3, CPU 9,8/4,4 %) | v2 773 -> v3 763 (-10); `final` -7 | Dưới mức nhiễu và có rủi ro nhìn thấy -> **hoàn lại** (v8) |
| DWM cloak bật/tắt (x-cloak, CPU 7,5/5,1 %) | cloak 772 vs không 800 | Cloak không tốn gì; giữ nguyên |
| Chất lượng scale HighQuality vs Linear (x-linear) | Assign -> Presented 93 vs 104 | Không phải nguyên nhân chi phí khung đầu |
| Knob runtime trên v6 R2R (x-pgo, CPU 8,5/5,3 %) | mặc định 738 [724-782]; `DOTNET_TieredPGO=0` 731 (-7); `DOTNET_TieredCompilation=0` 876 (+138) | Nhiễu / tệ hơn; không đổi |

## Thí nghiệm triển khai (KHÔNG đổi trong nhánh này; để người dùng quyết) - lô x-comp (CPU 11/9,2 %) và x-sc2 (CPU 9,9/9,9 %)

Cùng mã v6, cùng probe, xen kẽ:

| Kiểu publish | Kích thước | `RenderedFrame` median [min-max] |
|---|---:|---:|
| R2R phụ thuộc framework (publish hôm nay) | 15 MB | 867 [831-900] (x-comp) / 841 [820-860] (x-sc2) |
| R2R phụ thuộc framework + `PublishReadyToRunComposite` (chỉ assembly của app) | 15 MB | 844 [814-962] (-23, nhiễu) |
| Self-contained R2R (framework không biên dịch lại) | 154 MB | 846 [824-876] (+5, như nhau) |
| **Self-contained + `PublishReadyToRunComposite`** (framework + app trong một ảnh composite; publish 47 s) | **174 MB** | **707** [695-780] (x-comp, -160) / **704** [687-759] (x-sc2, -137) |

Composite bớt đều trên UI thread: `settingsFileLoaded` 273 -> 187, `xamlLoaded` 579 -> 470, `windowShown` 749 -> 609, `Assign` 774 -> 637.
Chỉ có tổ hợp **self-contained + composite** mới có lợi; từng thứ riêng lẻ nằm trong nhiễu.

**Bẫy obj:** publish composite và non-composite dùng chung `obj\Release\...\win-x64`. Publish cái này ngay sau cái kia mà không xoá thư mục đó tạo ra exe
**crash trong coreclr (0xc0000602)**. Luôn dọn intermediates của RID giữa hai kiểu publish (hoặc dùng `-o`/`--no-incremental` kèm xoá `obj\Release\net10.0-windows\win-x64`).

**Chi phí của self-contained:** 174 MB thay vì 15 MB; runtime .NET đi kèm bản app nên cập nhật runtime = phải publish lại (không nhận bản vá runtime của máy);
publish ~47 s so với ~8 s tăng dần.

## Đường găng còn lại sau P-1 (v7 R2R, lô yên tĩnh, ms)

| Giai đoạn | ~ms |
|---|---:|
| Runtime + khởi tạo `Application` của WPF tới `App_Startup` | 165-175 |
| `ConnectRenderThread` (UI chờ render thread) | 80-100 |
| Settings/ngôn ngữ | ~15 |
| Instance lock + view model | ~55 |
| XAML `MainWindow` (`InitializeComponent`) | ~205 (theme dictionary ~35-40, mục context menu ~15, binding ~25, ScrollViewer ~12) |
| `Show()` | ~165 (HWND, layout đầu kể cả khởi tạo text/font ~35) |
| Catalog thư mục + `Assign` | ~25 |
| `Assign` -> khung ảnh đầu | ~35-40 |

Còn trên đường găng sau `Assign`: `BitmapSource.DUCECompatiblePtr` ~22 ms (định dạng đầu ra của decoder; chưa đổi).

## So với sàn (đo 2026-10-10 trong `NO-WPF-MIGRATION-PLAN`, probe tương tự)

| | Ảnh đầu | PhotoReview v7 R2R hơn |
|---|---:|---:|
| PhotoReview v7 R2R (nhánh này) | 782 | |
| Viewer WPF tối giản (1 Window + 1 Image) | 403 | +379 |
| Viewer Win32 + WIC R2R (không WPF) | 182 | +600 |

Chênh 379 ms với WPF tối giản là chi phí của kiến trúc app trong WPF (XAML chrome, DI, theme, settings...) mà một phần còn kéo được xuống;
chênh thêm ~220 ms (403 - 182) là chi phí cố định của WPF.

## Kết luận so với NW-5 và bước tiếp theo

NW-5 (c): dừng sau P-1 nếu <= 600 ms. **Chưa đạt:** 782 ms (lô yên tĩnh) / 849 ms (lô bận) với R2R; build 915 / 973 ms.
Các bước còn lại, thứ tự theo lợi/chi phí (số "ước" chưa đo ở nhánh này):

| | Bước | Lợi ước | Chi phí / rủi ro |
|---|---|---:|---|
| a | Self-contained + `PublishReadyToRunComposite` cho bản Explorer chạy | **-140..-160 ms** (đo: 707/704 vs 867/841); R2R ~620-650 ms | 174 MB thay vì 15 MB; cập nhật runtime phải publish lại; publish ~47 s; bẫy obj; không đổi mã app |
| b | Hoãn phần XAML chrome nhìn thấy (toolbar, overlay, context menu, compare panel) tới sau khung ảnh đầu | ~-100 ms (ước từ trace) | **đổi hành vi nhìn thấy**: toolbar/overlay hiện sau ảnh ~100 ms; cần người dùng đồng ý; test UI/binding phải cập nhật |
| c | Đọc/parse catalog ngôn ngữ song song với `config.json` | ~-15 ms (ước) | thấp; đổi thứ tự khởi tạo localizer |
| d | Decoder xuất ra đúng định dạng render để bỏ `BitmapSource.DUCECompatiblePtr` | ~-20 ms (ước, cần khảo sát) | chạm decoder + tương thích file cache preview |
| e | Hướng Win32/Native AOT (spike S1 của `NO-WPF-MIGRATION-PLAN`) | ~250-400 ms (ước của kế hoạch đó: R2R 300-400, Native AOT 250-350) | 12-33 người-tuần (ước của kế hoạch), cần toolchain |

Cộng dồn (ước): a ~620-650; a + b ~520-550; a + b + c + d ~485-515 ms. Riêng (a) chưa chắc xuống dưới 600; (a) + (b) thì có.

## Người dùng cần xem bằng mắt

Đóng hẳn PhotoReview, double-click ảnh trong Explorer, lặp 3-5 lần:

1. Icon cửa sổ/taskbar **không đổi** (thay đổi `Window.Icon` đã hoàn lại).
2. **Không khung trắng**, cửa sổ hiện thẳng ở vị trí/trạng thái lần đóng trước, nền tối, rồi ảnh.
3. **F11** vào/ra toàn màn hình; kéo cửa sổ sang màn hình khác, đóng/mở lại (vị trí được nhớ).
4. **Placement trên màn hình thứ hai / đã rút:** Maximized đã lưu trên màn hình rút vẫn mở Maximized trên màn hình chính; Normal ngoài màn hình mở kích thước mặc định.
5. **Mở thư mục lớn** (như F4): ảnh đầu hiện, rồi duyệt tiếp không bị khựng khi preload bắt đầu (kick được hoãn tới sau khung đầu).
6. **Mở file RAW của máy ảnh:** RAW không bao giờ decode sớm; hiển thị và ghép cặp RAW/JPEG như trước.
7. **Mở từ Explorer khi đã có một instance chạy:** không có việc decode sớm bị rò (decode chỉ bắt đầu sau khi instance giữ khoá).
