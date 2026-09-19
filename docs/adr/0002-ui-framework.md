# ADR 0002: Giữ WPF làm framework UI

- **Trạng thái:** Accepted — No-go cho spike WinUI 3 ở giai đoạn này
- **Ngày:** 2026-09-19
- **Phạm vi:** PhotoReview desktop UI sau đợt refactor W7

## Bối cảnh

T71 cần quyết định có mở một spike WinUI 3 hay tiếp tục với WPF. Quy tắc R-UI yêu cầu dùng tỷ lệ thời gian `UiAssign/Present` so với `Decode` ở P95 của chuỗi `key → present`. Ngưỡng mở spike là UI chiếm ít nhất 40%. ViewModel đã tách khỏi view nhờ K-2 nên việc thử framework khác là khả thi về mặt kiến trúc, nhưng vẫn phải có bằng chứng runtime đủ phân biệt thời gian render thật với overhead của harness.

## Bằng chứng trước và sau refactor

D12 ghi nhận S3 warm có `t_render` khoảng 80% trở lên trong nhóm điều hướng nhanh. Báo cáo đồng thời nêu rõ đây là số đo qua harness và cần D08/PresentMon để tách render thật khỏi routed-event/test timing; vì vậy `t_render` không được coi là `UiAssign/Present` đã đo trực tiếp.

T66 giữ được hành vi sau T87: S3 F1 có final P95 8,5 ms, hit rate 98,6%, 200/200 phím và không có lỗi. Phân rã S3 cho thấy render chiếm khoảng 81,7% trong số liệu harness. Ngược lại, các nhóm chậm S2 và S9 chủ yếu là decode và thumbnail (khoảng 50,4%/42,1% và 52,9–57,5%/39,7–45,6%); đây không phải dấu hiệu nút thắt UI. T66 không có cặp counter `UiAssign/Present` và `Decode` ở cùng P95, nên chưa thể tính R-UI theo định nghĩa của task.

## Các lựa chọn

1. **Spike WinUI 3 ngay:** không đủ điều kiện vì ngưỡng R-UI chưa được chứng minh bằng counter render thật; chi phí chuyển framework và rủi ro native/interop sẽ không giải quyết nút thắt decode/thumbnail đã quan sát.
2. **Giữ WPF (quyết định):** giữ backend WPF mặc định và các lớp ViewModel/decoder hiện tại; dùng D08/PresentMon khi cần xác minh render.
3. **Chuyển framework ngay:** loại vì vượt quá bằng chứng và phạm vi của T71.

## Quyết định

**No-go WinUI 3 ở hiện tại. Giữ WPF làm framework UI mặc định.** Không dùng tỷ lệ `t_render` của harness làm R-UI; số liệu đó chỉ là tín hiệu để điều tra tiếp. Quyết định này không ngăn việc tiếp tục tối ưu pipeline decode, thumbnail, cache và lifecycle hiện có.

## Hệ quả

- Không phát sinh spike, nhánh chuyển framework, hay thay đổi dependency UI trong W7.
- T73 tiếp tục kiểm tra GUI trên WPF, bao gồm đổi decoder khi đang xem và trình diễn lại ảnh.
- D08/PresentMon chỉ cần chạy khi cần bằng chứng render độc lập; không làm chậm các task release hiện tại.
- T66 giữ vai trò baseline đã kiểm chứng cho các so sánh sau này; mọi kết luận về UI phải ghi rõ nếu đến từ harness hay từ PresentMon/ETW.

## Điều kiện mở lại

Mở lại ADR khi một phép đo runtime có cùng workload và điều kiện warm/cold ghi được `UiAssign/Present` và `Decode` ở P95, có `errors=[]`, `idleTimeouts=0`, và cho thấy UI chiếm **≥ 40%** thời gian `key → present`; hoặc khi có lỗi WPF tái hiện được mà một spike WinUI 3 có khả năng giải quyết. Khi đó tạo task spike riêng với tiêu chí so sánh thời gian present, bộ nhớ, input/focus, decoder switching, DPI và publish/native packaging.

## Liên kết bằng chứng

- D12: `docs/refactoring/diagnosis/REPORT.md`
- T66: `docs/refactoring/results/final.md`
- T87: commit `ec120fd` (decoder setting/fallback, WPF default)
