# PhotoReview — Task chẩn đoán hiệu năng (D00–D13) — ARCHIVED

**Plan:** [`PERF-DIAGNOSIS-PLAN.md`](PERF-DIAGNOSIS-PLAN.md) · **Cập nhật:** 2026-09-16  
**Status:** Completed. All D00–D13 performed; results fed into refactoring priority.

---

## Quy trình chung

- Một task mỗi phiên, chỉ sửa file trong **Files**, chạy VERIFY, ghi nhật ký theo mẫu
- Model assignment: Haiku 4.5 (D00, D01, D02, D07), Sonnet 5 (D03, D05, D08–D11), Opus 5 (D04, D06, D12, D13)
- Review: R1 (Haiku), R2 (Sonnet), R3 (Opus)
- Branch: `diag/<ID>-<slug>` từ `refactor/integration`
- Layout: cũ (trước T24) — `PhotoReview.App/`, `PhotoReview.Tests/`, `PhotoReview.Tests.Unit/`

**Quy tắc riêng:**
1. Không tải công cụ/thay đổi cài đặt hệ thống nếu chưa hỏi người dùng. Admin steps (RAMMap, reboot) do người dùng.
2. Nếu chưa có env var chẩn đoán → không đổi hành vi app, test hiện có phải pass.
3. Không commit ảnh thật, đường dẫn cá nhân, file > 5 MB.
4. **Không bao giờ** `SendInput`, `SendKeys`, `SetForegroundWindow`, clipboard. Điều khiển trong process hoặc routed event.

---

## Bảng tổng quan

| ID | Tên | Phụ thuộc | ∥ | Sửa | Status |
|----|-----|----------|---|-----|--------|
| D00 | Setup env, fixture, công cụ | T00 | | Không | DONE |
| D01 | AppLog analysis script | D00, D06 | ∥1 | Script | BLOCKED (waits D06) |
| D02 | File access count script | D00, D06 | ∥1 | Không | BLOCKED (waits D06) |
| D03 | EventSource `PhotoReview-Perf` + listener CSV | D00 | ∥1 | Có | DONE |
| D04 | Instrument measurement points | D03 | | Có | DONE |
| D05 | Split read/decode + `--io-decode-split` | D04 | ∥2 | Có | DONE |
| D10 | Override preload worker count / disable disk cache | D04 | ∥2 | Có | DONE |
| D06 | Scenario driver `--perf-session` | D04 | ∥2 | Có | DONE |
| D11 | Parse `--perf-analyze` (CLI) | D04 | ∥2 | Có | DONE |
| D07 | Run scenario matrix (reduced set) | D05, D06, D10, D11 | | Không | DONE |
| D08 | ETW / PresentMon deep-dive | D07 | ∥3 | Không | TODO |
| D09 | GC + memory profiler | D07 | ∥3 | Không | TODO |
| D12 | Report, decisions, priority recommendation | D07, D08, D09, D01, D02 | | Docs | DONE |
| D13 | Update refactor plan/task list per results | D12 | | Docs | DONE |

**Parallel:** ∥1 (D01/D02/D03) · ∥2 (D05/D10/D06/D11, different files) · ∥3 (D08/D09)

---

**Full task details & progress:** see git history at commits around `D00–D13` implementation.  
**Results:** [`task-log-2026-09-21.md`](task-log-2026-09-21.md) and [`../../ACTIVE-TASKS.md`](../../ACTIVE-TASKS.md) (D section).
