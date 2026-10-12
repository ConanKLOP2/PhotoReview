---
id: NOWPF-WP36
order: 990
summary: |-
  WP-36: bản Native AOT của PhotoReview.exe là artifact tùy chọn riêng (không cầu WPF); mặc định vẫn R2R hybrid.
---

# NOWPF-WP36 - publish Native AOT cho `PhotoReview.exe` + CI (2026-10-12)

Thẻ: WP-36 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); nền: [NOWPF-WP32](NOWPF-WP32-AOT-SPIKE.md) (spike), [NOWPF-WP33](NOWPF-WP33-COM-SHELL.md)
(ILC 0 cảnh báo). Số đo: [perf/2026-10-12-wp36-aot.md](../perf/2026-10-12-wp36-aot.md).

## 1. Đã làm

| Việc | Kết quả |
|---|---|
| `Shell.Win32.csproj` với `-p:ShellAot=true` | `PublishAot` + `EventSourceSupport` (đã có từ WP-32) + **bỏ `ProjectReference` WpfBridge** + `Compile Remove` `WpfBridgeLoader.cs` + hằng `SHELL_AOT`; `SecondaryWindowPorts.TryLoad` trả `null` (cửa sổ phụ no-op). Build thường không đổi (điều kiện `ShellAot != true`) |
| `tools/publish-release.ps1 -Aot` / `-RequireAot` / `-AotOnly` | Publish AOT vào thư mục RIÊNG `<out>-aot` (hoặc `-AotOutputDirectory`), xóa `*.pdb` (~50 MB), thêm `vswhere` vào PATH, thiếu MSVC thì bỏ qua có cảnh báo (`-RequireAot`: lỗi), khôi phục đồ thị restore thường sau khi xong |
| `tools/verify-release.ps1 -AotDirectory` / `-AotOnly` | exe + native + legal có mặt, KHÔNG có `PhotoReview.dll`/WpfBridge/PresentationFramework/coreclr, exe 5-40 MB, thư mục <= 40 MB (không `*.pdb`), SHA native khớp ghim, version có commit; `-Smoke` chạy `--smoke` <= 60 s mã 0 SAU KHI bỏ mọi `DOTNET_*` và mục `dotnet` trong PATH |
| `ci.yml` job `publish-aot` | `windows-latest`, `continue-on-error: true`, song song (không chặn `tag-version`), artifact `release-publish-aot`, thời gian publish ghi vào job summary |
| `release.yml` | KHÔNG đổi (bản phát hành vẫn hai exe R2R như WP-30); thêm bản AOT vào zip phụ là việc sau, khi người dùng chọn |
| Test | `AotReleaseScriptTests` (20 ca: thiếu tệp, tệp cấm, cỡ exe/thư mục, SHA, không có commit, smoke, thiếu MSVC skip/fail) + luật `Ci_PublishesAotAsSeparateNonBlockingArtifact` |

## 2. Số đo (chi tiết ở perf doc)

AOT sạch: exe 11,0 MB, thư mục 18,6 MB không `*.pdb` (zip 8,8 MB, 11 tệp), chạy được khi không có .NET. Ảnh đầu A/B xen kẽ 10 vòng trên máy bận 88-91 % CPU:
506 ms (AOT) so với 1330 ms (R2R), working set 118 vs 158 MB. Publish cục bộ ~97 s.

## 3. Quyết định (đề xuất, CHƯA XÁC NHẬN)

- **Bản mặc định giữ nguyên** (hybrid R2R, framework-dependent, có cầu WPF mở Settings/Diagnostics/Recovery) cho tới khi WP-35 thay cửa sổ phụ.
- **Bản AOT là artifact riêng, tùy chọn, thử nghiệm**: `release-publish-aot` trong CI, hoặc `tools/publish-release.ps1 -Aot` cục bộ. Giới hạn đã biết: **cửa sổ phụ
  (Settings, Diagnostics, Recovery) không mở** (cổng trả `null`); mọi thứ khác của shell chạy.
- Phương án thay thế: (b) thay luôn bản mặc định bằng AOT ngay (mất Settings cho tới WP-35: không chấp nhận được với người dùng cuối); (c) AOT + cầu WPF out-of-process
  (đã loại ở NE-5; lớn hơn WP-35). Hoàn tác: xóa job `publish-aot` + các tham số `-Aot*`; csproj/`SecondaryWindowPorts` chỉ đổi dưới `ShellAot`, không ảnh hưởng bản thường.
- Hành vi ngoài thẻ: `-AotOnly` (CI không cần publish R2R hai lần) và các móc chỉ cho test: `-VsWherePath` (publish), `-AotSkipVersionCheck` (verify).

## 4. Chưa làm / việc kế

- WP-35 (cửa sổ phụ không WPF) rồi đổi mặc định sang AOT, đưa AOT vào zip phát hành (sửa `release.yml` + luật WP-30 trong `TestFilterDriftTests`).
- Đo A/B lúc máy rảnh để chốt NW-5 (<= 350 ms); đo G0.6 (pan) trong tiến trình AOT; ghi thời gian job CI thật sau lần chạy đầu.
- Khi `-Aot` publish cục bộ rồi build thường ngay: script đã chạy `dotnet restore` lại, nhưng IDE đang mở có thể phải reload.

## 5. Kiểm đột biến (hai bước, docs/MUTATION-TESTING.md)

14 đột biến thủ công trên `verify-release.ps1` / `publish-release.ps1` (một dòng mỗi cái), chạy lớp `AotReleaseScriptTests` (bước 1), survivor chạy cả project Integration (bước 2):

| # | Đột biến | Kết quả |
|---|---|---|
| M01-M02 | tắt kiểm tệp thiếu / tệp cấm | giết (5 / 4 ca) |
| M03-M05 | tắt kiểm cỡ tối thiểu / tối đa của exe / cỡ thư mục | giết (mỗi cái 1 ca) |
| M06 | đảo so sánh SHA native | giết (4 ca) |
| M07 | tắt kiểm "version không có commit" | giết (1 ca) |
| M08 | bỏ qua mã thoát của smoke | giết (2 ca; cần exe thật chạy được: thêm `whoami.exe` làm exe thay thế + `-AotSkipVersionCheck`) |
| M09 | WpfBridge không còn trong danh sách cấm | giết |
| M10 | nhánh `-AotOnly` tắt | giết (17 ca) |
| M11-M12 | `-RequireAot` không còn lỗi; bỏ rào `-AotOnly` thiếu `-Aot` | giết |
| M13 | smoke không truyền `--smoke` | giết (ban đầu SỐNG; thêm test `whoami.exe` rồi giết) |
| M14 | không lọc `dotnet` khỏi PATH khi chạy smoke | **sống**, tương đương: chỉ lộ ra với exe cần runtime .NET; exe AOT không cần nên CI smoke thật không phân biệt; chạy cả project Integration (1276/1278 xanh; 2 ca đỏ là thời gian khởi động, không liên quan) |
