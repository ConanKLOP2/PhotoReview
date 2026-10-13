---
id: FLAKY-INTEGRATION-APP
order: 1310
summary: |-
  CHƯA XÁC NHẬN: 5 test Integration/App.Tests đỏ ngẫu nhiên khi máy tải - StaTestHostSmoke (trần 10 s đồng hồ thật), Startup prewarm (kiểm "đã bắt đầu" ngay sau Show khi decode chạy trên pool), Navigation seed (Flush bỏ sót ghi đang bay của SessionWriter), DarkScrollBar (Join 15 s) sửa tận gốc; AppForwardedOpen A3 chưa tái hiện được, giữ nguyên.
---

# FLAKY-INTEGRATION-APP - test Integration/App.Tests đỏ ngẫu nhiên (2026-10-14, CHƯA XÁC NHẬN)

Tái hiện có kiểm soát: máy 12 nhân, test chạy với `start /affinity 3` (2 nhân) cùng 4 tiến trình đốt CPU cùng affinity, `run-tests-hidden.ps1`
`--blame-hang-timeout 5m`. Chạy riêng 3 lượt trước khi sửa: xanh hết (không tự đỏ ở 2 nhân), nên mỗi nguyên nhân được chứng minh bằng độ trễ giả
đặt đúng điểm nghi ngờ (tạm thời, đã gỡ), trước/sau khi sửa.

| Test | Nguyên nhân gốc | Mức chắc chắn | Sửa | Chứng minh |
|---|---|---|---|---|
| `StaTestHostSmokeTests.RealMessageBox_*` | `Assert.True(sw.Elapsed < 10 s)`: trần đồng hồ thật quanh cả khởi động lạnh của WPF (cửa sổ đầu tiên của tiến trình) | Cao (log CI 11,9 s, hết lý do khác) | Bỏ trần; chờ có giới hạn treo (`Task.WhenAny`, 120 s) và khẳng định guard vẫn đóng hộp thoại + nêu đúng chữ | Trễ giả 11 s trong hook guard: 3/3 đỏ trước, 3/3 xanh sau |
| `StartupFirstShow...Show_WithALaunchFile_StartsItsDecode...` | `InitialImagePrewarm` đăng ký decode in-flight trong `Task.Run` (pool, vì stat file không được chạy trên UI); test khẳng định `HasInflight` NGAY khi `Show()` trả về | Cao (cơ chế chứng minh được) | `SpinWait.SpinUntil` có điều kiện (Deadline 20 s), KHÔNG nhả dispatcher nên presenter không thể tự đăng ký key: ý nghĩa "do lúc Show, không do presenter" giữ nguyên | Trễ giả 300 ms trong Task.Run: 71/100 đỏ trước (phần còn lại bị che do Show chậm), 100/100 xanh sau |
| `MainViewModelNavigationTests` "Random navigation ... seed" | Bước Reopen gọi `SessionWriter.Flush()` chỉ ghi phần còn *pending*; batch mà thread timer đã rút (đang ghi dở) không được chờ, rồi `OpenFolderAsync` đọc file session đang bị thay -> mất skip/vị trí | Trung bình-cao (cơ chế khớp; cùng họ với `LoadSettledSessionAsync` đã có) | `await FlushAsync(); await WhenIdleAsync();` trước Reopen | Debounce 1 ms + trễ 15 ms sau drain: 1/60 đỏ trước (Skipped 3 vs 2), 200/200 xanh sau |
| `DarkScrollBarRenderingTests` | `thread.Join(15 s)` cố định quanh lần render WPF lạnh trên thread STA riêng | Trung bình (1 lần CI, không tái hiện) | Bound hang 120 s + chẩn đoán (thời gian chờ, ThreadState), thread nền | Phân tích mã; 10 lượt tải nền xanh |
| `ExplorerOrderServiceSeamTests` "Timeout: caller gets TimedOut..." (phát hiện ở CI run 38447347034, không do PR này) | Timeout `TimeSpan.Zero` chạy đua với thread pump: nếu cancel tới trước, pump trả Canceled mà KHÔNG gọi query (token đã huỷ) nên `sawCancel` không bao giờ xảy ra (30 s) | Cao (đọc mã `ExplorerOrderService` dòng "already cancelled"; chưa tái hiện) | Timeout 3 s thay cho Zero: query chạy trước, rồi mới bị huỷ | Phân tích mã |
| `AppForwardedOpenTests.OpenForwarded_ClosedWindow_...` (A3) | CHƯA TÌM RA: 150 lượt lặp + 3 lượt cả lớp dưới tải (2 nhân + 4 đốt) đều xanh; không còn log CI. Giả thuyết đã loại: join 200 ms của pump Explorer (ghi qua `FileLog.Default`, guard không thấy), pool cạn (đã `SetMinThreads`). Giả thuyết còn lại: `ApplicationIdle` bị đói khi dispatcher bận | Thấp | Không đổi (không sửa bừa) | - |

## Rollback

Chỉ đổi code test (5 file); khôi phục từ git. Không đổi mã sản xuất.
