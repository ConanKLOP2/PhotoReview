# Mở ảnh từ Explorer: thời gian tới ảnh đầu, khung trắng lúc khởi động, so sánh phiên bản (2026-10-10)

Nhánh `perf/startup-first-image` (base `cb0e8130`, 2.0.380). Quyết định còn mở: [`P-STARTUP-FIRST-IMAGE`](../decisions/P-STARTUP-FIRST-IMAGE.md).

## Cách đo

- **Kịch bản:** tiến trình mới mở trực tiếp 1 file JPEG (5,4 MB, giữa thư mục) trong **F4** = `C:\Xiuren\[[WALLPAPER]` (2867 file, đếm trước/sau mỗi lô: không đổi).
  Đo trên thư mục thật, không copy. Mỗi lần chạy: data root riêng (`PHOTOREVIEW_DATA_ROOT` + `PHOTOREVIEW_ISOLATE_CONFIG`, không đụng config/cache/placement thật của người dùng),
  `PHOTOREVIEW_PERF_TRACE` (PerfCsvListener: `Startup`/`Folder`/`Decode`/`Assign`/`Presented`/`RenderedFrame`), `PHOTOREVIEW_DIAG_INSTANCE_LABEL=AGENT CHECK`.
- **Probe** (công cụ tạm trong scratchpad, không commit): `Process.Start` exe + file, đồng hồ QPC chung với CSV của app; thăm dò cửa sổ top-level của PID
  (~0,5 ms: lúc hiện, rect, `IsZoomed`, `DWMWA_CLOAKED`); chờ dòng `RenderedFrame`, đợi 1,2 s rồi `CloseMainWindow` (WM_CLOSE). Không SendInput/SetForegroundWindow.
- **Lô xen kẽ** (thứ tự phiên bản xáo mỗi vòng), 1 lần khởi động bỏ (warm-up) + 6-10 lần đo / phiên bản; báo **median [min-max]**, ms tính từ lúc gọi `CreateProcess`.
  `D ...` = hiệu từng lần chạy tính từ `openPathBegin` (bỏ được nhiễu của phần .NET/WPF khởi động trước đó).
- **Điều kiện:** `cold` = xoá cache preview/thumbnail đĩa của data root trước mỗi lần (giống máy người dùng: `%LOCALAPPDATA%\PhotoReview\cache` đang rỗng);
  `warm` = cache đĩa còn từ lần trước. OS file cache luôn ấm (không xả được khi không có quyền admin). Placement mặc định = Maximized trên màn hình chính.
  `exp` = có cửa sổ Explorer mở F4 (thứ tự Explorer áp dụng); sort `Name` (cấu hình thật của người dùng) hoặc `Default`.
- **Môi trường:** i7-9750H, AC, power plan Turbo; tải CPU nền đo 5 s trước mỗi lô = **1,8-6 %** cho các lô quyết định (lô có 11-41 % ghi rõ bên dưới, do Edge của người dùng);
  không agent build/test nào khác. **Màn hình đang tắt** suốt phiên (display state = 0, người dùng vắng): `Rendered`/`RenderedFrame` chịu nhịp render khi tắt màn
  hình (~100 ms sau Assign, bình thường ~2 khung hình) - số tuyệt đối của mốc (c) có thể khác khi màn hình bật; so sánh xen kẽ vẫn hợp lệ.
- Mốc: (a) cửa sổ hiện (probe), (b) ảnh đầu decode xong (`Decode` nav 1, hoặc đọc cache đĩa), (c) ảnh đầu hiển thị (`Presented`, kèm `RenderedFrame`).

## A. Hiện trạng 2.0.380, chia theo giai đoạn (lô `g`, cold, Name + Explorer, 10 lần, CPU nền 3,6 %)

| Giai đoạn (UI thread trừ khi ghi khác) | median | ghi chú |
|---|---:|---|
| CreateProcess -> `appStartup` (runtime + nạp assembly) | 191 | trước mọi code của app |
| -> `servicesBuilt` | 19 | |
| -> `renderThreadConnected` (`StartupWarmup.ConnectRenderThread`, chặn UI) | 133 | |
| -> `settingsLoaded` (UI **chờ** task đọc config.json + ngôn ngữ trên pool) | 96 | task mất ~230 ms kể từ servicesBuilt (`settingsFileLoaded`/`languageLoaded` mới thêm) |
| -> `instanceLock` | 19 | |
| -> `mainWindowCtor` (resolve MainViewModel graph) | 84 | |
| -> `xamlLoaded` (InitializeComponent) | 191 | |
| -> `openPathBegin` | 5 | quét thư mục chạy song song trên pool: xong sau ~30 ms |
| -> Show() trả về (`windowShown`) | **306** | gồm: hiện cửa sổ 1200x800 (trắng), Loaded khôi phục placement -> maximize -> layout lần 2 |
| -> `presentStart` (catalog 2867 mục, áp thứ tự Explorer) | 41 | continuation của folder load phải chờ UI thread rảnh |
| -> `Assign` (decode ảnh đầu ~145 ms bắt đầu ở đây) | 177 | |
| -> `RenderedFrame` | 106 | màn hình tắt, xem trên |

