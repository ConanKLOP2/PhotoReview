# `--perf-session`: driver kịch bản trong process (D06)

Driver chạy app PhotoReview **trong chính process test** (`PhotoReview.Tests`), phát lại một kịch bản JSON (mở folder, Next, zoom, pan, Move…) và ghi CSV perf (D03/D04) cùng `metrics.json`, `process.json`, `session.json`. `tools/diag/run-matrix.ps1` chạy cả ma trận kịch bản × mode × điều kiện, mỗi lượt một process mới.

- Code: `PhotoReview.Tests/PerfSession.cs`, `PhotoReview.Tests/WpfTestHost.cs` (khung STA dùng chung với `--ui-next-probe`).
- Kịch bản: `tools/diag/scenarios/*.json` (S1–S9 theo `PERF-DIAGNOSIS-PLAN.md` mục 5).
- Dữ liệu chạy (`work\diag\runs\…`) **không commit**.

## Cách dùng

```powershell
# Build một lần
dotnet build PhotoReview.Tests -c Release

# Một lượt
dotnet run --project PhotoReview.Tests -c Release --no-build -- `
  --perf-session tools\diag\scenarios\s2-next-slow.json "<folder>" work\diag\runs\manual\s2 `
  --mode Preview --alias F1 [--repeat 3] [--commit <sha>]

# Ma trận (đọc đường dẫn từ work\diag\fixtures.local.json)
.\tools\diag\run-matrix.ps1 -Scenarios s2-next-slow -Modes Fast,Preview,Original -Conditions warm,cold-diskcache -Repeat 3 -FixtureAlias F1
.\tools\diag\run-matrix.ps1 -Scenarios s8-move -FixtureAlias F1 -Repeat 1
.\tools\diag\run-matrix.ps1 -Scenarios s6-zoom -FixtureAlias F2 -Modes Original
```

