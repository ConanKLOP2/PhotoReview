---
id: CI-PARALLEL-TEST-JOBS
order: 1220
summary: |-
  CI: job `build-test-publish` tách thành 3 job test song song + `build-publish`; `build-test-publish` còn là job tổng hợp (CHƯA XÁC NHẬN, 2026-10-13).
---

# CI-PARALLEL-TEST-JOBS - tách bước test của ci.yml theo RUNNER (2026-10-13)

Trạng thái: **CHƯA XÁC NHẬN** (đổi workflow CI cần chủ repo xem; PR không bật auto-merge). Hoàn tác: revert PR.

## 1. Vấn đề

Job `build-test-publish` chạy tuần tự trên 1 runner `windows-latest` (2 vCPU): run 38411060424 = 730 s (restore+cache 36 s,
Build Release cả solution 136 s, test ~500 s, Publish 33 s). Tách tiến trình trên 1 runner không có lợi (2 vCPU); tách theo
RUNNER (job riêng) thì có.

## 2. Thay đổi

| Job | Việc | Ghi chú |
|---|---|---|
| `changes` (ubuntu) | PR chỉ đổi `docs/**`, `README.md`, `CLAUDE.md`, `task_on_progress.md` thì `code=false` | push/dispatch luôn `code=true`; `AGENTS.md` KHÔNG thuộc danh sách (Architecture test đọc nó) |
| `build-publish` | Build Release cả `PhotoReview.slnx` (cổng biên dịch, warning = lỗi), Show version, Publish, Verify, upload `release-publish` | không chờ test |
| `test-core` | Architecture, Core, Imaging, App | chỉ build 4 project test này + phụ thuộc |
| `test-shell` | Shell, Shell.Integration, Shell.WpfBridge | |
| `test-integration` | Integration + bước Integration+Slow (17 test, 76 s) | |
| `build-test-publish` | JOB TỔNG HỢP (ubuntu, `if: always()`): thành công chỉ khi `changes` + 4 job trên đều `success` (hoặc cùng `skipped` khi PR chỉ docs) | GIỮ tên: là required check của master và `release.yml` tra tên này trên commit của tag |

- `tag-version` giữ `needs: [build-test-publish, repo-checks]` (ngữ nghĩa cũ: chỉ gắn tag khi mọi thứ xanh). `publish-aot` vẫn không nằm trong needs.
- Giữ nguyên MỌI test, MỌI filter (`TEST_FILTER` chuyển lên `env` cấp workflow), `--blame-hang-timeout 120s`, `--blame-hang-dump-type none`, trx.
  Bước safety-net Integration+Slow chạy theo project của từng job (7 project còn lại "No test matches" ~3 s mỗi cái; giữ để filter
  đổi không làm rơi test).
- Artifact test đổi tên: `test-results` thành `test-results-core|shell|integration` (không có consumer; kiểm bằng grep).
- Không đổi `permissions`, SHA ghim action, `concurrency`.
- Test kiến trúc (`TestFilterDriftTests`): luật "safety-net covers every project" gom mọi bước safety-net của các job; thêm luật
  `CI-PARALLEL` (mỗi project test có đúng 1 job chạy bước chính + bước safety-net; job tổng hợp `needs` mọi job build/test và có `if: always()`).

## 3. Rủi ro

- Mỗi job test restore + build lại phần của nó (không chia sẻ build output giữa job): tổng phút runner tăng, thời gian tường giảm.
- Cache NuGet: mọi job restore cả solution để bộ package giống nhau (job nào lưu cache trước cũng lưu bản đầy đủ).
- Chỉ tính `skipped` hợp lệ khi `changes.code=false`; job bị skip vì lý do khác làm job tổng hợp đỏ.

## 4. Việc cho chủ repo

Required checks hiện là `build-test-publish` và `repo-checks`: KHÔNG cần đổi gì (tên giữ nguyên). Tuỳ chọn: thêm
`test-core`, `test-shell`, `test-integration`, `build-publish` vào required checks để thấy từng job (không bắt buộc vì job tổng hợp
đã bao trùm; lưu ý job bị skip trên PR chỉ docs được GitHub coi là đạt).

## 5. Số đo

(Điền sau khi CI của PR chạy; xem mô tả PR.)
