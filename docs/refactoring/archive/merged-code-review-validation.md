# Merged-code review — validation (MR06)

- Branch tích hợp: `refactor/integration`; các thay đổi MR đã đi qua `codex/merged-review-fixes` và được merge vào `master` qua PR #5. Commit validation gốc: `50c2650`.
- Lệnh: `tools/verify-all.ps1` (Release), exit code 0.
- VERIFY xUnit hiện tại (sau T53a/T53b): Architecture 7, Core 304, Imaging 196, Integration 59, App 162 — **728 passed, 0 failed, 0 skipped**. `Tests.Unit` đã được chia vào các project theo lớp và không còn là một project gate riêng.
- Smoke: file-operation, fault-injection, publish + verify-release framework-dependent đều đạt.

| Finding | Task | Commit | Trạng thái |
|---|---|---|---|
| F01, F02 | MR01 | 210523e, a1e7f52, 613af50 | Resolved (RAM guard thật; bỏ `_ui.YieldAsync` trong PreloadScheduler để tránh deadlock với Dispose trên UI thread) |
| F03 | MR02 | a32fd32, 599a2e4 | Resolved |
| F04 | MR03 | 48a52ce | Resolved |
| F05, F06 | MR04 | f22116c | Resolved |
| F07 | MR05 | a5fe746, 613af50, 50c2650 | Resolved (cache v3 có `.meta` backend+orientation; thiếu meta = miss) |

## Giới hạn còn lại
- Chưa chạy GUI thủ công (đóng cửa sổ khi đang preload, ảnh ICC thật) và benchmark backend/ICC; cần người dùng hoặc driver in-process.
- Cache tổng dung lượng nguồn trong `App.xaml.cs` chưa có test riêng (composition root).
- CI remote chưa đọc; không coi local là CI xanh.
- T87/D07 chỉ tiếp tục sau khi có quyết định Q8/phạm vi đo; CI remote chưa được đọc lại trong hồ sơ local.
