# AR12 — Vệ sinh build: `.gitignore` native, warnings-as-errors, ghi chú GC

**Finding:** F13, F14, F15 · **Quyết định:** không cần · **Kích thước:** ~2 giờ, 1 PR `chore/ar12-build-hygiene` (3 commit độc lập) · **GUI:** không · **Agent:** haiku (12a, 12c), sonnet (12b — có thể lộ warning phải sửa)

## AR12a — rule `.gitignore` tường minh cho binary native

**Hiện trạng:** `.gitignore:24` là `x64/` (pattern VS build-output chung) và tình cờ phủ `native/x64/turbojpeg.dll` (3 MB, tải bởi `tools/fetch-native.ps1`, hash ở `native/x64/turbojpeg.sha256`). `git ls-files native/` chỉ có `README.md` và `turbojpeg.sha256`. Ai thu hẹp `x64/` thành `[Bb]in/x64/` sẽ bắt đầu track DLL mà không test/CI nào bắt.

**Thay đổi**

1. Thêm khối vào `.gitignore` (sau phần build results):
   ```gitignore
   # Native binaries are fetched by tools/fetch-native.ps1 (hash-pinned); never commit them
   native/**/*.dll
   native/**/*.pdb
   ```
2. Test kiến trúc `tests/PhotoReview.Architecture.Tests/RepoHygieneTests.cs` (mới, dùng `RepoScan`): `git ls-files` (hoặc quét cây) không có file `*.dll`/`*.exe` ngoài `bin/`/`obj/` — đọc bằng `Process` `git ls-files -z` như các test scan hiện có; nếu `git` không có thì `Skip`.
3. **Mutation:** tạm bỏ 2 dòng ignore, `git add -n native/x64/turbojpeg.dll` phải liệt kê file (kiểm tra tay khi làm; test ở bước 2 chỉ bắt file đã bị track).

## AR12b — warning là lỗi ở Release

**Hiện trạng:** `Directory.Build.props` đặt `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, không `TreatWarningsAsErrors`. Chỉ CA1707 (tests, none) và CA2007 (tầng dưới, error) được ghim ở `.editorconfig:29,36`. AGENTS.md yêu cầu "0 warnings" nhưng CI (`ci.yml:68`) chỉ chạy `dotnet build -c Release --no-restore` — warning mới không làm đỏ CI.

**Thay đổi**

1. `Directory.Build.props`:
   ```xml
   <PropertyGroup Condition="'$(Configuration)' == 'Release'">
     <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
   </PropertyGroup>
   ```
   Chỉ Release để vòng lặp Debug trong IDE không bị chặn bởi warning tạm.
2. Build `dotnet build PhotoReview.slnx -c Release` cả **solution** (kể cả `tools/PhotoReview.Benchmark.Cli`, tests). Nếu đỏ: sửa warning thật; **không** thêm `NoWarn`/`#pragma` mà không có dòng lý do (AGENTS.md "New analyzer suppressions").
3. Thêm dòng vào AGENTS.md "Coding Conventions" mục 6: "Release build treats warnings as errors (AR12b)".
4. **Mutation:** thêm biến không dùng vào một file `src` → `dotnet build -c Release` phải fail; xoá.

## AR12c — ghi chú GC/runtime vào `architecture.md`

**Hiện trạng:** không có `runtimeconfig.template.json`, không `ServerGarbageCollection`/`ConcurrentGarbageCollection`/`TieredPGO`/`InvariantGlobalization` trong cây; `PublishReadyToRun` có comment đo (`PhotoReview.App.csproj:10-14`) nhưng GC thì không.

**Thay đổi**

1. Thêm mục "Runtime và GC" vào `docs/architecture.md` (≤ 6 dòng, giữ file < 24 KB — hiện 17.0 KB): workstation concurrent GC (mặc định) là lựa chọn có chủ đích vì (i) app một cửa sổ, UI latency quan trọng hơn throughput, (ii) buffer pixel của `BitmapSource` nằm ở native heap (MIL) nên heap managed nhỏ, Server GC không giúp; `InvariantGlobalization` **không** bật vì `vi.json` và sắp xếp `CurrentCulture` cần ICU; `TieredPGO`/`TieredCompilation` mặc định .NET 10 (bật). Ghi "đo lại chỉ khi `%` GC pause trong perf session > 1 %".
2. Thêm 2 dòng vào `docs/refactoring/PERF-STATUS.md` mục "Not shipped": "GC mode: giữ mặc định (AR12c), lý do trong architecture.md".
3. `tools/docs-budget.ps1 -Check` xanh.

## Verification / Acceptance

- Gate chung. `git status` sạch sau `tools/fetch-native.ps1` (DLL bị ignore).
- CI xanh với `TreatWarningsAsErrors` (chạy PR).

## Không thuộc phạm vi

`WarningLevel`, bật thêm rule analyzer, self-contained publish (README nói framework-dependent là bản duy nhất hỗ trợ).
