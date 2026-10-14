# Gate đợt 3 của shell Win32: đo lại trên máy YÊN, phân rã và hai tối ưu (2026-10-14)

Gói: [NOWPF-PERF-GATE3](../decisions/NOWPF-PERF-GATE3.md). Nền: [2026-10-12-nowpf-wave3.md](2026-10-12-nowpf-wave3.md) (gate), [2026-10-12-wp21-stall.md](2026-10-12-wp21-stall.md)
(GPU rỗi), [2026-10-13-aot-local-trial.md](2026-10-13-aot-local-trial.md) (lượt AOT trên máy bận, độ tin cậy thấp).
Base = `origin/master` `c81c8552` (v2.0.504). Bản đo cuối = base + 2 commit của gói (xem mục 4).

## Kết luận ngắn

| Gate | Mục tiêu | Trước (đo lại, base) | Sau (gói này) | Kết luận |
|---|---|---|---|---|
| G1 cửa sổ thấy được, AOT | <= 150 ms | median 112-114 ms | 112 ms (không đổi) | **ĐẠT** |
| G1 cửa sổ thấy được, R2R (mặc định) | <= 150 ms | median 321-325 ms | 321 ms (không đổi) | **HỤT** (đường găng = runtime + JSON/JIT, mục 3) |
| NW-5 ảnh đầu <= 350 ms, AOT | <= 350 ms | median 208-209 ms | 204-206 ms | **ĐẠT** (dư 140 ms); R2R 408-413 ms (<= 600 NW-5 cũ đạt, <= 350 hụt) |
| G2 key->frame P95 trong cache | <= 3 ms | AOT P50 6,7 / P95 8,8; phím ĐẦU TIÊN 36-507 ms (8/10 lần >= 36) | AOT **P50 4,4 / P95 4,8**, phím đầu 4,6-6,8 ms | **HỤT** (4,8 so với 3), cải thiện 1,9x; sàn = chi phí một khung 2,7-3,7 ms |
| G2, R2R | <= 3 ms | P50 7,5 / P95 9,6; phím đầu 12-116 ms | P50 5,7 / P95 6,4; phím đầu 9-11 ms | **HỤT** |
| G3 mở Settings lần sau | <= 150 ms | 289 ms là cầu WPF CŨ (nay không còn: Win32 `SettingsDialog`, WP-35 GD5) | **48 ms** (R2R = AOT; lần đầu 63-79 ms) | **ĐẠT** |

Điểm cần biết trước khi đọc số:

1. **Số "cửa sổ thấy được 356 ms" và "Settings 289 ms" của đợt 3 đã lỗi thời**: Settings nay là hộp thoại Win32 (không XAML), AOT thì cửa sổ thấy được ~112 ms.
   Gate G1 chỉ đạt trên bản AOT; bản R2R (gói mặc định, framework-dependent) vẫn 321 ms.
2. **Probe trên desktop ẩn có 20-30 % lượt bị "đứng" 300-500 ms ở `CreateWindowEx`/`ShowWindow`/`Present`; trên desktop thật 0/20 lượt** (mục 2.3).
   Trung vị không đổi, P95 và cực đại thì có: nêu rõ ở từng bảng.
3. Hai tối ưu đã làm đều nhỏ, không đổi hành vi nhìn thấy: giữ GPU ấm ngay khi ảnh đầu vẽ xong, và vẽ ngay khung đầu sau khi rỗi (không chờ điểm lưới vblank).

## 1. Môi trường và cách đo

- Máy: i7-9750H (6 nhân / 12 luồng), Intel UHD 630 điều khiển màn hình + RTX 2080 Max-Q, Windows 11 build 26300, cắm điện. Một máy, một lô mỗi bảng.
- Tải CPU trước mỗi lô (`Get-CimInstance Win32_Processor`, 4-6 mẫu): 1-22 % (xem cột "CPU" của probe: 4-7 % trong lô). Không agent nặng nào khác chạy: chỉ có các
  `dotnet` build-server rỗi và `VBCSCompiler` của agent khác. KHÔNG nhiễu theo nghĩa của lượt AOT 13/10 (84-99 %).
