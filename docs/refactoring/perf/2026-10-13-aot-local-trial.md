# Chạy thử cục bộ bản Native AOT so với R2R (2026-10-13)

Gói: [NOWPF-AOT-LOCAL-TRIAL](../decisions/NOWPF-AOT-LOCAL-TRIAL.md). Tiếp theo [2026-10-12-wp36-aot.md](2026-10-12-wp36-aot.md).
Máy người dùng (VS 2022 BuildTools MSVC + Windows SDK 10.0.26100), một lượt, không phải CI.

## Kết luận ngắn

- Bản AOT chạy được thật: `--smoke` mã 0 (3/3), mở **thật** ZoomPrompt, Settings, Recovery, Diagnostics (3 lượt x 4 cửa sổ = 12/12, trùng R2R: cùng lớp `#32770`, cùng tiêu đề, cùng kích thước), đóng shell sạch mã 0.
- Ảnh đầu nhanh hơn R2R khoảng 2,3 lần (trung vị 8 vòng: 451 so với 1028 ms), cửa sổ hiện 169 so với 771 ms, working set -26 %.
- **Độ tin cậy số tuyệt đối: THẤP.** Máy bận 84-99 % CPU suốt lượt đo (nhiều agent chạy song song; bộ đếm trước lượt 49-100 %). Thứ tự và tỉ lệ có ích; con số ms tuyệt đối thì không so được với mục tiêu NW-5 (<= 350 ms).

## Phiên bản và cách đo

| | R2R | Native AOT |
|---|---|---|
| Commit | `0a15b79d` (origin/master, v2.0.491 + 3 script chẩn đoán chưa commit nên ghi `.dirty`) | cùng commit, cùng lần chạy `tools/publish-release.ps1 -RequireAot` |
| Lệnh | `dotnet publish` Shell.Win32 + App (framework-dependent, ReadyToRun) | `-p:ShellAot=true` (publish 232 s trên máy bận) |
| `PhotoReview.exe` | 0,5 MB (apphost + `PhotoReview.dll`, cần .NET 10 đã cài) | 15,5 MB, không cần .NET |
| Thư mục (bỏ `*.pdb`) | 18,3 MB / 49 tệp (gồm cả `PhotoReview.App.exe` WPF) | 23,1 MB / 11 tệp |

Chú ý: thư mục R2R chứa cả bản WPF và nhiều DLL; AOT gộp mọi thứ vào một exe nên thư mục AOT LỚN HƠN R2R (đảo so với WP-36 ngày 12/10: 18,6 MB; thêm Settings/Recovery Win32 làm exe 11 -> 15,5 MB).
Không dùng `publish-r2r` của người dùng (chỉ đọc): hai bên đều publish mới từ cùng commit vào thư mục tạm.

Công cụ (đều chạy trên desktop riêng ẩn, không `SendInput`, không `SetForegroundWindow`):

```powershell
# 1. publish cả hai (R2R + AOT) từ cùng commit
tools\publish-release.ps1 -RequireAot -OutputDirectory <r2r> -AotOutputDirectory <aot>
# 2. A/B ảnh đầu: probe có sẵn, chạy trên desktop ẩn
tools\diag\aot-hidden-run.ps1 -CommandLine '"<Benchmark.Cli.exe>" --startup-probe --source "C:\Xiuren\[[WALLPAPER]" --count 10 --rounds 5 --warmup 1 --seed 21 --keys 12 --out <out> --shell <aot>\PhotoReview.exe --wpf <r2r>\PhotoReview.exe' -LogPath <log>
# 3. mở cửa sổ phụ bằng phím gửi qua PostMessage (Tab,Tab,Enter = Settings; menu Tools = Tab x4,Enter,Down...)
tools\diag\aot-window-check.ps1 -Exe <exe> -PhotoFolder <jpg> -Label <tên>
# 4. working set 10 s sau ảnh đầu, xen kẽ
tools\diag\aot-ws10.ps1 -Exes "aot=<exe>;r2r=<exe>" -PhotoFolder <jpg> -Runs 3
```

## (1) `--smoke`

| Lần | R2R | AOT |
|---|---|---|
| 1 | mã 0 (6334 ms, nguội) | mã 0 (1029 ms) |
| 2 | mã 0 (2334 ms) | mã 0 (538 ms) |
| 3 | mã 0 (1561 ms) | mã 0 (404 ms) |

## (2) Ảnh đầu, cửa sổ, working set

Probe xen kẽ, mỗi vòng = tiến trình mới, 10 JPEG ~12 MB của F4 (`C:\Xiuren\[[WALLPAPER]`), 12 phím; vòng khởi động bị loại. Hai lô: 3 vòng (seed 13) và 5 vòng (seed 21), tất cả 0 lỗi.

