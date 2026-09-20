# PhotoReview — trạng thái hiện hành

- **Cập nhật:** 2026-09-20.
- **Checkout đã xác minh:** `master`, `ba1317e54702e3af8bd32c0bf5d953b3766f1ba0` (merge PR #9); working tree sạch trước review.
- **Mục tiêu phiên:** review xuyên repo và triển khai ba gói P1 đầu tiên trong plan optimize/clean.
- **Plan:** `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`; OC02/OC03/OC04 đã `DONE`, các task khác vẫn `TODO` hoặc chờ evidence.

## Review / validation mới

- Đã xem Core/Platform.Windows, Imaging/TurboJpeg, App/coordinators/UI, Benchmarking/CLI/scripts/CI và tests liên quan bằng 3 agent + coordinator.
- **Release build + xUnit:** `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` exit 0; 745 PASS (7 Architecture + 305 Core + 203 Imaging + 59 Integration + 171 App), 0 fail/skip. Có analyzer warnings.
- **Runtime probes:** DuplicateFinder chọn 2/2 bản cùng hash ở cả group toàn original và toàn numbered; chỉ selection, không recycle/delete. PerfAnalyze gộp 2 run workers 0/8 thành 1 group, R-CONT báo thiếu dữ liệu. Chi tiết/artifact tạm trong plan.
- **Source findings cần regression:** native buffer sizing unchecked; preload exit/restart chưa bảo đảm drain; batch dialog sau ConfigureAwait(false); WPF catch-all retry; folder remap O(n²)/stat lại; display state; cache invalidation/quota; Undo tail-limit; benchmark semantics.
- **Chưa xác minh runtime:** race Session/journal failures, native recycle identity, GUI T89. Không biến static concern thành runtime defect.
- `tools/verify-all.ps1` PASS sau implementation: 7 Architecture + 307 Core + 204 Imaging + 59 Integration + 171 App = 748/748; smoke, fault-injection, publish và verify-release PASS.

## Quyết định và việc còn lại

1. OC02/OC03/OC04 đã triển khai và commit; tiếp tục OC05–OC12 theo plan, mỗi scope cần regression test và Release gate.
2. T89 wheel/anchor/pan và transaction Fit đã merge qua PR #9; `docs/refactoring/T89-FIT-LAYOUT-PLAN.md` vẫn **IN PROGRESS**, còn unify initial-mode viewport, STA layout và GUI Fit → wheel → drag trên ảnh dọc/ngang. Không gọi DONE từ pure math tests.
3. Giữ WPF, DecoderBackend=Wpf, ScalingQuality=HighQuality; SourceBytesCache mặc định off. Chỉ đổi theo số đo/plan được duyệt; không ép RAM gây paging/OOM.
4. T73 còn coverage Recovery retry/ảnh hỏng nếu có fixture; T74 release trước đã DONE theo hồ sơ cũ, không phải release mới trong lượt review.
5. Sau implementation: `./tools/verify-all.ps1`, publish `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, feature branch/push origin/PR master theo AGENTS. Không commit thẳng master.

## Tiếp tục

- Đọc AGENTS, plan mới và plan T89; kiểm tra branch/SHA/dirty tree lại trước làm.
- Commit implementation: `4a81ba8` preload lifetime, `6a4ec01` decoder safety/fallback, `00cad65` duplicate survivor. Tài liệu plan và file này được cập nhật sau validation.
- Giữ frame khi Move/Delete đang loading; no hidden retry. Không dùng OS SendInput/SendKeys/SetForegroundWindow cho harness; dùng fixture riêng, không đổi config/action người dùng.