| Tham số | Ý nghĩa |
|---|---|
| `<scenario.json>` | File kịch bản (schema bên dưới) |
| `<folder>` | Folder ảnh nguồn. Chỉ đọc, trừ khi kịch bản cần bản sao (xem "An toàn") |
| `<outDir>` | Nơi ghi CSV và JSON. Không được chứa hoặc nằm trong folder nguồn, và không trùng `%LOCALAPPDATA%\PhotoReview` |
| `--mode` | `Fast`, `Preview` hoặc `Original`. Chỉ ghi đè `_settings.LoadingMode` **trong bộ nhớ**; nếu bỏ trống thì dùng mode trong config |
| `--repeat N` | Lặp N lần trong cùng process, mỗi lần một `MainWindow` mới. Khi N > 1, kết quả nằm trong `outDir\iter-NN\` |
| `--alias` | Tên fixture (F1, F2…) ghi vào `session.json` thay cho đường dẫn thật |
| `--commit` | SHA git ghi vào `session.json` (`run-matrix.ps1` tự truyền) |

Exit code: `0` đạt; `1` có bước lỗi (chi tiết trong `session.json` → `errors`); `2` sai tham số, kịch bản không hợp lệ hoặc đường dẫn không an toàn.

### `run-matrix.ps1`

- Tham số: `-Scenarios` (tên file trong `scenarios\` hoặc đường dẫn), `-Modes`, `-Conditions`, `-Repeat` (mặc định 3), `-FixtureAlias`, `-OutRoot` (mặc định `work\diag\runs`), `-FixturesFile`, `-SkipBuild`, `-ColdOsConfirmed`.
- Script build Release một lần, rồi chạy mỗi lượt bằng `dotnet run … --no-build -- --perf-session …` trong một process mới. Stdout của mỗi lượt được lưu vào `console.log`.
- Kết quả ghi vào `<OutRoot>\<yyyyMMdd-HHmmss>\<scenario>\<alias>-<mode>-<condition>\run-NN\`. `matrix.json` (danh sách lượt, trạng thái, số giây, commit, biến `PHOTOREVIEW_DIAG_*`) được cập nhật sau mỗi lượt.
- Điều kiện:
  - `cold-app`: process mới, giữ nguyên cache.
  - `cold-diskcache`: xóa `%LOCALAPPDATA%\PhotoReview\cache\*.png` và `thumbnails\*.png` trước **mỗi** lượt.
  - `warm`: chạy một lượt khởi động (`warmup\`, đánh dấu `warmup=true` trong `matrix.json`), rồi mới chạy các lượt được tính.
  - `cold-os`: script **dừng lại** (exit 3) và in hướng dẫn: người dùng reboot hoặc dùng RAMMap → Empty Standby List (cần admin). Sau đó chạy lại với `-ColdOsConfirmed`; mỗi ô chỉ chạy 1 lượt.
- Các biến `PHOTOREVIEW_DIAG_*` đặt trong shell gọi script sẽ được truyền xuống process con.
- `fixtures.local.json` hiện chứa backslash chưa escape (`"C:\Xiuren\…"`), vốn không phải JSON hợp lệ. Script sẽ thử parse lại sau khi escape các backslash đó.

## Schema kịch bản

```json
{
  "name": "S2-next-slow",
  "note": "tùy chọn",
  "copy": { "kind": "files", "count": 60 },
  "steps": [
    { "open": "folder" },
    { "open": "file", "index": 0 },
    { "key": "Right", "repeat": 100, "intervalMs": 1500 },
    { "waitMs": 3000 },
    { "waitIdle": true, "timeoutMs": 30000 },
    { "zoom": [0, 1, 2, 4], "holdMs": 1000 },
    { "pan": { "dx": 400, "dy": 0, "steps": 30, "intervalMs": 16 } },
    { "action": "Enter", "repeat": 50, "intervalMs": 800 }
  ]
}
```

| Bước | Hành vi |
|---|---|
| `open: folder` | Gọi `LoadFolderAsync(folder)` qua reflection **sau khi** đã set mode. Cửa sổ được tạo mà không truyền folder, vì constructor sẽ load ngay trước khi kịp đổi mode |
| `open: file` + `index` | Sắp xếp ảnh bằng `ImageSortService.Sort(…, ImageSortMode)` rồi gọi `LoadFolderAsync(folder, files[index])` |
| `key` | Gửi phím bằng routed event (xem bên dưới), lặp `repeat` lần, cách nhau `intervalMs`. Driver không chờ ảnh hiển thị, giống người bấm phím |
| `waitMs` | `Task.Delay` (dispatcher vẫn chạy) |
| `waitIdle` | Rảnh khi: không có file action đang chạy, `ReviewMetrics.Snapshot()` không đổi ≥ 1 s, và (`PreloadScheduler._preloadSchedulerTask` đã xong **hoặc** metrics không đổi ≥ 5 s). Sau đó chờ thêm `Dispatcher.InvokeAsync(…, ApplicationIdle)`. Quá `timeoutMs` (mặc định 30 s) thì ghi `TIMEOUT` vào `steps[].detail`, tăng `idleTimeouts` và chạy tiếp |
| `zoom` | `0` = Fit (gửi phím `ToggleFit` theo config; nếu không có phím thì gọi `ResetFitView`). Giá trị `> 0` gọi `SetZoom(value)` qua reflection. Giữ mỗi mức `holdMs` |
| `pan` | Chia `dx`/`dy` thành `steps` bước, mỗi bước gọi `ImageScroll.ScrollToHorizontalOffset/VerticalOffset` rồi chờ `intervalMs`. Chỉ có tác dụng khi ảnh lớn hơn viewport (đã zoom) |
| `action` | Gửi phím action (vd. `Enter`), lặp `repeat` lần. Mỗi lần: kiểm tra đường dẫn, gửi phím, chờ `_fileActionInProgress == 0` (tối đa 30 s), rồi chờ `intervalMs`. **Chỉ chạy trên bản sao** |
| `copy` | `kind: files` copy `count` ảnh đầu (theo tên) vào `<outDir>\copy\images`. Mặc định `count` = tổng số action + 10. `kind: compare` tạo `pairs` cặp `pairNNN.jpg` + `pairNNN (1).jpg` (cùng nội dung) từ các ảnh đầu. Kịch bản có `action` luôn dùng bản sao, kể cả khi không khai báo `copy` |

Tên phím là tên enum `System.Windows.Input.Key` (`Right`, `Left`, `Home`, `Enter`/`Return`, `F`…).

Các kịch bản có sẵn:

| File | Plan | Nội dung |
|---|---|---|
| `s1-open-folder.json` | S1 | Mở folder, chờ rảnh |
| `s1b-open-file.json` | S1 | Mở trực tiếp ảnh đầu tiên |
| `s2-next-slow.json` | S2, S5 | 100 × Next, 1,5 s. S5 = S2 chạy với `-Modes Fast,Preview,Original` |
| `s3-next-burst.json` | S3 | 200 × Next, 33 ms (~30 phím/s) |
| `s4-jump.json` | S4 | 40 × Next, 20 × Prev, Home. Không có End vì `ShortcutMappings` không có phím "ảnh cuối". Chưa có bước nhảy ngẫu nhiên |
| `s6-zoom.json` | S6 | Fit → 1× → 2× → 4× → pan ngang/dọc/về → Fit. Nên dùng F2 (48 MP) |
| `s7-compare.json` | S7 | Bản sao 20 cặp (40 file), 39 × Next, 800 ms |
| `s8-move.json` | S8 | Bản sao 60 ảnh, 50 × Move (Enter), 800 ms |
| `s9-large.json` | S9 | Folder lớn (F4), chờ preload, 200 × Next, 500 ms. Chưa có biến thể > 16 GB (xem F5) |

## Đầu ra

| File | Nội dung |
|---|---|
| `perf-<pid>-<ts>.csv` | CSV của `PerfCsvListener` (D03). Mỗi process một file; khi `--repeat` > 1, dùng `startQpc`/`endQpc` trong `session.json` để tách từng lần lặp. Dòng header ghi `commit=` (InformationalVersion của exe test, có SHA git) |
| `metrics.json` | `ReviewMetrics.Snapshot()` lấy từ field `_metrics` của cửa sổ, **sau** lần lặp |
| `process.json` | Peak/current working set, private bytes, managed heap, số GC gen0/1/2 (tổng và delta), `GC.GetTotalPauseDuration()` (tổng và delta), CPU time (tổng và delta), thời gian lần lặp |
| `session.json` | Kịch bản, mode (và mode trong config), alias, `folderPathId` (hash, không ghi đường dẫn), dùng bản sao hay không, số ảnh nguồn/đã mở, commit, version App, biến `PHOTOREVIEW_*`, thông số cửa sổ và DPI, `keysSent`/`keysHandled`, `idleTimeouts`, mốc UTC/QPC, thời gian và `detail` từng bước, ghi chú của host, lỗi |

Khi kết thúc, driver đóng cửa sổ, dispose listener rồi xóa `<outDir>\data` và `<outDir>\copy`.

## Cách gửi phím và hạn chế

```csharp
var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
window.RaiseEvent(args);
if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; window.RaiseEvent(args); }
```

- `MainWindow.xaml` gắn `Window_KeyDown` vào **`PreviewKeyDown`**, nên driver raise `PreviewKeyDownEvent` trước.
- `Keyboard.PrimaryDevice` chỉ là tham chiếu tới object thiết bị, dùng làm tham số. `PresentationSource.FromVisual` chỉ tra `HwndSource` của cửa sổ. `RaiseEvent` chỉ định tuyến event trong cây WPF: không vào hàng đợi input của OS, không đổi focus, không kích hoạt cửa sổ. Vì vậy phím không thể rơi vào cửa sổ khác.
- **Không đo được độ trễ input thật.** `Timestamp = Environment.TickCount`, nên `KeyInput.inputDelayMs` ≈ 0. Mọi thời gian chờ dispatcher trước khi handler chạy (`t_input` trong plan mục 3) đều không đo được. Driver cũng không mô phỏng auto-repeat của OS. Độ trễ input thật chỉ đo được khi người dùng tự bấm phím trên app thật với `PHOTOREVIEW_PERF_TRACE`.
- Handler là `async void`: `RaiseEvent` trả về ở `await` đầu tiên, nên phần đồng bộ của `ShowImageAsync` chạy ngay trong lúc gọi. Key→present lấy từ CSV (`KeyInput` → `Presented` kế tiếp; D11).
- `Keyboard.Modifiers` trong handler đọc trạng thái bàn phím thật. Nếu người dùng đang giữ Ctrl khi driver chạy, phím `Z` sẽ thành Undo. Driver không bao giờ gửi `Z` (xem danh sách cấm bên dưới).
- **Cửa sổ có thể không ở foreground.** Cửa sổ 1920×1080 DIP ở (0,0), `WindowState=Normal`, `ShowActivated=false`; driver không gọi `Activate()`/`Focus()`/`SetForegroundWindow`. Cửa sổ có thể bị cửa sổ khác che, nên frame pacing/DWM có thể khác so với khi app ở foreground. Với DPI > 100%, kích thước pixel lớn hơn 1920×1080 (`session.json` → `window.dpiScale`).
- Thao tác chuột (wheel zoom, kéo) không được mô phỏng; zoom và pan đi qua `SetZoom`/`ScrollViewer`.
- Trong CSV, `DispatcherLongOp` của chính driver cũng xuất hiện (vd. `Action\`1.Invoke` lúc tạo cửa sổ, các continuation của kịch bản). Khi phân tích nên bỏ qua các op nằm ngoài khoảng giữa các phím.