- Hai bản publish CÙNG commit, cùng lần chạy `tools/publish-release.ps1 -RequireAot` (R2R framework-dependent + Native AOT, MSVC 14.x + SDK 10.0.26100; AOT 0 cảnh báo ILC).
  `PhotoReview.exe`: R2R 0,5 MB (+ DLL), AOT 16,2 MB. Không dùng `publish-r2r` của người dùng.
- Công cụ: `PhotoReview.Benchmark.Cli --startup-probe` (xen kẽ, seed, mỗi lần = tiến trình mới, data root riêng, 10 JPEG ~12 MB của F4 `C:\Xiuren\[[WALLPAPER]`,
  12 phím `PostMessage`, KHÔNG `SendInput`/`SetForegroundWindow`), chạy bằng `tools/diag/aot-hidden-run.ps1` (desktop riêng ẩn). Vòng 0 bỏ. 10-12 vòng mỗi cặp.
  Bảng "thô" lấy từ `runs.csv` (KHÔNG loại ngoại lai; cột tóm tắt của probe loại ngoại lai xấu bằng MAD nên trông "đẹp" hơn).
- Phần phân rã (mục 3) đọc từ CSV `PHOTOREVIEW_PERF_TRACE` của chính các lượt đó (mốc `Startup`, `KeyInput`, `RenderedFrame`); mốc đo phím đầu = KeyInput -> RenderedFrame kế.
- Khung (mục 2.2): `PHOTOREVIEW_FRAME_LOG` (có sẵn từ WP21-STALL).

Chạm DESKTOP THẬT: đúng MỘT lô ngắn (`real1`, 10 vòng x 2 bản, mỗi lượt ~4 s, 3 phím, cửa sổ hiện trên màn hình ~80 s) để biết cú đứng của desktop ẩn có thật không.
Không `SendInput`/`SetForegroundWindow`/`SetCursorPos`; cửa sổ có thể giành focus trong lúc đó (app tự `Activate` khi mở).

## 2. Số đo

### 2.1 Khởi động (cửa sổ thấy được = `windowRevealed`; ảnh đầu = `RenderedFrame`), ms từ lúc tạo tiến trình

Thô (không loại ngoại lai), median [min-max] P95. "đứng" = lượt có `windowRevealed` > median + 150 ms.

| Lô | Bản | n | Ảnh đầu | Cửa sổ thấy được | đứng |
|---|---|---:|---|---|---:|
| `ab1` (master, ẩn) | R2R | 10 | 413 [397-1208] P95 1208 | 325 [317-1173] | 3 |
| | AOT | 10 | 209 [204-616] P95 616 | 114 [107-603] | 2 |
| `ab2` (+marks, ẩn) | R2R | 12 | 410 [400-866] | 323 [318-830] | 4 |
| | AOT | 12 | 208 [203-602] | 112 [106-586] | 1 |
| `real1` (**desktop thật**) | R2R | 10 | 412 [399-416] P95 416 | **335** [326-339] P95 339 | 0 |
| | AOT | 10 | 211 [200-221] P95 221 | **121** [115-131] P95 131 | 0 |
| `finAot` (base vs gói, ẩn) | AOT base | 10 | 210 [200-683] | 113 [108-617] | 2 |
| | AOT gói | 10 | 206 [203-709] | 112 [107-117] | 0 |
| `finR2r` (base vs gói, ẩn) | R2R base | 10 | 409 [396-926] | 321 [311-664] | 1 |
| | R2R gói | 10 | 411 [399-1170] | 322 [316-1137] | 2 |

