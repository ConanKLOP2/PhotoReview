---
id: CI-PARALLEL-TEST-JOBS
order: 1220
summary: |-
  CI: job `build-test-publish` tách thành job test song song (đã merge #502, 2026-10-13) + `build-publish`; `build-test-publish` còn là job tổng hợp (cấu trúc sau đó được thay bằng CI-OPTIMIZE-ROUND2 #508).
---

# CI-PARALLEL-TEST-JOBS - tách bước test của ci.yml theo RUNNER (2026-10-13)

Trạng thái: **ĐÃ MERGE #502** (cập nhật 2026-10-14). Cấu trúc 3 job test ở mục 2 đã được thay bằng CI-OPTIMIZE-ROUND2 (#508): 6 job test song song + `compile-gate`; mục 2 giữ làm lịch sử. Hoàn tác: revert PR.

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

Run 38411060424 (master, tuần tự): 730 s. PR #502, run 38423595823 (cùng bộ test):

| Job | Lần 1 | Lần 2 |
|---|---|---|
| `build-publish` | 243 s | 242 s |
| `test-core` | 311 s | 338 s |
| `test-integration` | 306 s | 313 s |
| `test-shell` | 396 s | 327 s (đỏ do flaky Shell.Tests `NativeDialogChromeTests`, xanh khi rerun) |
| Tổng thời gian tường tới `build-test-publish` | ~410 s | ~ 390 s (tới lúc job test-shell dừng) |

Số test (so master 56c6b6a4, cùng commit nền): Architecture 159 (158 + 1 luật CI-PARALLEL mới), Core 3202, Imaging 4181, App 2583,
Integration 1344 + 17 Slow, Shell 1609, Shell.Integration 151, WpfBridge 39: không project nào mất test.
Job `test-shell` quyết định thời gian tường; Shell.Tests dao động 98-144 s. Hướng tối ưu thêm: tách Shell.Tests sang job riêng.
