# Tiến độ

- **Cập nhật:** 2026-09-16 | **Branch:** `docs/refactor-plan` (tách từ `master` @ `86282cd`)
- **Mục tiêu:** phân tích cấu trúc project, đề xuất hướng tái cấu trúc, lập plan và task chi tiết cho multi-agent.
- **Đã rà:** toàn bộ `PhotoReview.App` (MainWindow, services, cache, Explorer, journal, settings, benchmark), hai project test, `tools/*.ps1`, `AGENTS.md`, `README.md`, `outputs/APP-MECHANISMS-VI.md`.
- **Đã thêm:** `docs/refactoring/REFACTOR-PLAN.md` (hiện trạng, 13 vấn đề P1–P13, 3 hướng A/B/C, kiến trúc đích, bất biến INV-1…11, đề xuất hiệu năng R-1…7, mô hình multi-agent, rủi ro/rollback) và `docs/refactoring/REFACTOR-TASKS.md` (T00–T74 theo 8 wave, có trạng thái, phụ thuộc, cờ song song, phạm vi file, tiêu chí hoàn thành).
- **Không đổi:** source, cấu hình, cấu trúc dữ liệu.
- **Vướng mắc/chờ người dùng:** xác nhận plan và 6 câu hỏi ở mục 11 của plan (hướng, benchmark trong app, đường dẫn publish, quy trình git, R-4, thư viện ngoài). `AGENTS.md` yêu cầu push `master`, nhưng lịch sử dùng PR (T05).
- **Còn lại:** toàn bộ task T00–T74 ở trạng thái `TODO`. Không được bắt đầu trước khi plan được duyệt.
- **Cho AI tiếp theo:** đọc `REFACTOR-PLAN.md` mục 4.5 và 7 trước khi nhận task. Giữ comment về contract, race, fixture, giới hạn WPF/Windows. Lỗi flaky quota prune nền (189/190) đã giao cho T13.