Hai tối ưu KHÔNG đổi khởi động (đúng thiết kế: giữ ấm chỉ bắt đầu SAU ảnh đầu; khung rỗi chỉ ảnh hưởng khung sau). Chênh giữa base và gói trong cùng bản nằm trong nhiễu.

### 2.2 key->frame (phím điều hướng `ramHit`, khoảng cách phím 250 ms, 2,5 s sau ảnh đầu)

Phím ĐẦU TIÊN của mỗi tiến trình tách riêng: probe cũ gộp nó vào "không có khung" (khung tới sau khi phím kế đã post) nên P95 tóm tắt che nó đi.

| Bản / lô | Phím đầu (sắp xếp, 10 lượt), ms | Các phím còn lại P50 / P95 / max (n=110) |
|---|---|---|
| AOT base (`finAot`) | 6,3 8,9 38,7 39,9 43,1 **180 211 414 494 507** | 6,7 / 8,8 / 9,1 |
| AOT + giữ ấm (B, `abAB`) | 6,5 6,6 6,6 7,1 7,1 7,9 8,1 8,4 8,5 8,6 | 6,5 / 8,8 / 9,1 |
| AOT + giữ ấm + khung rỗi vẽ ngay (C = gói, `finAot`) | 4,6 5,0 5,0 5,1 5,1 5,2 5,4 5,4 6,3 6,8 | **4,4 / 4,8 / 5,8** |
| R2R base (`finR2r`) | 12 13 38 41 42 42 43 43 51 116 | 7,5 / 9,6 / 10,2 |
| R2R gói (`finR2r`) | 9,1 9,3 9,4 9,6 9,6 9,7 9,7 9,7 10,2 10,9 | **5,7 / 6,4 / 6,4** |

Tóm tắt của probe (mọi phím, n=120): AOT base P50 6,9 / P95 41,0 -> gói 4,5 / 5,4; R2R base 7,9 / 46,1 -> gói 6,0 / 13,7 (phím đầu của R2R còn ~9-11 ms: lần đầu qua đường
điều hướng là mã R2R/JIT).

Chi phí MỘT khung (`PHOTOREVIEW_FRAME_LOG`, AOT gói, desktop ẩn, 3 tiến trình): khung điều hướng 3,5-4,2 ms (`endDraw` 0,3-0,5, `present` 1,7-2,4, phần còn lại = layout + vẽ ảnh + HUD
~1,2 ms); mỗi phím tạo HAI khung cách nhau ~4 ms (khung thứ hai do cập nhật chữ/HUD sau khi gán ảnh). Số desktop thật của WP21-STALL: khung P50 2,7-2,9 ms.
=> key->frame không thể <= 3 ms chừng nào chi phí một khung còn >= 2,7 ms: sàn thực tế ~ (nhận phím + timer ~1 ms) + khung 2,7-3,7 ms = 4-5 ms.

### 2.3 Cú đứng 300-500 ms trên desktop ẩn (và vì sao không tính vào gate)

Trên desktop ẩn, 20-30 % lượt có một cú đứng liền khối 300-500 ms ở MỘT trong: `CreateWindowEx` (ShellWindow), `ShowWindow`, `Present` ảnh đầu, hoặc mở hộp thoại lần đầu. Bằng chứng từ CSV
(marks tạm quanh từng bước, lô `ab2`): r09 AOT đứng 470 ms đúng trong `ShowWindow`, r03/r11 R2R đứng 300/380 ms trong ctor `ShellWindow`, r01 R2R 320 ms trong `ShowWindow`,
r08 R2R 526 ms ở `firstPresent`. Trong lúc đứng KHÔNG có thread nào của app làm gì (không sự kiện perf), decode xong từ lâu: đứng ở hệ thống (win32k/DWM/GPU).
Khớp cơ chế GPU Intel rỗi của WP21-STALL (cùng độ dài ~0,3-0,5 s; xác suất 26 % ở khoảng nghỉ 250 ms) và 0/20 lượt đứng trên desktop thật => ghi nhận là **đặc trưng của
desktop không tương tác của probe**, không phải của app. Giả thuyết "GPU rỗi" chưa kiểm trực tiếp (cần một tiến trình giữ GPU ấm song song); mọi số trung vị ở trên không bị ảnh hưởng.

