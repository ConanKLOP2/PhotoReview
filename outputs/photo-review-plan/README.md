# PhotoReview — tài liệu hiệu năng và theo dõi bắt buộc

Tài liệu được giữ lại chỉ phục vụ quy trình bắt buộc, hiệu năng, benchmark và regression.

## Tài liệu bắt buộc

- [TASKS.md](TASKS.md): danh sách task, trạng thái, agent, tiêu chí và bằng chứng.
- [ERROR-HISTORY.md](ERROR-HISTORY.md): lịch sử lỗi và test chống hồi quy.
- [AGENTS.md](../../AGENTS.md): quy tắc lập plan, xác nhận, test, publish và push.

## Tài liệu hiệu năng

- [04-PERFORMANCE.md](04-PERFORMANCE.md): mục tiêu và phương pháp đo.
- [BENCHMARK-PROFILES.md](BENCHMARK-PROFILES.md): registry profile và cách diễn giải.
- [BENCHMARK-RESULTS.md](BENCHMARK-RESULTS.md): bảng số liệu thực tế.
- [BENCHMARK-PLAN-COMPARISON.md](BENCHMARK-PLAN-COMPARISON.md): đối chiếu plan với kết quả.
- [OPTIMIZATION-PLAN.md](OPTIMIZATION-PLAN.md): kế hoạch tối ưu tổng thể.
- [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md): audit function theo tác động hiệu năng.
- [EXPLORER-ORDER-OPTIMIZATION-PLAN.md](EXPLORER-ORDER-OPTIMIZATION-PLAN.md): tối ưu native Explorer order.
- [06-TESTING.md](06-TESTING.md): release gate và ma trận test hiệu năng.
- [APP-MECHANISMS-VI.md](../APP-MECHANISMS-VI.md): mô tả cơ chế runtime liên quan load/cache/navigation.

Mọi kết quả phải ghi rõ folder, số ảnh, dung lượng, profile, workload, P50/P95/P99/max, correctness, trạng thái cold/warm và nguồn read-only hay temp copy.