**Thứ tự Explorer (Prefetch COM) không nằm trên đường găng:** snapshot đã có trước khi folder load hỏi tới trong 100 % lần chạy (`explorerSnapshot` 2-8 ms sau `F:start`,
áp dụng sớm `explorerApplied` trước `presentStart`). `F:explorerAwaited` ≈ `presentStart` - 10 ms ở mọi phiên bản.

**Sort Default/Name không dùng Explorer:** Prefetch vẫn chạy nhưng đo **không có chi phí thấy được**: bản thử chỉ Prefetch khi sort mode dùng Explorer
(gọi trong task đọc settings) cho `D openPath->Presented` 632 vs 630 ms (Default, 8 lần, CPU nền 30 %) và chậm hơn 53 ms ở Name (nhiễu, nền 41 %) -> **không áp dụng**.

**ReadyToRun:** file `.jpg` của người dùng mở bằng `src\PhotoReview.App\bin\Release\net10.0-windows\PhotoReview.App.exe` (HKCU `Applications\PhotoReview.App.exe`),
tức bản **build**, không phải bản publish (R2R). Đo xen kẽ (lô `z-r2r`, cold, Name + Explorer, 10 lần, nền 17,7 %):

| Bản | launch -> openPath | (c) Presented | D openPath -> Presented |
|---|---:|---:|---:|
| 2.0.380 build | 964 | 1708 | 716 |
| 2.0.380 publish (R2R) | 871 | 1516 | 658 |
| nhánh này, build | 1024 | 1657 | 626 |
| nhánh này, publish (R2R) | **882** | **1413** | **533** |

R2R bớt ~150-250 ms ảnh đầu (lô sớm hơn, nền 11,6 %: -150 / -475 ms). `dotnet publish` tăng dần sau build mất ~8 s. Chọn bản Explorer chạy là việc của người dùng (quyết định).

## B. So sánh phiên bản ("từ rev 240 nặng hơn")

