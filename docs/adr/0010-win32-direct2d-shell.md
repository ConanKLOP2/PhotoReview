# ADR 0010: Shell Win32 + Direct2D (`PhotoReview.exe`) song song với bản WPF

- **Trạng thái:** Accepted (đợt NO-WPF, 2026-10; WP-31). Bổ sung/thay một phần [ADR 0002](0002-ui-framework.md) (giữ WPF làm framework UI duy nhất) và [ADR 0004](0004-presentation-project-separation.md) (không tách phần dùng chung khỏi App).
- **Phạm vi:** UI của PhotoReview: cửa sổ xem ảnh, cửa sổ phụ, đóng gói, luật kiến trúc.

## Bối cảnh

ADR 0002 giữ WPF vì chưa có bằng chứng UI chiếm >= 40 % chuỗi `key -> present`. Đợt NO-WPF dựng một shell Win32 + Direct2D chạy thật: ảnh đầu ~470 ms so với WPF ~1.200 ms, working set -16 % (đo 2026-10-12, [handoff](../refactoring/decisions/NOWPF-HANDOFF-20261010.md) mục 0). ADR 0004 không tách project vì mọi consumer vẫn cần `PhotoReview.App`; hợp đồng NO-WPF (C-01..C-16) đã tách được ViewModel/coordinator/composition ra `App.Shared` mà không đổi hành vi.

## Quyết định

1. **Hai exe trong một thư mục** (NE-2 = a): `PhotoReview.App.exe` (WPF, mặc định, liên kết tệp hiện có) và `PhotoReview.exe` (assembly `PhotoReview.Shell.Win32`: cửa sổ Win32 + renderer Direct2D). Prefix instance khác nhau (`PhotoReview` / `PhotoReviewShell`) nên chạy cạnh nhau được. "Open with" cho shell là opt-in (`-Variant Shell`, ProgId `PhotoReview.Shell`).
2. **Phần dùng chung không WPF:** `App.Shared` (ViewModel, coordinator, input, composition `AddPhotoReviewShared`), `Imaging.*` (PixelBuffer + WIC; chỉ `Imaging.Wpf` dùng WPF), `Core`, `Platform.Windows`. Phần shell: `Shell.Interop` (P/Invoke, COM source-generated), `Shell.Rendering` (Direct2D), `Shell.Win32`.
3. **Cửa sổ phụ của shell là hộp thoại Win32 gốc** (`Shell.Win32/Dialogs`): Settings 7 trang, Recovery, Diagnostics, ZoomPrompt, BatchReview, Skipped, ActionProfiles, Benchmark. Cầu WPF (`Shell.WpfBridge`) đã gỡ khỏi shell ở WP-35 GĐ5 (#500); bản WPF giữ cửa sổ phụ riêng trong `App.WpfWindows`.
4. **Luật kiến trúc** (Architecture.Tests): L-SHARED, L-IMG, L-WPF, L-SHELL, L-AOT; chi tiết ở [architecture.md](../architecture.md).
5. **Tương đương với WPF** được giữ bằng golden dùng chung (`tests/Fixtures/golden`), không bằng đọc mã.
6. **Native AOT** của `PhotoReview.exe` là artifact tùy chọn (`tools/publish-release.ps1 -Aot`/`-RequireAot`, cần MSVC + Windows SDK), không nằm trong zip phát hành. Vì không còn cầu WPF, nó có cùng bộ cửa sổ phụ với bản R2R.

## Hệ quả

- Bản WPF vẫn có trong gói cho tới khi người dùng quyết định xoá (WP-37, chưa làm); `Shell.WpfBridge` chỉ còn test dùng.
- Mỗi thay đổi hành vi UI phải đúng ở cả hai exe, hoặc khác biệt được ghi trong `NOWPF-UNCONFIRMED-DECISIONS-*`.
- ADR 0005 (UI thread affinity) áp dụng cho cả hai: trong shell, "UI thread" là luồng của `MessageLoop`/`Win32UiSynchronizationContext`.
