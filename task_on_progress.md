# Tiến độ

- **Cập nhật:** 2026-09-16 | **Branch:** `feature/wave3-architecture-testing`
- **Mục tiêu:** bỏ comment dư; giữ giải thích hành vi và test.
- **Đã rà:** hai lượt, mỗi lượt 3 reviewer độc lập cho App, CLI tests, xUnit tests.
- **Đã đổi:** bỏ comment lặp code/tên model, boilerplate ThemeInfo, fallback lặp và lịch sử migration/WP; rút gọn giải thích benchmark. Sửa assertion title để kiểm tra code thật.
- **File:** App bootstrap/cache/models/benchmark/title; CLI runner; unit test summaries/assertions.
- **Kiểm tra:** CLI Release đạt; xUnit trước commit đạt 190/190. Sau commit, full xUnit hai lần đạt 189/190 do quota prune nền; chạy riêng case đó đạt 1/1. Chỉ thay comment/assertion source-presence, không đổi logic cache.
- **Commit:** `Trim redundant code comments` (cùng commit này cập nhật tiến độ).
- **Còn lại:** chạy lại gate CLI Release và publish sau cập nhật này, sau đó push branch theo quy trình repo.
- **Cho AI tiếp theo:** giữ comment về contract, race, fixture, giới hạn WPF/Windows và source-presence rationale.