Tag thật: `v2.0.200` (4aec4b8a), `v2.0.239` (53c45d9d), `v2.0.240` (2f2bf342 = merge camera RAW #239), `v2.0.260`, `v2.0.300`, `v2.0.380` (= base). Build Release
ở worktree riêng (scratchpad), không đụng bin của checkout chính. (c) Presented, median [min-max], 6 lần / phiên bản, xen kẽ:

| Lô | v200 | v239 | v240 | v260 | v300 | v380 |
|---|---:|---:|---:|---:|---:|---:|
| warm, Name, không Explorer (nền 1,8 %) | 1605 [1153-2008] | 1357 [1094-1754] | 1318 [1126-1643] | 1600 [1276-2388] | 1476 [1284-1644] | 1344 [1180-1659] |
| warm, Name + Explorer (nền 2,1 %) | 1855 [1621-2098] | 1439 [1139-1804] | 1608 [1296-2197] | 1272 [1176-1817] | 1319 [1191-1700] | 1367 [1214-1580] |
| **cold**, Name + Explorer (nền 2,6 %) | 1616 [1246-2190] | 1784 [1411-2104] | 1622 [1275-1822] | 1562 [1322-1897] | 1702 [1272-2090] | 1484 [1353-1665] |

- **Không có bước nhảy ở v240.** v240 nhanh hơn v239 ở 2/3 lô; khác biệt giữa các phiên bản nằm trong độ phân tán (bimodal: Show() có lần ~290 ms, có lần ~760 ms
  ở mọi phiên bản cũ khi màn hình tắt). Bản hiện tại có median thấp nhất ở lô cold. Thời gian decode ảnh đầu như nhau (134-139 ms, cold) -> RAW (#239) không làm chậm decode JPEG.
  Không cần `git bisect` vì không có hồi quy để chỉ ra.
- Giả thuyết cho cảm nhận của người dùng: `window-placement.json` thật (ghi 2026-10-08 20:30) là Maximized trên **màn hình thứ hai đã rút** (rect -1197,-1047..3,-247).
  Từ đó app mở cửa sổ **1200x800 trắng** (placement bị bỏ vì ngoài màn hình) thay vì toàn màn hình - đúng mô tả "½-¼ màn hình". Đo với file placement thật này:
  2.0.380 mở 1200x800 thường; nhánh này mở maximized (lô `z-useroffscreen` dưới).

## C. Khung trắng

Nguồn gốc (đo bằng timeline cửa sổ của probe, 2.0.380):
1. `Show()` hiện HWND ở kích thước XAML **1200x800** (t≈+150 ms từ openPathBegin) trước khi WPF vẽ khung đầu: compositor vẽ surface chưa được vẽ = **trắng**
   (PrintWindow lúc đó: 95 % pixel trắng).
2. ~60 ms sau, `Window_Loaded` mới `SetWindowPlacement` -> di chuyển + maximize (cửa sổ "nhảy"), rồi layout lần 2; khung WPF đầu tiên (nền `#101010`) tới ~+390 ms.

Giới hạn chụp hình: DXGI Desktop Duplication trả `E_ACCESSDENIED`, GDI `BitBlt` màn hình chỉ ~2 khung/s và `PrintWindow(PW_RENDERFULLCONTENT)` chỉ đọc được
surface GDI (không phải nội dung DirectX của WPF) - vì màn hình đang tắt và cửa sổ app không được lên trên cùng (không được dùng SetForegroundWindow).
Nên **màu** khung đầu chỉ xác nhận được bằng mắt; **thứ tự/kích thước/cloak** thì đo được (timeline dưới).

Sửa:
- **Placement trước khi hiện:** `WindowPlacementService.RestoreBeforeShow` trong `SourceInitialized` (HWND đã có, chưa hiện): `SetWindowPlacement` với `SW_HIDE`
  + `WindowState` = trạng thái đã lưu, để `Show()` của WPF hiện cửa sổ **thẳng** ở chỗ/trạng thái cuối. Monitor đã rút + đã lưu Maximized -> vẫn mở Maximized.
- **Cloak tới khung đầu:** `DWMWA_CLOAK` trong `SourceInitialized`; bỏ cloak ở **tick render thứ 2** sau khi cửa sổ hiện (khung đầu đã vẽ), dự phòng `ContentRendered`
  và hẹn giờ 3 s. Theme giữ nguyên (`Dark.Backdrop`/`Dark.Canvas`); không đổi gì sau khi ảnh đã hiện.

Timeline cửa sổ (probe, một lần chạy điển hình của lô `g` / `z-useroffscreen`, ms từ CreateProcess):

| Bản | Lúc hiện | Trạng thái lần đầu thấy | Sau đó |
|---|---|---|---|
| 2.0.380, placement Maximized | 894 | 1200x800 thường, **không cloak** (trắng) | 948 di chuyển, 953 maximize, 1064 rect cuối, 1144 khung WPF đầu, ảnh 1389 |
| nhánh này, placement Maximized | 886 (cloak) | **maximized, cloak** | 1036 bỏ cloak (khung tối `#101010`), ảnh 1219 |
| 2.0.380, placement thật của người dùng (monitor đã rút) | 1272 | 1200x800 thường, trắng | giữ 1200x800 |
| nhánh này, placement thật của người dùng | 1241 (cloak) | **maximized, cloak** | 1411 bỏ cloak, ảnh 1690 |
| không có placement (lần chạy đầu) | - | cả hai bản 1200x800; nhánh này cloak tới khung đầu | |

## D. Trước / sau (nhánh này = `final2`/`g1`, cùng mã)

| Lô (xen kẽ, cold trừ khi ghi) | n | CPU nền | (c) Presented 380 -> nhánh | D openPath->Presented | D openPath->Show() | Thấy cửa sổ (hiện & không cloak) |
|---|---:|---:|---|---|---|---|
| Name + Explorer (`g`) | 10 | 3,6 % | 1349 -> **1245** | 604 -> **484** (-20 %) | 306 -> 183 | 873 (trắng 1200x800) -> 1063 (maximized, tối) |
| Default (`z-default`) | 10 | 6 % | 1344 -> **1234** | 588 -> **493** | 300 -> 189 | 866 (trắng) -> 1048 |
| Name + Explorer, warm (`z-warm`) | 8 | 3,8 % | 1529 -> 1425 | 540 -> 459 | 336 -> 231 | |
| placement thật của người dùng (`z-useroffscreen`) | 8 | 18,8 % | 1740 -> 1670 | 701 -> 636 | 276 -> 279 | 1181 (trắng 1200x800) -> 1421 (maximized) |

Tách đóng góp (lô `g`, Name + Explorer cold): bản không prewarm (`g0`) 553 ms, có prewarm (`g1`) 484 ms -> **prewarm decode -69 ms** (khoảng không chồng nhau:
356-418 vs 451-495 ở `D openPath->Assign`); phần còn lại (-51 ms so với 380) là placement trước khi hiện (Show() 306 -> 183 ms).
Prewarm: khi cửa sổ hiện (`SWP_SHOWWINDOW`) hộp decode được tính từ client rect và `PrewarmInitialImage` bắt đầu decode file khởi chạy; presenter gặp
`Lookup = inflight` ở 100 % lần chạy (cùng key, không decode hai lần).
Bỏ cloak bằng `ContentRendered` (bản trước của nhánh) trễ ~180 ms vì WPF post nó ở mức Input, sau folder load + ảnh đầu -> đổi sang tick render.

## Rủi ro / giới hạn

- Màn hình tắt suốt phiên đo; tuyệt đối của (c) có thể khác khi bật màn hình. Khung trắng/đen chỉ người dùng xác nhận được bằng mắt.
- Cloak: nếu DWM từ chối (`DwmSetWindowAttribute` lỗi) cửa sổ hiện như cũ; nếu không có tick render, hẹn giờ 3 s bỏ cloak.
- Prewarm sai key (DPI đổi giữa lúc hiện và layout) = một decode thừa trên nền, không bao giờ sai ảnh. RAW/định dạng không hỗ trợ: không prewarm.
- Không đổi thứ tự cuối của danh sách (folder load/Explorer giữ nguyên), không chạm ApplyFitViewAsync, không Dispatcher.Invoke mới.
