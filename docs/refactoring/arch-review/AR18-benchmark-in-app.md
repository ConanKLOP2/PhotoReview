# AR18 — `BenchmarkWindow` và assembly Benchmarking trong app sản phẩm

**Finding:** F22 · **Quyết định:** Q-AR9 · **Kích thước:** (a) ~30 phút (haiku); (b) ~1 ngày · **GUI:** không

## Hiện trạng

- `src/PhotoReview.App/PhotoReview.App.csproj:22-26` tham chiếu `PhotoReview.Benchmarking` (971 dòng, `UseWPF`), kéo theo `PhotoReview.PerfAnalysis` (1408 dòng). Consumer duy nhất trong App: `BenchmarkWindow.xaml.cs` (`using PhotoReview.Benchmarking;`), mở qua menu → `MainViewModel.ShowBenchmark` → `WpfDialogService.ShowBenchmark` (`:111-113`).
- **Đã kiểm chứng:** `App.xaml.cs`, `AppHost`, `MainViewModelCompositionRoot` không tham chiếu kiểu nào của Benchmarking → CLR nạp `PhotoReview.Benchmarking.dll`/`PerfAnalysis.dll` **lười**, khi `ShowBenchmark` được JIT/thực thi lần đầu. Chi phí khởi động ≈ 0; chi phí là kích thước publish (~2 assembly) và bề mặt bảo trì.
- Tách project đã hợp lý: `Benchmark.Cli` và `Integration.Tests` dùng chung `Benchmarking`; `PerfAnalysis` không phụ thuộc gì.

## Q-AR9 — phương án

| | (a) Giữ, ghi nhận là tính năng | (b) Nạp theo yêu cầu qua assembly riêng | (c) Chuyển Benchmark ra ngoài app (chỉ CLI) |
|---|---|---|---|
| Việc | `docs/architecture.md` sơ đồ project: chú thích "Benchmarking: cửa sổ Benchmark trong app (menu) + CLI; nạp lười, không ảnh hưởng khởi động (AR18)"; `README` mục tính năng nếu chưa có | Tách `BenchmarkWindow` sang `PhotoReview.App.Benchmark` (WPF class lib), App nạp bằng `AssemblyLoadContext`/`Type.GetType(string)` — đúng pattern F1 (AR01) đã **loại bỏ** vì "chọn được nhưng không có trong release" | Xoá `BenchmarkWindow`, menu, `IDialogService.ShowBenchmark`, i18n keys; người dùng chạy `Benchmark.Cli` |
| Công | 30 phút | 1 ngày | 0.5 ngày |
| Rủi ro | 0 | Tái tạo lỗi kiểu AR01 F1 (release thiếu assembly → menu chết im lặng); `verify-release.ps1` phải kiểm thêm | Mất tính năng người dùng có thể đang dùng (Q-R6 accessibility đã đầu tư cho Benchmark best-effort → được coi là tính năng) |
| Lợi ích | Không còn câu hỏi "vì sao app ship benchmark" | Publish nhỏ hơn ~0.3 MB; không đổi khởi động (đã lười sẵn) | Bề mặt nhỏ nhất |

**Khuyến nghị: (a).** Không có chi phí runtime đo được; (b) lặp lại chính lỗi mà AR01 sửa; (c) bỏ tính năng đã có accessibility.

## Nếu chọn (a) — thay đổi

1. `docs/architecture.md` "Các project và hướng phụ thuộc": thêm dòng dưới `PhotoReview.Benchmarking (benchmark dùng chung)`: "— cũng là backend của cửa sổ Benchmark trong app; assembly chỉ nạp khi mở cửa sổ (AR18, Q-AR9 a)".
2. Test kiến trúc nhỏ (tuỳ chọn, giữ đúng tính "nạp lười"): trong `AppCompositionTests`, sau `AppHost.BuildServices` + resolve `MainViewModel`, `AppDomain.CurrentDomain.GetAssemblies()` **không** chứa `PhotoReview.Benchmarking` (chạy trong process test riêng lẻ có thể đã nạp bởi test khác → dùng `[Collection]` riêng hoặc kiểm tra bằng `Process` con; nếu không ổn định thì bỏ test, chỉ ghi docs).

## Verification / Acceptance

`tools/docs-budget.ps1 -Check`; Q-AR9 ghi trên `master` (PR docs, AGENTS.md "Decision log").
