# Test Parity Matrix (CLI vs xUnit) — ARCHIVED

**Ngày lập:** 2026-09-16 · **Task:** T10 — Ma trận parity test  
**Status:** DONE (2026-09-19, xem T47/T67 outcome)

---

## Mục tiêu (T10)

Ghi lại mapping giữa:
1. CLI checks (147 `Check(…)` trong `PhotoReview.Tests/Program.cs`)
2. xUnit tests (188 `[Fact]` trong `PhotoReview.Tests.Unit/`)

Kết quả: toàn bộ checks đã được gộp → một bộ test xUnit duy nhất.

---

## Bảng 1: Tổng kết mapping

| Nhóm | Count | Status |
|------|-------|--------|
| CLI checks | 146 (sau khi trừ định nghĩa `static void Check(...)`) | ✅ COVERED 146/146 |
| xUnit tests khớp | 146 | ✅ Exact match 1-1 |
| xUnit tests bổ sung | +42 | ✅ New coverage |
| **Total xUnit** | **188** | ✅ |
| Thiếu (MISSING) | 0 | ✅ None |
| Bỏ được (DROP) | 4 (xem Bảng 3) | ✅ Replaced |

**Kết luận T11:** không có việc phải làm — mọi check CLI đã có test xUnit khớp. T11 chỉ xác nhận.

---

## Bảng 2: 5 file CLI trùng (xUnit)

| File | CLI checks | xUnit tests | Hành động |
|------|----------|------------|----------|
| BenchmarkScenarioTests | 5 | 5 (khớp 1-1) | ✅ **Xóa CLI file T12**, bỏ `Program.cs` dòng 99 |
| CacheExplorerRegressionTests | 12 | 12 (khớp 1-1) | ✅ **Xóa CLI file T12**, bỏ `Program.cs` dòng 98 |
| FileActionConcurrencyTests | 4 | 4 (khớp 1-1) | ✅ **Xóa CLI file T12**, bỏ `Program.cs` dòng 97 |
| ImageCacheKeyTests | 4 (khớp) + 2 (inline) | 6 | ✅ **Xóa CLI file T12**, bỏ `Program.cs` dòng 96; giữ 2 check inline |
| PerformanceTestHarness | (shared harness, 1 file) | (shared harness, 1 file) | ✅ **Giữ tới T50b** — CLI vẫn dùng `Program.cs` dòng 104–105 |

**T12 outcome:** xóa 4 file CLI (BenchmarkScenarioTests, CacheExplorerRegressionTests, FileActionConcurrencyTests, ImageCacheKeyTests) + 4 dòng lời gọi.

---

## Bảng 3: 69 test SourcePresenceTests — phân loại

| Phân loại | Count | Hành động |
|-----------|-------|----------|
| REPLACE-BY task (T14b, T14d, T43a, T45b/c, T46a/b/c, T31a/b, T33b, T25a, T64) | 58 | ✅ Sẽ được test hành vi trong các task khác |
| DROP (đã có test hành vi thay thế) | 4 | ✅ Tên test thay thế xác minh tồn tại: `NaturalFilenameSort*`, `SizeSort*`, `FileHashService*` |
| KEEP-XAML (kiểm `.xaml` + accessible names + lifecycle) | 6 | ⚠️ **Vượt trần 5** — T47 gộp 3 accessible-name test thành 1 (`XDocument` parse) → còn 4 |
| KEEP-SCRIPT (file registry script) | 1 | ✅ Vĩnh viễn, không tính vào giới hạn KEEP-XAML |

**T47 outcome (2026-09-19):**
- Xóa 58 test REPLACE-BY + 4 DROP + gộp 3 accessible-name test → 12 test xUnit lại (3 XAML + 9 source-presence), từ 69 → 12
- `SourcePresenceTests`: 69 → 12 PASS
- `Tests.Unit` total: 257 → 199 test
- Cũng xóa 2 check source-presence từ `CacheExplorerRegressionTests`

**T67 outcome (2026-09-19):**
- Xóa RAM marker + file shim chết `ImageSortService.cs`
- `SourcePresenceTests`: 12/12 PASS
- `verify-all.ps1` PASS

---

**Full historic data (146 checks + 69 tests):** see git history at T10–T12 commits.  
**Live test status:** [`../../ACTIVE-TASKS.md`](../../ACTIVE-TASKS.md) → TC/OC tasks.
