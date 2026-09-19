# PhotoReview — trạng thái hiện hành

- **Cập nhật:** 2026-09-20.
- **Checkout đã xác minh:** `master`, `ba1317e54702e3af8bd32c0bf5d953b3766f1ba0` (merge PR #9); working tree sạch trước review.
- **Mục tiêu phiên:** review xuyên repo, lập plan optimize/clean; chưa triển khai source/config, commit hoặc push.
- **Plan chờ duyệt:** `docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`, 13 task OC01–OC13 đều `TODO`; có evidence, scope, dependencies, multi-agent, tests, risks và rollback.

## Review / validation mới

- Đã xem Core/Platform.Windows, Imaging/TurboJpeg, App/coordinators/UI, Benchmarking/CLI/scripts/CI và tests liên quan bằng 3 agent + coordinator.
- **Release build + xUnit:** `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"` exit 0; 745 PASS (7 Architecture + 305 Core + 203 Imaging + 59 Integration + 171 App), 0 fail/skip. Có analyzer warnings.
- **Runtime probes:** DuplicateFinder chọn 2/2 bản cùng hash ở cả group toàn original và toàn numbered; chỉ selection, không recycle/delete. PerfAnalyze gộp 2 run workers 0/8 thành 1 group, R-CONT báo thiếu dữ liệu. Chi tiết/artifact tạm trong plan.
- **Source findings cần regression:** native buffer sizing unchecked; preload exit/restart chưa bảo đảm drain; batch dialog sau ConfigureAwait(false); WPF catch-all retry; folder remap O(n²)/stat lại; display state; cache invalidation/quota; Undo tail-limit; benchmark semantics.
- **Chưa xác minh runtime:** race Session/journal failures, native recycle identity, GUI T89. Không biến static concern thành runtime defect.
- Lượt này chưa chạy mới smoke/fault-injection/full verify-all/publish, vì chỉ review và tài liệu. Full release/publish sẽ chạy khi triển khai theo OC13.

## Quyết định và việc còn lại

1. Chờ người dùng duyệt plan OC01–OC13 trước thay đổi source/config. Ưu tiên safety + benchmark correctness trước tối ưu và clean code.
2. T89 wheel/anchor/pan và transaction Fit đã merge qua PR #9; `docs/refactoring/T89-FIT-LAYOUT-PLAN.md` vẫn **IN PROGRESS**, còn unify initial-mode viewport, STA layout và GUI Fit → wheel → drag trên ảnh dọc/ngang. Không gọi DONE từ pure math tests.
3. Giữ WPF, DecoderBackend=Wpf, ScalingQuality=HighQuality; SourceBytesCache mặc định off. Chỉ đổi theo số đo/plan được duyệt; không ép RAM gây paging/OOM.
4. T73 còn coverage Recovery retry/ảnh hỏng nếu có fixture; T74 release trước đã DONE theo hồ sơ cũ, không phải release mới trong lượt review.
5. Sau implementation: `./tools/verify-all.ps1`, publish `src/PhotoReview.App/bin/Release/net10.0-windows/publish`, feature branch/push origin/PR master theo AGENTS. Không commit thẳng master.

## Tiếp tục

- Đọc AGENTS, plan mới và plan T89; kiểm tra branch/SHA/dirty tree lại trước làm.
- Chỉ hai tài liệu được sửa/tạo trong phiên: plan mới và file này; chưa commit. Các hồ sơ lịch sử ở `docs/refactoring/archive/`.
- Giữ frame khi Move/Delete đang loading; no hidden retry. Không dùng OS SendInput/SendKeys/SetForegroundWindow cho harness; dùng fixture riêng, không đổi config/action người dùng.
