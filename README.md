# PhotoReview

[English](#english) | [Tiếng Việt](#tiếng-việt)

---

<a name="english"></a>
## English

PhotoReview is a Windows WPF application for browsing, comparing, and organizing photos in Explorer order. The application prioritizes fast image rendering, recoverable file operations, and entirely local data processing.

### Key Features

- Reads file order from Windows Explorer when a valid native snapshot is available; falls back to an internal sorting order during wait times or when Shell is unavailable.
- Features Fast, Preview, and Original modes; bounded RAM/disk image caching; memory pressure checks for preloading.
- Delete operations move files to the Recycle Bin; Move/Copy/Delete actions are journaled to support Undo and recovery.
- Supports side-by-side Compare, optional hash/dimension verification, batch duplicate cleanup with a confirmation step, zoom/Fit/fullscreen views, and keyboard shortcuts.
- Images and file paths are processed locally; internal diagnostics are only enabled when configured.

See [Architecture](docs/architecture.md) and [Image Loading Mechanisms, Safety Invariants, and Benchmark Guide](docs/APP-MECHANISMS-VI.md) before modifying the pipeline or interpreting performance metrics.

### Requirements

- Windows 10/11 x64.
- .NET 10 Windows Desktop Runtime for the framework-dependent build; self-contained builds include the runtime.

### Build, Test, and Publish

```powershell
dotnet build PhotoReview.slnx -c Release
.\tools\verify-all.ps1
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

`src/PhotoReview.App/bin/Release/net10.0-windows/publish` (framework-dependent) is the only supported release folder; CI builds it and uploads it as the `release-publish` artifact. Verification for self-contained builds uses separate scripts and paths in `tools/`; a successful build/test pass does not replace runtime benchmarks or GUI acceptance testing.

**Releasing:** CI tags every merge to `master` (`v2.0.N`) and then automatically creates a **draft** GitHub Release for that tag (framework-dependent zip, exe SHA256, generated notes). Drafts are invisible to the public: review one under Releases and press **Publish** to release it. Delete drafts you do not want. You can also run the **Release** workflow by hand (Actions → Release → Run workflow) for an existing tag that has no release yet.

Note: `Microsoft.CodeAnalysis.CSharp` (Localization generator, `Directory.Packages.props`) must not be newer than the compiler in the installed .NET SDK; upgrade it only together with the SDK.

### Benchmarks

```powershell
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-list-profiles
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-all 'C:\path\to\image-folder' 'C:\path\to\output-folder'
```

Keep the machine, fixtures, viewport, mode, and cache state consistent when comparing runs. The final runtime refactoring results are documented in [T66](docs/archive/evidence/T66-final.md).

### Performance and Display Settings

- `DecoderBackend`: Defaults to `WicDirect` (fastest, see ADR 0001); can be set to `Wpf` or `TurboJpeg`. Non-WPF backends automatically fall back to WPF when encountering supported codec errors.
- `ScalingQuality`: Defaults to `HighQuality` for visual fidelity; `Linear` reduces rendering overhead during zoom and pan operations.
- `UseSourceBytesCache`: Defaults to `false`. When enabled, the application keeps source file bytes in RAM with a 16 GiB quota to eliminate repeated disk reads; enable only after measuring real workloads.

### File Associations (Optional)

Register "Open with" for `.jpg`, `.jpeg`, and `.png`:

```powershell
.\deploy\install-photo-review-association.ps1 -ExePath 'C:\path\to\PhotoReview.App.exe'
```

Unregister:

```powershell
.\deploy\uninstall-photo-review-association.ps1
```

### Languages

The UI ships in English and Vietnamese (Settings → Language; `auto` follows Windows). Texts live in plain JSON files that anyone can edit or extend without rebuilding — see [docs/TRANSLATING.md](docs/TRANSLATING.md).

| Language | Status |
|---|---|
| English (`en`) | built in, complete |
| Tiếng Việt (`vi`) | complete |

**Add your own language (e.g. Chinese) — no build needed:**

1. Settings → pick a language → **Export strings to translate**. This writes `<code>.todo.json` with every missing key, its English text and translator notes.
2. Create `zh.json` in `%LocalAppData%\PhotoReview\Languages\` (Settings → **Open languages folder**) with a `_meta` block (`"code": "zh"`, `"nativeName": "中文"`, `"plural": "none"` for languages without plural forms) and your translations.
3. Settings → **Reload translations**, then choose the language under **Language**. Untranslated keys fall back to English, so you can translate gradually.
4. Share it: open a pull request that adds the file to `src/PhotoReview.Core/Localization/Languages/`; run `tools/i18n-check.ps1` first.

Details, rules and the Vietnamese glossary: [docs/TRANSLATING.md](docs/TRANSLATING.md).

### Known Limitations

Explorer ordering depends on open folder windows and valid Shell snapshots; fallback ordering is used if Explorer is not ready or if the snapshot encounters an error or timeout. Contract tests do not guarantee GUI behavior, perceived first-image latency, or P95 timings; these conclusions require controlled runtime measurements.

---

<a name="tiếng-việt"></a>
## Tiếng Việt

PhotoReview là ứng dụng Windows WPF để duyệt, so sánh và phân loại ảnh theo thứ tự Explorer. Ứng dụng ưu tiên hiển thị ảnh nhanh, giữ thao tác file có thể phục hồi và xử lý dữ liệu cục bộ.

### Điểm chính

- Đọc thứ tự file từ Windows Explorer khi snapshot native hợp lệ; dùng thứ tự fallback trong lúc chờ hoặc khi Shell không sẵn sàng.
- Có chế độ Fast, Preview và Original; cache ảnh trong RAM/đĩa có giới hạn, preload có kiểm tra áp lực bộ nhớ.
- Delete chuyển file vào Recycle Bin; Move/Copy/Delete được ghi journal để hỗ trợ Undo và recovery.
- Hỗ trợ Compare, kiểm tra hash/kích thước tùy chọn, batch duplicate có bước xác nhận, zoom/Fit/fullscreen và phím tắt.
- Ảnh và đường dẫn được xử lý cục bộ; diagnostics nội bộ chỉ bật theo cấu hình.

Xem [kiến trúc](docs/architecture.md) và [cơ chế load ảnh, bất biến an toàn, hướng dẫn benchmark](docs/APP-MECHANISMS-VI.md) trước khi sửa pipeline hoặc diễn giải kết quả hiệu năng.

### Yêu cầu

- Windows 10/11 x64.
- .NET 10 Windows Desktop Runtime cho bản framework-dependent; bản self-contained kèm runtime.

### Build, test và publish

```powershell
dotnet build PhotoReview.slnx -c Release
.\tools\verify-all.ps1
dotnet publish src/PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o src/PhotoReview.App/bin/Release/net10.0-windows/publish
.\tools\verify-release.ps1 -ReleaseDirectory 'src/PhotoReview.App/bin/Release/net10.0-windows/publish'
```

`src/PhotoReview.App/bin/Release/net10.0-windows/publish` (framework-dependent) là thư mục release duy nhất được hỗ trợ; CI build và upload thư mục này thành artifact `release-publish`. Verification cho self-contained dùng scripts và đường dẫn riêng trong `tools/`; một lần build/test thành công không thay thế benchmark hoặc GUI acceptance.

**Phát hành:** CI gắn tag cho mỗi lần merge vào `master` (`v2.0.N`) rồi tự tạo một **bản nháp (draft)** GitHub Release cho tag đó (zip framework-dependent, SHA256 của exe, release notes tự sinh). Bản nháp không hiện công khai: vào Releases xem lại rồi bấm **Publish** để phát hành, bản không cần thì xoá. Vẫn có thể chạy tay workflow **Release** (Actions → Release → Run workflow) cho một tag chưa có release.

Lưu ý: `Microsoft.CodeAnalysis.CSharp` (generator Localization, `Directory.Packages.props`) không được mới hơn compiler của .NET SDK đang cài; chỉ nâng cùng lúc với SDK.

### Benchmark

```powershell
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-list-profiles
dotnet run --project tools/PhotoReview.Benchmark.Cli/PhotoReview.Benchmark.Cli.csproj -c Release -- --benchmark-all 'C:\duong-dan\folder-anh' 'C:\duong-dan\ket-qua'
```

Giữ nguyên máy, fixture, viewport, mode và trạng thái cache khi so sánh. Kết quả runtime cuối của đợt refactor nằm trong [T66](docs/archive/evidence/T66-final.md).

### Cài đặt hiệu năng và hiển thị

- `DecoderBackend`: `WicDirect` mặc định (nhanh nhất, xem ADR 0001); có thể chọn `Wpf` hoặc `TurboJpeg`. Backend khác WPF tự fallback về WPF với lỗi codec được hỗ trợ.
- `ScalingQuality`: `HighQuality` mặc định cho chất lượng hiển thị; `Linear` giảm chi phí khi zoom/chuyển khung.
- `UseSourceBytesCache`: mặc định `false`. Khi bật, ứng dụng giữ byte nguồn trong RAM với quota 16 GiB để giảm đọc đĩa lặp lại; chỉ nên bật sau khi đo trên workload thực.

### File association (tùy chọn)

Đăng ký Open With cho `.jpg`, `.jpeg`, `.png`:

```powershell
.\deploy\install-photo-review-association.ps1 -ExePath 'C:\duong-dan\PhotoReview.App.exe'
```

Gỡ đăng ký:

```powershell
.\deploy\uninstall-photo-review-association.ps1
```

### Ngôn ngữ

Giao diện có English và Tiếng Việt (Cài đặt → Ngôn ngữ; `auto` theo Windows). Chữ nằm trong các file JSON thường, ai cũng sửa hoặc thêm ngôn ngữ được mà không cần build — xem [docs/TRANSLATING.md](docs/TRANSLATING.md).

**Thêm ngôn ngữ của bạn (ví dụ tiếng Trung), không cần build:**

1. Cài đặt → chọn ngôn ngữ → **Xuất chuỗi cần dịch**: tạo `<mã>.todo.json` gồm các key còn thiếu, câu English và ghi chú.
2. Tạo `zh.json` trong `%LocalAppData%\PhotoReview\Languages\` (Cài đặt → **Mở thư mục ngôn ngữ**) có khối `_meta` (`"code": "zh"`, `"nativeName": "中文"`, `"plural": "none"` cho ngôn ngữ không có số nhiều) và bản dịch.
3. Cài đặt → **Tải lại bản dịch**, rồi chọn ngôn ngữ trong **Ngôn ngữ**. Key chưa dịch tự hiện English nên dịch dần được.
4. Chia sẻ: mở pull request thêm file vào `src/PhotoReview.Core/Localization/Languages/`; nên chạy `tools/i18n-check.ps1` trước.

Chi tiết, quy tắc và bảng thuật ngữ: [docs/TRANSLATING.md](docs/TRANSLATING.md).

### Giới hạn cần biết

Thứ tự Explorer phụ thuộc cửa sổ/folder và snapshot Shell hợp lệ; fallback vẫn được dùng nếu Explorer chưa sẵn sàng hoặc snapshot lỗi/timeout. Contract tests không chứng minh GUI behavior, cảm nhận first-image latency hay P95; các kết luận đó cần phép đo runtime có kiểm soát.