## An toàn

- **Quy tắc 4 (`PERF-DIAGNOSIS-TASKS.md`)**: không dùng `SendInput`/`SendKeys`/`keybd_event`/`mouse_event`/`AttachThreadInput`/`SetForegroundWindow`, không `Activate()`/`Focus()`, không đụng clipboard.
- **Config thật không bị ghi**: mode, danh sách action và (với S8) đích Move chỉ được sửa trên object `_settings` trong bộ nhớ. `AppSettings.Load()` là code app: nó chỉ ghi `config.json` khi file **chưa tồn tại**; driver in cảnh báo trong trường hợp này.
- **Window placement thật không bị đọc hay ghi**: `WpfTestHost.SuppressWindowPlacement` đặt `_placementRestored = true`, để `SetWindowPlacement` không di chuyển hay kích hoạt cửa sổ, và gỡ handler `Window_Closing` (vốn lưu `window-placement.json`).
- **Dữ liệu app** (session, journal, log) nằm trong `<outDir>\data` (`PHOTOREVIEW_DATA_ROOT`, đặt trước khi tạo listener/cửa sổ).
- **Disk cache preview** (`%LOCALAPPDATA%\PhotoReview\cache`) vẫn là cache thật; đây chính là đối tượng cần đo. Thumbnail mới không được ghi đĩa (`persistNewThumbnails: false`).
- **Action chỉ chạy trên bản sao**:
  - Config thật có thể trỏ action tới **đường dẫn tuyệt đối**. Trên máy này, `Enter` → Move tới `C:\Xiuren\[[WALLPAPER]`, tức chính fixture F4. Vì vậy driver **thay toàn bộ** `_settings.Actions`: rỗng nếu kịch bản không có `action`, còn nếu có thì chỉ một Move tới `<outDir>\copy\moved`.
  - Trước **mỗi** lần gửi phím action, driver kiểm tra lại: ảnh hiện tại, `_compareSelectedPath` và toàn bộ catalog phải nằm trong `<outDir>\copy\images`, đích action phải là đường dẫn tuyệt đối nằm trong `<outDir>\copy`. Nếu sai, driver dừng lần lặp.
