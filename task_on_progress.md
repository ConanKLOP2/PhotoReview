# PhotoReview — trạng thái hiện hành

- **Cập nhật:** 2026-09-20
- **Branch:** `master` (`origin/master` đồng bộ)
- **Baseline hiện tại:** O1–O4 đã thực hiện; T65, T66, T67, T71, T72, T74 và T87 `DONE`; T73 `BLOCKED` chờ quyền điều khiển GUI. Đã sửa lifecycle drain của `PreloadScheduler` cho cancel/dispose.
- **Verification gần nhất:** `verify-all.ps1` PASS — Architecture 7, Core 305, Imaging 203, Integration 59, App 163; tổng 737/737. Test flaky mục tiêu stress `30/30` PASS, Imaging suite `3/3` PASS; smoke, fault injection, publish và verify-release đều PASS.
- **Publish:** `src/PhotoReview.App/bin/Release/net10.0-windows/publish`.

## Thay đổi phiên này

- Xóa ba project shell cũ ở root, `.claude`, `.vs`, framework-dependent release cũ và bundle `self-contained-desc-fix`; giữ self-contained bundle vì `verify-all.ps1` còn hỗ trợ nó.
- Xóa compatibility marker trong `src/PhotoReview.App`; SourcePresenceTests dùng implementation thật ở Core/Imaging.
- Chuyển `PerfAnalyze*` và `PerformanceTestHarness` dùng chung sang `src/PhotoReview.Benchmarking`; Integration Tests không còn tham chiếu executable CLI.
- Cập nhật diagnosis commands và trạng thái tracker; `work/` chưa xóa vì còn có thể chứa bằng chứng T73.
- T74 đã đối chiếu với Git: PR #8 đã merge vào `master`, tag `v2.0.0` đã có trên `origin`, publish artifact vẫn tồn tại và được verify.
- Điều tra timing-flaky: `PreloadScheduler` nay drain tất cả worker task còn được scheduler theo dõi khi cancellation/exception trước khi hoàn tất; trước sửa test mục tiêu fail 9/30 lượt, sau sửa pass 30/30.

## Quyết định còn hiệu lực

- Giữ WPF làm framework UI; chưa mở spike WinUI 3. Chỉ xem lại khi PresentMon/ETW chứng minh UI chiếm ít nhất 40% P95 key-to-present hoặc có lỗi WPF tái hiện được.
- `DecoderBackend` mặc định `Wpf`; `WicDirect` và `TurboJpeg` là lựa chọn có fallback về WPF.
- `ScalingQuality` mặc định `HighQuality`.
- `UseSourceBytesCache` mặc định `false`; cache byte 16 GiB chỉ bật sau khi đo workload thật.
- D08/D09 chỉ chạy nếu cần thêm bằng chứng render/GC/ETW; không chặn release hiện tại.

## Việc còn lại

1. **T73 — GUI acceptance (BLOCKED):** chạy đủ 13 nhóm khi phiên có quyền điều khiển native Windows; ghi PASS/FAIL và tạo task sửa cho lỗi tái hiện được.
2. **T74 — Release (DONE):** PR #8 đã merge, `v2.0.0` đã tag/push, publish và verify đã hoàn tất.

## Bàn giao và lưu ý

- Tài liệu kiến trúc: `docs/architecture.md`; cơ chế/cache/benchmark: `docs/APP-MECHANISMS-VI.md`.
- Baseline hiệu năng cuối: `docs/refactoring/results/final.md`; quyết định UI: `docs/adr/0002-ui-framework.md`.
- Hồ sơ review đã hoàn tất nằm trong `docs/refactoring/archive/`.
- Cleanup 2026-09-19 đã bỏ 25 worktree, 72 local branch và 28 remote branch cũ; chi tiết và SHA phục hồi ở `docs/refactoring/archive/branch-cleanup-2026-09-19.md`.
- Không dùng input mức OS (`SendInput`, `SendKeys`, `SetForegroundWindow`) cho perf harness; config action thật của người dùng không được ghi đè.
- T73 bị hoãn có chủ đích; không suy diễn GUI PASS từ test tự động hoặc benchmark headless.