### 2.4 Settings (hộp thoại Win32, desktop ẩn, 9 vòng xen kẽ x 3 lần mở, phím `Tab,Tab,Enter` rồi `Enter`)

Thời gian từ lúc post phím tới khi cửa sổ top-level mới hiện (thăm dò 5 ms, nên +0-5 ms): 

| Bản | Lần mở đầu median [min-max] | Lần sau median [min-max] P95 (n=16) |
|---|---|---|
| R2R | 79 [78-94] ms | **48** [46-64] P95 64 |
| AOT | 63 [62-454] ms (454 = cú đứng 2.3) | **48** [45-63] P95 63 |

Trong app (mốc tạm): `new SettingsDialog` ~1 ms (R2R 12 ms lần đầu) + `CreateDialogIndirectParam` 40-65 ms = 40-80 ms từ lệnh tới `ShowWindow`. Không còn thời gian dựng XAML.

## 3. Phân rã (median các lượt, desktop thật `real1`, ms từ lúc tạo tiến trình)

| Mốc | R2R | AOT | Đoạn (R2R / AOT) | Ghi chú |
|---|---:|---:|---|---|
| `appStartup` (vào `Main`) | 76 | 22 | **76 / 22** | khởi động runtime + nạp assembly (R2R) so với exe tự chứa |
| `servicesBuilt` | 100 | 28 | 24 / 6 | dựng DI |
| `settingsFileLoaded` | 207 | 32 | **107 / 4** | `SettingsStore.Load` (System.Text.Json source-gen ~100 thuộc tính): JIT/nạp kiểu ở R2R |
| `settingsLoaded` (+ngôn ngữ) | 230 | 49 | 23 / 17 | ngôn ngữ |
| `instanceLock` | 238 | 52 | 8 / 3 | khoá instance |
| `windowShown` | 264 | 80 | 26 / 28 | tạo cửa sổ + cloak + placement + `ShowWindow` |
| `d3dDeviceReady` | 307 | 96 | 43 / 16 | thiết bị D3D/D2D + swap chain (làm nóng trên pool đã chạy từ đầu) |
| `firstPresent` = `windowRevealed` | 330 | 120 | 23 / 24 | khung canvas đầu + Present + bỏ cloak |
| decode ảnh đầu xong | 390 | 200 | | decode ~120-145 ms bắt đầu ngay sau khoá, song song với cửa sổ |
| `RenderedFrame` (ảnh đầu) | 412 | 211 | | |

Đoạn lớn nhất:

- **G1 AOT (112-121 ms):** đã đạt. Phần lớn là tạo cửa sổ 28 + D3D 16 + khung đầu 24 + runtime 22.
- **G1 R2R (321-335 ms):** (1) đọc config/JSON 107 ms (32 %), (2) khởi động runtime 76 ms (23 %), (3) D3D 43 ms. Hai khoản đầu là chi phí JIT/nạp của R2R (AOT: 4 ms và 22 ms).
  Không có hạng mục nào của app bỏ được mà không đổi thiết kế (xem mục 5).
- **Ảnh đầu AOT (205-211 ms):** đường găng = decode ~120-145 ms sau khoá (khoá ở 52 ms), cửa sổ không nằm trên đường găng. NW-5 (<= 350) đạt dư 140 ms.
- **key->frame (mục 2.2):** (a) trước gói: chờ điểm lưới vblank kế (0-16,7 ms, trung bình ~4 ms ở nhịp phím này) + một khung ~3-4 ms; (b) phím đầu tiên: lần nộp việc đầu cho GPU
  rỗi bị chặn 36-507 ms vì giữ ấm chỉ bắt đầu từ chính phím đó.