- **Phím bị cấm trong bước `key`** (kiểm tra trước khi mở folder): `Escape`, phím `SendToRecycleBin`, `Undo`, `NextFolder`, `PreviousFolder`, `Fullscreen`, `MoveToFolder2`, và phím của mọi action trong config thật.
- **Đường dẫn**: driver từ chối `outDir` chứa hoặc nằm trong folder nguồn, hoặc trùng `%LOCALAPPDATA%\PhotoReview`. Driver chỉ tạo và xóa `data`/`copy` có file đánh dấu `.perf-session-temp`; nếu các thư mục này đã tồn tại mà không có dấu thì driver từ chối chạy.
- `run-matrix.ps1` không tự làm trống standby list, không đổi power plan hay Defender, và không cài công cụ.

## Khung STA dùng chung (`WpfTestHost`)

`WpfTestHost` sửa 3 lỗi D04 tìm thấy ở `--ui-next-probe`:

1. **Icon pack URI** (`pack://application:,,,/Assets/PhotoReview.ico`) được resolve theo `Application.ResourceAssembly`, mặc định là exe test. Setter public ném `InvalidOperationException` khi đã có entry assembly, nên host set bằng reflection vào `Application._resourceAssembly` và `BaseUriHelper.ResourceAssembly`. Ghi chú lúc chạy: `ResourceAssembly: reflection (…)`.
2. **`TryGetCachedPreview`** có 2 overload; probe giờ chỉ định kiểu `(string, out BitmapImage)`.
3. **Listener**: host gọi `PerfCsvListener.TryStartFromEnvironment()` khi có `PHOTOREVIEW_PERF_TRACE`, và gắn `App.PerfDispatcherHooks` (class private lồng trong `App`) qua reflection (`Attach(Dispatcher)`, `TraceDiagMode()`, `Detach()`). Không sửa `App.xaml.cs`. Nếu reflection thất bại, host ghi chú và chạy tiếp mà không có `DispatcherLongOp`.

`Application` được tạo một lần với `ShutdownMode = OnExplicitShutdown`. Mỗi process chỉ có một `Application`, và mỗi lệnh CLI là một process.

`--ui-next-probe` cũng dùng `SuppressWindowPlacement`. Trước đây, placement đã lưu có thể khôi phục (và hiện) cửa sổ minimized của probe.
