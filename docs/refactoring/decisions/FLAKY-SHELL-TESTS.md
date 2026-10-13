---
id: FLAKY-SHELL-TESTS
order: 1290
summary: |-
  CHƯA XÁC NHẬN: hai test Shell.Tests đỏ ngẫu nhiên trên CI sửa tận gốc - Benchmark_HasADarkBackground (bật/tắt comctl32 v6 ở hai việc dispatcher -> SEHException, đo 15/60) và StartupCloak SafetyTimeout (Timer thật 50 ms + chờ 10 s phụ thuộc thread pool) -> bộ hẹn tiêm được.
---

# FLAKY-SHELL-TESTS - hai test Shell.Tests đỏ ngẫu nhiên trên CI (2026-10-14, CHƯA XÁC NHẬN)

## 1. Triệu chứng (log CI nguyên văn)

- `NativeDialogChromeTests.Benchmark_HasADarkBackground_AndReadableText(visualStyles: True)` đỏ ở các run 38430286743 và 38432813132
  (nhánh `refactor/ci-opt2-*`), 106-120 ms: `System.Runtime.InteropServices.SEHException : External component has thrown an exception`
  tại `ActivationContext.DeactivateActCtx` <- `NativeDialogTestKit.ActivationScope.Dispose` <- `RunBenchmark` (dòng `styles?.Dispose()`).
- `StartupCloakTests.SafetyTimeout_RevealsAWindowThatNeverGetsAFrame` đỏ ở run master 38422570285 (#500): `hạn chót an toàn không bỏ
  cloak` sau 9 s (chờ tối đa 10 s cho timer 50 ms).
- Chạy riêng từng test hay chạy cả `Shell.Tests` theo đúng filter CI (7 lượt, 4 lượt có 4 burner CPU nền, ~1623 test/lượt):
  0 đỏ - KHÔNG tái hiện cục bộ. Nguyên nhân dưới đây suy ra từ log CI + thực nghiệm cơ chế (không phải từ một lượt đỏ cục bộ).

## 2. Nguyên nhân gốc

### 2.1 Benchmark: activation context bật và tắt ở hai việc dispatcher khác nhau (chắc chắn: cao, có số đo)

`RunBenchmark` bật visual styles trong một `ui.InvokeAsync`, tạo cửa sổ ở việc thứ hai và tắt ở việc thứ ba. Mỗi việc chạy hoặc
trực tiếp từ `MessageLoop.Run` (khi thấy `HasHighPriorityWork`) hoặc BÊN TRONG WndProc của `WM_APP+1`
(`OnWakeMessage`, khi vòng lặp đang chờ). user32 khôi phục ngăn xếp activation của thread khi WndProc trả về, nên khung bật ở một
lượt WndProc không còn khi lượt sau gọi `DeactivateActCtx` -> `STATUS_SXS_*` -> `SEHException`. Việc rơi vào đường nào là một cuộc
đua giữa thread pool (đăng việc) và UI thread (đang chờ) nên ngẫu nhiên, nhất là trên VM CI chậm.

Thực nghiệm cơ chế (probe tạm, đã xoá): trên `UiThread` thật, `InvokeAsync(ActivateVisualStyles)` rồi `InvokeAsync(Dispose)`, 60
lần: 45 ổn, **15 `SEHException`**. Probe WndProc thuần (bật ở message A, tắt ở message B): luôn `SEHException`; bật + tắt trong cùng một
message: luôn ổn. Các test khác dùng `OnSta`/`using` trong một callback nên không dính.

### 2.2 StartupCloak: Timer thật + chờ có hạn phụ thuộc thread pool (chắc chắn: trung bình - cơ chế rõ, chưa đo được starvation)

`SafetyTimeout_RevealsAWindowThatNeverGetsAFrame` dùng `System.Threading.Timer` 50 ms và `revealed.Wait(10 s)`. Callback của Timer
chạy trên thread pool; lớp test KHÔNG nằm trong `[Collection]` nên chạy song song với hàng trăm test UI (nhiều test chặn thread pool
bằng `Task.Run(...).GetAwaiter().GetResult()`/`WaitAsync`). Lúc khởi động tiến trình (test chạy ở giây thứ ~17 của lượt) pool chỉ
có `ProcessorCount` thread nên callback có thể xếp sau hàng đợi > 10 s. Không thể làm test tất định chỉ bằng cách chờ lâu hơn
(luật AGENT-HYGIENE 5.7: không nới timeout).

## 3. Sửa

- `StartupCloak` nhận seam `scheduleTimeout: Func<TimeSpan, Action, IDisposable>` (mặc định = `Timer` như cũ; hành vi sản xuất
  không đổi). Test tiêm `ManualScheduler` (không đồng hồ, không thread pool): `SafetyTimeout_RevealsAWindowThatNeverGetsAFrame`,
  `Stop_CancelsTheSafetyTimeout...` và test mới `SafetyTimeout_AfterTheFirstPresent_DoesNotRevealASecondTime` đều tất định.
  Đường Timer thật giữ lại ở `SafetyTimeout_WithTheRealTimer_...` với `[Trait("Category","Slow")]` (chỉ `-Slow`).
- `RunBenchmark`: một `ui.InvokeAsync` duy nhất chứa `using ActivateVisualStyles()`, tạo owner/dialog, đo, đóng - activation bật và
  tắt trong cùng một lời gọi. Ghi chú bắt buộc ở `NativeDialogTestKit.ActivateVisualStyles` cho test sau này.

## 4. Kiểm chứng

- Đột biến (thủ công, đã hoàn tác): bỏ hẹn hạn chót trong `Hide` -> `SafetyTimeout_Reveals...` đỏ; `Stop` không Dispose bộ hẹn ->
  `Stop_Cancels...` và `SafetyTimeout_AfterTheFirstPresent...` đỏ.
- Benchmark: KHÔNG có đột biến tất định (đua thời gian); bằng chứng là số đo 15/60 ở 2.1 và việc sửa loại bỏ khả năng trộn hai việc.
- Sau sửa: cả `Shell.Tests` theo filter CI, >= 4 lượt (một nửa có 4 burner), 0 đỏ (số liệu trong mô tả PR).

## 5. Chưa kiểm / rủi ro còn lại

- Chưa xác nhận starvation thread pool là nguyên nhân duy nhất của lỗi cloak (không có số đo trên VM CI); việc sửa loại bỏ sự phụ
  thuộc thread pool nên hết lỗi bất kể nguyên nhân là pool hay GC/lịch CPU.
- Các ca "rò GDI/USER" (`NativeControlsTests`, `DiagnosticsAndListDialogTests`, `ZoomPromptDialogTests`) ghi ở
  NOWPF-WP35-G3B-PAGE-MOUSE mục 5 KHÔNG nằm trong phạm vi sửa này (không có đỏ nào trong 7 lượt cục bộ).
- `NativeDialogSnapshotTests.WriteSnapshots_OfActionProfilesAndBenchmark` còn bật styles ở một việc và không tắt (chỉ chạy khi có biến
  môi trường `PHOTOREVIEW_DIALOG_SNAPSHOTS`, không vào CI).