## 4. Tối ưu đã làm (mỗi cái một commit; đo trước/sau cùng điều kiện, 10 vòng xen kẽ)

### 4.1 `perf(shell-gpu)`: giữ GPU ấm ngay khi ảnh đầu đã Present (commit `d4b2ff6a`)

`KeepWarmController.WarmAtLaunch()` (gọi một lần từ `OnFramePresented` ở ảnh đầu, chỉ khi `Activate` = true): kéo dài khoảng giữ ấm hiện có (5 s, tắt bằng `PHOTOREVIEW_GPU_KEEPWARM=0`)
mà không cần tương tác. Trước đây giữ ấm chỉ bắt đầu từ tương tác, nên chính phím điều hướng đầu tiên là lần nộp việc đầu cho GPU đã rỗi ~2,5 s (36-507 ms; 8/10 lượt >= 36 ms).
Không đổi `Activations`, throttle hay hành vi nhập liệu; không chạy ở smoke/cửa sổ không activate/WARP/tắt công tắc. Chi phí: ~4 % engine GPU + 0,6 % một lõi trong 5 s sau khi mở ảnh.

| Phím đầu tiên (AOT, 10 lượt xen kẽ, `abAB`) | trước | sau |
|---|---|---|
| ms (sắp xếp) | 6,2 6,2 36 42 42 43 43 44 44 188 | 6,5 6,6 6,6 7,1 7,1 7,9 8,1 8,4 8,5 8,6 |
| P95 mọi phím (n=120) | 42,6 | 8,9 |

Kiểm: `KeepWarmTests` 2 test mới (cộng dồn đúng hạn chót, không đụng bộ đếm tương tác, tắt thì im), `ShellKeepWarmTests` viết lại bước đầu (cửa sổ activate: giữ ấm chạy KHÔNG cần
tương tác; hết hạn thì im; rồi tương tác như cũ). Đột biến (đã chạy): bỏ `LaunchWarmups++` -> `WarmAtLaunch_Extends...` đỏ. Xóa lời gọi `WarmAtLaunch` ở `OnFramePresented` KHÔNG chạy thử riêng (suy luận: `ShellKeepWarmTests` chờ `LaunchWarmups >= 1`, 30 s rồi đỏ; lần sửa test đầu đã thấy đúng thông báo đó khi cửa sổ không activate).

### 4.2 `perf(shell-frame)`: khung đầu sau khi rỗi vẽ ngay, không chờ điểm lưới vblank (commit `d0258b2b`)

`TimerFrameClock.Schedule`: nếu không có khung nào trong một chu kỳ vừa qua (hoặc chưa có khung nào) thì hẹn đúng "bây giờ" thay vì điểm lưới vblank kế; từ khung thứ hai (hoạt ảnh liên tục)
vẫn neo lưới như cũ. Present flip-model vẫn được DWM lấy ở vblank kế nên hình hiện cùng lúc hoặc SỚM hơn một chu kỳ (vẽ ngay rồi chờ vblank thay vì chờ vblank rồi vẽ 3 ms để lỡ nó);
key->frame bớt trung bình nửa chu kỳ chờ. Rủi ro: không đổi nội dung khung, không tear (flip-model Present(1)); đồng hồ vẫn không phát quá một khung mỗi chu kỳ khi liên tục.

| key->frame (10 vòng xen kẽ, `abBC`, B vs C) | trước (B) | sau (C) |
|---|---|---|
| mọi phím P50 / P95 (n=120) | 7,1 / 9,1 | 4,5 / 5,6 |
| lô cuối `finAot` (A base vs C): phím đầu | 6,3-507 | 4,6-6,8 |
| `finR2r` R2R base vs gói: P50 / P95 (n=120) | 7,9 / 46,1 | 6,0 / 13,7 |