| Chỉ số (ms) | R2R lô 1 (n=3) | AOT lô 1 | R2R lô 2 (n=5) | AOT lô 2 | **R2R gộp 8 vòng** | **AOT gộp 8 vòng** |
|---|---:|---:|---:|---:|---:|---:|
| Ảnh đầu, trung vị | 886 | 431 | 1184 | 470 | **1028** (628-1527) | **451** (240-878) |
| Cửa sổ hiện, trung vị | 749 | 138 | 800 | 200 | **771** | **169** |
| Cửa sổ thấy được, trung vị | 830 | 248 | 999 | 287 | **889** | **268** |
| key->frame P50 | 6,8 | 6,6 | 7,1 | 6,9 | ~7 | ~7 |
| key->frame P95 | 75,9 | 62,3 | 289,1 | 404,0 | không dùng được | không dùng được |
| WS đỉnh theo probe, MB | 154 | 114 | 154 | 113 | 154 | 113 |
| CPU máy trong lô, % | 88 | 99 | 96 | 84 | | |

P95 của key->frame ở cả hai bên bị nhiễu CPU (vài phím rơi đúng lúc tải nền đột biến): không kết luận.

Working set 10 s sau ảnh đầu (3 lượt xen kẽ, 10 JPEG):

| Lượt | R2R WS / private (MB) | AOT WS / private (MB) |
|---|---|---|
| 1 | 141,9 / 83,8 | 112,0 / 80,4 |
| 2 | 141,5 / 84,5 | 112,3 / 80,7 |
| 3 | 143,5 / 86,6 | 104,5 / 72,9 |
| **Trung vị** | **141,9 / 84,5** | **112,0 / 80,4** (-21 % WS) |

(Thời gian tới ảnh đầu của `aot-ws10.ps1` đo bằng vòng thăm dò 50 ms nên nhiễu, không dùng.)

## (3) Cửa sổ phụ có mở THẬT không

Bản AOT, mỗi lượt là một tiến trình mới trên desktop ẩn; "mở" = xuất hiện cửa sổ top-level MỚI, hiện, của chính tiến trình; sau đó WM_CLOSE cửa sổ, shell vẫn sống, rồi WM_CLOSE shell -> mã 0.

| Cửa sổ | Cách mở | Cửa sổ thật quan sát | AOT 3 lượt | R2R 3 lượt |
|---|---|---|---|---|
| ZoomPrompt | phím `3` (CustomZoom) | `#32770` "Custom click zoom level" 366x171 | 3/3 | 3/3 |
| Settings | Tab, Tab, Enter (nút ⚙ của toolbar) | `#32770` "Photo Review · Settings" 961x658 | 3/3 | 3/3 |
| Recovery | Tab x4, Enter (menu Tools), Down, Enter | `#32770` "Recovery" 1241x658 | 3/3 | 3/3 |
| Diagnostics | Tab x4, Enter, Down x2, Enter | `#32770` "Diagnostics" 716x592 | 3/3 | 3/3 |

Không lượt nào rơi vào "không có trong bản này" (`SecondaryWindowFallback`); kích thước cửa sổ AOT = R2R. Dự phòng chỉ còn là mạng an toàn khi cổng vắng.
Lần chạy đầu của kịch bản Recovery/Diagnostics không mở được vì phím tới menu gửi quá sớm trong lúc máy bận: script nay chờ menu `#32768` xuất hiện rồi mới gửi phím tới nó.

## Giới hạn

- Máy bận 84-99 % CPU: số ms tuyệt đối bị đội khoảng 2x; chưa chốt NW-5. Cần một lượt trên máy rảnh (`startup-probe.ps1`, cùng lệnh) trước khi ghi con số vào gate.
- Chưa kiểm: LƯU cài đặt từ Settings của AOT (mới chỉ mở/đóng), RAW qua LibRaw, hành động tệp thật (di chuyển/Recycle), Benchmark, BatchReview, cập nhật. Phần lớn đã có test in-process (JIT), nhưng JIT khác AOT ở cắt gọt/phản chiếu; cần người dùng dùng thật vài ngày.
- Đo trên desktop ẩn (D3D phần cứng/WARP do hệ điều hành chọn); hiệu năng ở desktop thật có thể chênh. Không đo pan (G0.6), không đo nhiều màn hình/F11.
- Một máy, một commit, 8 vòng; không so qua ranh giới cũ/mới: cả hai bên cùng commit và cùng lần publish.
