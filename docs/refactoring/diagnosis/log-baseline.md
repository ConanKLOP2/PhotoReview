# D01 — Phân tích nhanh từ AppLog (baseline)

> **Ghi chú của Coordinator (2026-09-16):** `tools/diag/drive-app.ps1` (gửi phím bằng `SendInput` + `AttachThreadInput`) **không được merge**, vì trên desktop dùng chung phím mô phỏng đã rơi vào cửa sổ ứng dụng khác. Việc đo thật sẽ chạy lại sau khi có driver trong process (D06, gửi routed event vào chính cửa sổ app mà agent tạo ra), hoặc do người dùng tự bấm phím.

**Trạng thái: BLOCKED trước khi thu đủ dữ liệu.** Xem "Vì sao dừng" bên dưới. Tài liệu này ghi lại
những gì đã làm, bằng chứng thu được, và lý do không thể an toàn chạy tiếp ma trận đo ở mục 2 của
task D01 trong `docs/refactoring/PERF-DIAGNOSIS-TASKS.md`.

## Đã làm

1. Viết và kiểm thử `tools/diag/parse-applog.ps1` trên log mẫu tự tạo (`tools/diag/samples/applog-sample.log`,
   đã ẩn đường dẫn ảnh dạng `<F1>\<nnn>.jpg`). Script nhóm `ShowImage ... token=N`, tính
   `start→cache-state`, `start→thumbnail-presented`, `start→preview-presented`, đếm `Preload paused
   for memory`, `Preload progress`, các dòng `Explorer ...`, và khoảng `LoadFolder start` → 
   `preview-presented` đầu tiên (T0→T2). In bảng P50/P95/max theo `ramReady` bằng phân vị nearest-rank
   (`rank = ceil(p/100 * N)`), có ghi chú khi N nhỏ.
2. Viết và kiểm thử `tools/diag/drive-app.ps1`: sửa `config.json` (backup trước, LoggingEnabled=true,
   LoadingMode theo tham số), tùy chọn xóa cache tái tạo được, khởi động app với
   `PHOTOREVIEW_DATA_ROOT` cô lập, chờ `MainWindowHandle`, đưa cửa sổ lên foreground, gửi phím
   `VK_RIGHT` bằng `SendInput` (Win32 thật, không phải `SendKeys` hay routed event giả lập).
3. Build Release (`PhotoReview.App.csproj`) tại HEAD `aed950b` (nhánh `diag/D01-D02`), 0 lỗi.
4. Chạy thử nghiệm (smoke test) nhiều lần trên fixture F1 thật để xác minh app khởi động, ghi log,
   và đóng lại đúng cách. Trong quá trình đó phát hiện và sửa hai lỗi trong `drive-app.ps1`:
   - `SendInput` ban đầu trả lỗi `ERROR_INVALID_PARAMETER (87)` vì struct `INPUT` marshal thiếu
     field `MOUSEINPUT` trong union, khiến `Marshal.SizeOf(INPUT)` tính sai kích thước trên x64
     (32 byte thay vì 40 byte thật). Đã sửa bằng cách thêm đầy đủ `MOUSEINPUT`/`HARDWAREINPUT` vào
     union.
   - `SetForegroundWindow` đơn thuần bị Windows từ chối vì tiến trình gọi (PowerShell nền, không
     phải tiến trình đang giữ input) không có quyền chuyển foreground. Đã thêm `AttachThreadInput`
     trước khi gọi `SetForegroundWindow`/`BringWindowToTop` (kỹ thuật chuẩn để vượt qua giới hạn này).
   - Sau hai lần sửa trên, `SendInput` trả về thành công (return=2, `GetLastError=0`) và
     `GetForegroundWindow`/`GetGUIThreadInfo` xác nhận cửa sổ PhotoReview đúng là cửa sổ có
     foreground **và** focus tại thời điểm gửi phím.

## Vì sao dừng (BLOCKED)

Máy chạy tác vụ này là **máy tính chia sẻ giữa nhiều phiên agent chạy song song** (bằng chứng: nội
dung clipboard đọc được trong lúc kiểm thử là bản ghi hội thoại của một phiên Claude Code khác đang
điều phối các task T13a/T13b/D01+D02 trên cùng máy). Sau khi sửa hai lỗi ở trên, để xác minh
`SendInput` thực sự hoạt động, tôi chạy một phép thử tối thiểu và an toàn hơn (gõ vào Notepad thay vì
PhotoReview): mở Notepad, gõi `SendInput` 3 phím A/B/C, sau đó Ctrl+A, Ctrl+C để đọc lại nội dung qua
clipboard. Nội dung clipboard đọc lại **không phải** "ABC" mà là đoạn hội thoại của phiên Claude Code
khác nói trên — nghĩa là bàn phím ảo (`SendInput`) trong môi trường này **không đi tới cửa sổ tiến
trình con mà tôi vừa tạo**, mà đi tới ứng dụng Claude thật đang hiển thị trên màn hình vật lý dùng
chung, bất kể `GetForegroundWindow()`/`GetGUIThreadInfo()` gọi từ tiến trình PowerShell của tôi báo
cửa sổ PhotoReview mới là foreground/focus.

Nói cách khác: trên máy chia sẻ này có ít nhất hai "phiên" desktop logic khác nhau — nội bộ (nơi
`GetForegroundWindow` của tiến trình con thấy đúng cửa sổ của nó) và bàn phím vật lý thật (nơi
`SendInput` thực sự phát ra), và chúng không đồng nhất. Kết quả là tôi đã **vô tình gửi Ctrl+A,
Ctrl+C thật vào cửa sổ của một phiên Claude Code khác** — một hành động ngoài ý muốn, không thể kiểm
soát được đích đến thực của phím gửi đi. Đây là rủi ro an toàn nghiêm trọng: bất kỳ lần `SendInput`
tiếp theo nào (kể cả các phím tưởng chừng vô hại như mũi tên phải) đều có thể rơi vào cửa sổ của
phiên khác và gây tác động ngoài ý muốn ở đó.