Kiểm: `FrameClockTests` (test neo lưới viết lại: khung đầu ngay, khung hai lưới; test mới: rỗi >= một chu kỳ thì ngay lại, burst vẫn neo lưới). Đột biến `false && timing`: 2 test đỏ.
`PhotoReview.Shell.Tests` đầy đủ 1653/1653 (gồm G-VIEW, G-PIX của Shell.Rendering), `Shell.Integration.Tests` lọc ShellKeepWarm|Startup|Hosting 84/84 sau sửa.

## 5. Ý tưởng chưa làm (rủi ro cao hoặc cần quyết định), kèm ước lượng

| Ý tưởng | Lợi ước tính | Vì sao chưa làm |
|---|---|---|
| Phát hành bản AOT làm gói mặc định (thay R2R) | G1 321 -> ~115 ms, ảnh đầu 410 -> ~207 ms, WS -21 % | Quyết định phát hành/hỗ trợ (cần `NOWPF-AOT-LOCAL-TRIAL` được duyệt; chưa kiểm LƯU Settings, RAW qua LibRaw trên AOT) |
| R2R: bắt đầu `SettingsStore.Load` ngay ở `Main`, song song dựng DI | -20 đến -25 ms R2R (24 ms DI) | Đổi thứ tự khởi tạo quanh instance lock/cloak = rủi ro; lợi nhỏ |
| R2R: bỏ JSON cho config khởi động (parser tay) hoặc nạp trước metadata | -50 đến -90 ms R2R | Diff lớn, rủi ro dữ liệu cấu hình; AOT đã giải quyết |
| Vẽ khung điều hướng bằng nội suy Linear khi chỉ đổi ảnh / gộp hai khung mỗi phím thành một | key->frame -0,5 đến -1 ms | Đổi chất lượng ảnh hoặc luồng HUD; sàn vẫn > 3 ms |
| Giữ GPU ấm LIÊN TỤC khi cửa sổ active (CHƯA XÁC NHẬN WP21-STALL b) | phím sau khoảng nghỉ > 5 s hết bị chặn 36-500 ms (25-50 % số lần) | Tiêu thụ nền liên tục (pin/nhiệt); cần chủ dự án |
| Nâng giữ ấm lúc khởi động từ 5 s lên 10-15 s | phủ người mở ảnh rồi xem lâu mới bấm | Cùng đánh đổi pin, chưa có số người dùng thật |
| Dựng device D3D trên luồng nền song song với decode (đề bài gợi ý) | ~0 | ĐÃ CÓ từ WP-20 (`ShellWarmup.WarmGraphics`, `graphicsWarm` ~104 ms); `d3dDeviceReady` vẫn tạo thiết bị cửa sổ trên UI thread vì swap chain cần HWND |
| Tái dùng/ẩn sẵn cửa sổ Settings | ~0 | Settings đã 48 ms |

## 6. Hạn chế

- Một máy, hai lần chạy (đợt 1: 10-12 vòng trên desktop ẩn x 5 lô; đợt 2: 10 vòng trên desktop thật). Tải nền 1-22 %.
- Số desktop ẩn có 20-30 % cú đứng hệ thống (2.3) — dùng trung vị; P95 khởi động trên desktop ẩn không phản ánh người dùng.
- G2 đo trên desktop ẩn (cửa sổ bị coi là "occluded": `Present` rẻ hơn thật). Số khung desktop thật ở WP21-STALL (2,7-2,9 ms) cao hơn chút so với khung ẩn ở `endDraw`, thấp hơn ở `present`;
  KHÔNG chạy lô key->frame trên desktop thật ngoài lô `real1` 3 phím (P50 6,8 trước gói). Nên có một lượt xác nhận trên desktop thật khi người dùng ở máy.
- Cú đứng desktop ẩn: giả thuyết GPU rỗi chưa được kiểm trực tiếp.
- Chưa đo: pin/điện năng của giữ ấm lúc khởi động; pan/zoom; nhiều màn hình; AOT lưu Settings.
