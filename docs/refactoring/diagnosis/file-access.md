# D02 — Đếm truy cập file (Process Monitor)

> **Ghi chú của Coordinator (2026-09-16):** `tools/diag/drive-app.ps1` (gửi phím bằng `SendInput` + `AttachThreadInput`) **không được merge**, vì trên desktop dùng chung phím mô phỏng đã rơi vào cửa sổ ứng dụng khác. Việc đo thật sẽ chạy lại sau khi có driver trong process (D06, gửi routed event vào chính cửa sổ app mà agent tạo ra), hoặc do người dùng tự bấm phím.

**Trạng thái: BLOCKED, không thu được số liệu Procmon thật.**

## Vì sao dừng

D02 phụ thuộc vào `tools/diag/drive-app.ps1` để tạo ra các lượt Next lặp lại (10 lần, cách nhau
3s / 100ms) trong khi Process Monitor đang ghi. Trong lúc chuẩn bị D01, tôi phát hiện môi trường máy
chạy tác vụ này là **máy chia sẻ giữa nhiều phiên agent chạy song song**, và `SendInput` (phím thật ở
mức OS) không nhắm được vào đúng cửa sổ tiến trình con mà tôi khởi động — dù `GetForegroundWindow()` /
`GetGUIThreadInfo()` gọi từ tiến trình PowerShell của tôi xác nhận cửa sổ đích (PhotoReview hoặc
Notepad trong bài kiểm thử) là foreground/focus, phím gửi bằng `SendInput` thực tế lại đi tới ứng
dụng Claude Code thật đang hiển thị trên màn hình vật lý dùng chung (xác minh bằng cách gõ Ctrl+A,
Ctrl+C vào Notepad rồi đọc lại clipboard: nội dung lấy được là hội thoại của một phiên Claude Code
khác, không phải chữ đã gõ). Chi tiết đầy đủ và cách khắc phục hai lỗi đã sửa trong
`drive-app.ps1` (struct `INPUT` marshal sai kích thước, và `SetForegroundWindow` bị từ chối) nằm ở
`docs/refactoring/diagnosis/log-baseline.md`.

Vì `SendInput` có thể rơi vào cửa sổ của phiên khác một cách không kiểm soát được, tôi **không chạy
bất kỳ lượt capture Procmon nào** trong mục 2 của D02 (F1 × {Fast, Preview, Original} × cold/warm
cache, và lượt IntervalMs=100) — chạy sẽ nghĩa là tiếp tục gửi phím thật với cùng rủi ro đã quan sát
được. Do đó không có file `.pml`/`.csv` nào được tạo, và bảng "số lần mở, byte đọc, số stat mỗi ảnh"
theo mode/cache **không có số liệu thật để điền**.

## Đã làm (chuẩn bị, không phụ thuộc SendInput)

1. **Xác nhận Procmon sẵn sàng:** `C:\MyProjects\PhotoReview\work\tools\procmon\Procmon64.exe` đã có
   sẵn (không cần chờ tải), cùng `Procmon.exe`, `Procmon64a.exe`, `Eula.txt`.
2. **Viết và kiểm thử `tools/diag/procmon-summary.ps1`** trên một CSV Procmon giả lập tối thiểu (không
   commit): script lọc đúng `Process Name = PhotoReview.App.exe` (loại bỏ dòng của tiến trình khác),
   đếm `CreateFile`/`ReadFile`/tổng byte (`Length:` trong cột Detail) và các thao tác stat
   (`QueryBasicInformationFile`, `QueryNetworkOpenInformationFile`, `QueryAllInformationFile`) theo
   từng file ảnh nguồn, và đếm riêng truy cập `cache\*.png`, `thumbnails\*.png`, `Sessions\*.json`,
   `operations.jsonl`. Xác nhận qua kiểm thử: 1 dòng CreateFile + 1 ReadFile (1234 byte) + 1 stat cho
   ảnh nguồn, 1 CreateFile + 1 ReadFile (500 byte) cho `cache\abc.png`, dòng của tiến trình khác
   (`OtherProc.exe`) bị loại đúng.
3. Script hỗ trợ `-ShownFiles` để tách trung bình mỗi ảnh giữa nhóm "đã hiển thị" (10 ảnh đầu qua
   Next) và nhóm "chỉ được preload" — chưa dùng được vì không có dữ liệu Next thật.
4. Xác định cú pháp dòng lệnh Procmon dự kiến (theo task):
   `Procmon64.exe /AcceptEula /Quiet /Minimized /NoFilter /BackingFile <pml>` để bắt đầu ghi,
   `/Terminate` để dừng, `/OpenLog <pml> /SaveAs <csv> /Quiet` để xuất CSV. Chưa xác minh được trên
   phiên bản đã tải vì không chạy được lượt capture nào (cần Next thật diễn ra trong lúc ghi để có dữ
   liệu ý nghĩa).

## Kết luận cho các giả thuyết

Không có bằng chứng file-access thật, nên **H1, H2, H3, H9 đều CHƯA RÕ** — không tăng, không giảm độ
tin cậy so với trước D02. Cần chạy lại trên một máy/phiên cô lập (xem khuyến nghị dưới) trước khi kết
luận được các giả thuyết này.

## Khuyến nghị

- Chạy D02 trên máy/phiên Windows dành riêng, không chia sẻ với các phiên agent khác đang chạy đồng
  thời trên cùng desktop vật lý — hoặc nhờ người dùng tự bấm Next 10 lần theo nhịp yêu cầu trong khi
  Procmon ghi, thay vì để agent tự gửi `SendInput`.
- `tools/diag/procmon-summary.ps1` đã sẵn sàng dùng ngay khi có file CSV Procmon thật (không cần sửa
  gì thêm); chỉ cần chạy lại capture theo đúng ma trận ở mục 2 của D02 trong
  `docs/refactoring/PERF-DIAGNOSIS-TASKS.md`.