Vì lý do này, tôi **dừng toàn bộ việc chạy ma trận D01 mục 2** (6 lượt S1+S2/S3 trên F1/F1b/F2 với
`drive-app.ps1`) và không thu thập số liệu P50/P95/max thật. Tôi không có cách nào, từ bên trong máy
chia sẻ này, đảm bảo `SendInput` chỉ tới đúng cửa sổ PhotoReview mà không rủi ro ảnh hưởng phiên
khác. Đã dọn dẹp: đóng mọi tiến trình PhotoReview.App/Notepad/Procmon còn sót, xóa clipboard, xóa các
thư mục `%TEMP%\PhotoReview-diag-*`, và khôi phục `config.json` (xác minh bằng SHA-256 khớp bản sao
lưu `work/diag/config.backup.json`).

## Kết quả đã có (từ 1 lần chạy log thật, không phải ma trận đầy đủ)

Log mẫu ở `tools/diag/samples/applog-sample.log` là log thật (đã ẩn đường dẫn) từ một lần mở fixture
F1 (Preview mode, cold cache, không gửi phím do vấn đề nói trên): duy nhất `token=1` (ảnh đầu
tiên) được hiển thị.

| token | ramReady | start→cache-state (ms) | start→thumbnail (ms) | start→preview-presented (ms) |
|---|---|---|---|---|
| 1 | False | 9 | 253 | 905 |

`LoadFolder start` → `preview-presented` đầu tiên (T0→T2 xấp xỉ): **1417 ms** (bao gồm `Explorer
query-start`, `LoadFolder scan complete`, decode + thumbnail + preview cho ảnh nén ~1.4 MB/ảnh của
F1).

`Preload progress` xuất hiện liên tục ngay sau khi ảnh đầu tiên hiển thị (preload 8 worker chạy song
song, `queued` tăng dần 8→56 trong ~2.4 s, `cacheCount` tăng theo). Không thấy `Preload paused for
memory` trong lượt chạy ngắn này (máy có RAM dư dả — `availableBytes` ~17 GB trong suốt lượt chạy).

`Explorer view fallback: status=NoMatchingWindow` xuất hiện — trong môi trường headless/automation
này, không có cửa sổ Explorer thật đang mở fixture folder, nên `ExplorerOrderService` không tìm thấy
snapshot và rơi vào fallback (dùng thứ tự file mặc định thay vì thứ tự Explorer). Đây là hành vi dự
kiến trong điều kiện thử nghiệm này, không phải lỗi ứng dụng.

## Ảnh hưởng tới các giả thuyết H*

Vì chỉ có 1 lần điều hướng thật (không có Next), phần lớn giả thuyết **CHƯA THỂ kết luận**:

- **H1** (Preview tốn gấp đôi — thumbnail rồi mới decode) — *chưa rõ*: có bằng chứng định tính đúng
  hướng (thumbnail-presented lúc +253ms, preview-presented lúc +905ms, cách nhau ~650ms cho cùng một
  ảnh — phù hợp với mô hình "hiện thumbnail trước, decode preview sau"), nhưng cần nhiều token và cả
  hai mode Fast/Preview để so sánh mới đủ tin cậy.
- **H5** (Preload dừng vì memory load) — *giảm nhẹ độ ưu tiên trong điều kiện RAM rộng*: không thấy
  `Preload paused for memory` khi RAM khả dụng ~17 GB; giả thuyết này cần folder lớn hơn hoặc RAM hạn
  chế hơn để kiểm chứng, chưa bác bỏ.
- **H13** (Explorer snapshot đến muộn gây reindex/chặn T2) — *không loại trừ nhưng không quan sát
  được trong lượt này*: Explorer fallback xảy ra vì không có cửa sổ Explorer thật (điều kiện môi
  trường automation), không phải vì snapshot đến muộn; cần chạy có cửa sổ Explorer thật mở sẵn để
  kiểm chứng H13 đúng nghĩa.
- **H3, H15**: không có dữ liệu (cần nhiều lần Next liên tiếp).

## Hạn chế

- Log làm tăng độ trễ (I/O ghi log đồng bộ hàng đợi, xem `AppLog.cs`).
- Chưa có mốc decode/render (đó là việc của D03/D04 — EventSource); AppLog chỉ có các mốc thô mức
  UI/ShowImage.
- Timestamp chỉ chính xác tới mili-giây (`DateTime.Now`, không phải QPC).
- Input phải đi qua `SendInput` mức OS thật theo yêu cầu của task; trong môi trường máy chia sẻ này,
  `SendInput` không đáng tin cậy để nhắm đúng cửa sổ đích (xem "Vì sao dừng" ở trên) — đây là hạn chế
  của **môi trường thực thi**, không phải của bản thân log hay app.

## Khuyến nghị

- Chạy lại D01 mục 2 (ma trận 6 lượt) **trên một máy/phiên Windows độc lập, không chia sẻ** với các
  phiên agent khác, hoặc để **người dùng tự gõ phím** trong khi log được bật (loại bỏ hoàn toàn nhu
  cầu `SendInput` từ agent).
- Một lựa chọn khác nhất quán với kế hoạch: dùng driver `--perf-session` (D06) với routed
  `KeyEventArgs` bên trong tiến trình app (không đi qua hàng đợi input hệ điều hành, nên không có rủi
  ro nhắm nhầm cửa sổ) cho các số liệu tương đối; chỉ dùng `SendInput`/phím thật khi có một máy/phiên
  hoàn toàn cô lập.
