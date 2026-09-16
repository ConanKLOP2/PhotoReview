# Tiến độ

- **Cập nhật:** 2026-09-16 | **Branch:** `feature/wave3-architecture-testing`
- **Mục tiêu:** bỏ comment dư; giữ giải thích hành vi và test.
- **Đã rà:** hai lượt, mỗi lượt 3 reviewer độc lập cho App, CLI tests, xUnit tests.
- **Đã đổi:** bỏ comment lặp code/tên model, boilerplate ThemeInfo, fallback lặp và lịch sử migration/WP; rút gọn giải thích benchmark. Sửa assertion title để kiểm tra code thật.
- **File:** App bootstrap/cache/models/benchmark/title; CLI runner; unit test summaries/assertions.
- **Kiểm tra:** CLI Release và publish đạt. xUnit từng đạt 190/190; hai lần full suite sau đó có cùng lỗi quota prune nền (189/190), test riêng đạt 1/1. Chỉ thay comment/assertion source-presence, không đổi logic cache.
- **Commit/push:** `eca670e Trim redundant code comments`, đã push lên `origin/feature/wave3-architecture-testing`.
- **Còn lại:** không có.
- **Cho AI tiếp theo:** giữ comment về contract, race, fixture, giới hạn WPF/Windows và source-presence rationale.
