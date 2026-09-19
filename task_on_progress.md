# PhotoReview — trạng thái hiện hành

- **Cập nhật:** 2026-09-19
- **Branch:** `codex/w6-performance`
- **Baseline hiện tại:** T65, T66, T67, T71, T72 và T87 đã `DONE`; commit tài liệu gần nhất `6f8ba8d`.
- **Verification gần nhất:** `verify-all.ps1` PASS — Architecture 7, Core 305, Imaging 203, Integration 59, App 163; tổng 737/737. Smoke, fault injection, publish và verify-release đều PASS.
- **Publish:** `src/PhotoReview.App/bin/Release/net10.0-windows/publish`.

## Quyết định còn hiệu lực

- Giữ WPF làm framework UI; chưa mở spike WinUI 3. Chỉ xem lại khi PresentMon/ETW chứng minh UI chiếm ít nhất 40% P95 key-to-present hoặc có lỗi WPF tái hiện được.
- `DecoderBackend` mặc định `Wpf`; `WicDirect` và `TurboJpeg` là lựa chọn có fallback về WPF.
- `ScalingQuality` mặc định `HighQuality`.
- `UseSourceBytesCache` mặc định `false`; cache byte 16 GiB chỉ bật sau khi đo workload thật.
- D08/D09 chỉ chạy nếu cần thêm bằng chứng render/GC/ETW; không chặn release hiện tại.

## Việc còn lại

1. **T73 — GUI acceptance:** chạy đủ 13 nhóm trong `docs/refactoring/REFACTOR-TASKS.md`; ghi PASS/FAIL và tạo task sửa cho lỗi tái hiện được.
2. **T74 — Release:** PR vào `master`, CI xanh, người dùng duyệt, version/tag/publish theo checklist.
3. Đối chiếu patch riêng `8513eb0` trước khi xóa nhánh giữ lại `refactor/t47-delete-source-presence`.

## Bàn giao và lưu ý

- Tài liệu kiến trúc: `docs/architecture.md`; cơ chế/cache/benchmark: `docs/APP-MECHANISMS-VI.md`.
- Baseline hiệu năng cuối: `docs/refactoring/results/final.md`; quyết định UI: `docs/adr/0002-ui-framework.md`.
- Hồ sơ review đã hoàn tất nằm trong `docs/refactoring/archive/`.
- Cleanup 2026-09-19 đã bỏ 25 worktree, 72 local branch và 28 remote branch cũ; chi tiết và SHA phục hồi ở `docs/refactoring/archive/branch-cleanup-2026-09-19.md`.
- Không dùng input mức OS (`SendInput`, `SendKeys`, `SetForegroundWindow`) cho perf harness; config action thật của người dùng không được ghi đè.
