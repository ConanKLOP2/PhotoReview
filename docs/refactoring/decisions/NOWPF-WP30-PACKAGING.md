---
id: NOWPF-WP30
order: 920
summary: |-
  WP-30 (đợt 4 NO-WPF, 2026-10-12): đóng gói hai exe trong MỘT thư mục/zip. `tools/publish-release.ps1` (mới, dùng chung cho ci.yml, release.yml) publish PhotoReview.App (WPF) rồi PhotoReview.Shell.Win32 vào cùng thư mục, R2R phụ thuộc framework: 51 tệp, 17,6 MB thư mục, zip 8,6 MB (chỉ WPF: 15,5 MB / 7,7 MB). Thư viện dùng chung giống từng byte giữa hai lần publish. `verify-release.ps1` đòi thêm PhotoReview.exe/dll/deps/runtimeconfig + Shell.Interop/Rendering/WpfBridge, mọi assembly trong hai deps.json, cùng commit cho hai dll vào, `-Smoke` chạy PhotoReview.exe --smoke (CI + release bật). Liên kết Explorer: `deploy/install-photo-review-association.ps1 -Variant Shell -ExePath ...` (ProgId riêng PhotoReview.Shell, khoá verb PhotoReview.Shell, opt-in: gõ YES hoặc -Force, -WhatIf, không đổi mặc định của loại tệp nào, không đụng bản WPF). Mặc định `Wpf` giữ nguyên hành vi. Lệch thẻ: WPF App không có `--smoke` (không đổi hành vi WPF) nên chỉ exe shell chạy smoke.
---

# NOWPF-WP30 - đóng gói hai exe, CI, liên kết Explorer (2026-10-12)

Thẻ: WP-30 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); NE-2 = a (hai exe song song) và NE-5/NE-6 trong
[NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md). Tiền đề: WP-20 ([NOWPF-WP20](NOWPF-WP20-STARTUP-SHELL.md)), cầu WPF
([NOWPF-WP19B](NOWPF-WP19B-WPF-BRIDGE.md)). App WPF mặc định **không đổi**.

## 1. Gói phát hành

| Mục | Giá trị |
|---|---|
| Thư mục | `src/PhotoReview.App/bin/Release/net10.0-windows/publish` (đường dẫn cũ giữ nguyên để artifact CI và tài liệu không đổi) |
| Exe | `PhotoReview.App.exe` (WPF, 535 552 B) + `PhotoReview.exe` (shell Win32, 163 328 B) |
| Kiểu | R2R (`PublishReadyToRun` đã có ở cả hai csproj), phụ thuộc framework, KHÔNG self-contained |
| Cỡ đo | 51 tệp, 17,55 MB thư mục; zip 8,58 MB. Chỉ WPF trước đó: 15,48 MB / 7,73 MB. Shell thêm ~2,1 MB (0,85 MB nén) |
| Runtime | cả hai `runtimeconfig.json` khai `Microsoft.WindowsDesktop.App` (cầu WPF nạp muộn, WP-19b); chưa tách được khỏi WindowsDesktop |

Hai lần `dotnet publish` vào cùng `-o`: mọi DLL dùng chung (Core, Imaging.*, App.Shared, Platform.Windows, App.WpfWindows, ...) được đo
**giống từng byte** (hash) nên lần thứ hai chỉ thêm tệp của shell và ghi đè bằng đúng nội dung cũ. Thứ tự cố định: WPF trước, shell sau.
`THIRD-PARTY-NOTICES.md` được thêm vào csproj của shell (cùng tệp) để thư mục shell dựng riêng cũng đủ thông báo pháp lý.

## 2. Script và workflow

- `tools/publish-release.ps1` (mới): một nguồn cho `ci.yml` (bước Publish Release) và `release.yml` (Publish), tham số `-OutputDirectory`,
  `-NoRestore`. `tools/verify-all.ps1` publish cùng hai project (tự xoá thư mục có kiểm sở hữu như trước).
- `tools/verify-release.ps1`: thêm 7 tệp bắt buộc; đọc cả `PhotoReview.App.deps.json` và `PhotoReview.deps.json`, mọi assembly mục `runtime`
  phải có mặt; hai dll vào (`PhotoReview.App.dll`, `PhotoReview.dll`) cùng version + commit; `-Smoke` chạy `PhotoReview.exe --smoke`
  (tiến trình riêng, hạn 60 s, kill đúng tiến trình đó). In thêm SHA-256 của exe shell.
- `ci.yml`: chỉ nội dung bước Publish/Verify (`-Smoke`). `release.yml`: bước Publish/Verify/kiểm version (cả hai dll) và ghi chú thêm SHA-256
  `PhotoReview.exe`; trigger, quyền, secrets không đổi. Ghi chú phát hành nói rõ exe shell là bản song song, không đổi liên kết tệp.
- Test: `TestFilterDriftTests` (hai workflow cùng dùng publish-release + `-Smoke`, không còn `dotnet publish` rời; verify-release liệt kê
  đủ tệp shell), `ToolScriptTests` (fixture thêm tệp shell, thiếu từng tệp shell, exe rỗng, deps.json nêu assembly vắng, `-Smoke` trên
  exe giả).

## 3. Liên kết Explorer cho PhotoReview.exe (opt-in)

`deploy/install-photo-review-association.ps1 -Variant Shell -ExePath <PhotoReview.exe> [-Force] [-NoFolderMenu] [-WhatIf]`:

| Thành phần | Bản WPF (mặc định, không đổi) | Bản shell |
|---|---|---|
| ProgId | `PhotoReview.App` | `PhotoReview.Shell` ("Photo Review (Win32)") |
| Lệnh mở | `"exe" "%1"` | `"exe" "%1"` |
| Open with | `.jpg .jpeg .png .bmp .gif .tif .tiff` / `OpenWithProgids` | như vậy, giá trị `PhotoReview.Shell` |
| Menu thư mục | `Directory\shell\PhotoReview` (+ `Background`) "Browse with PhotoReview" | `Directory\shell\PhotoReview.Shell` (+ `Background`) "Browse with PhotoReview (Win32)" |

Bất biến (test): không ghi `(Default)` hay UserChoice của bất kỳ đuôi nào; không đụng ProgId/verb của bản WPF; khoá `PhotoReview.Shell`
tách khỏi khoá `PhotoReview` nên hai bản đăng ký song song. Opt-in: bản shell hỏi gõ `YES` (host không tương tác phải `-Force`), `-WhatIf`
không ghi gì; không installer/CI/first-run nào gọi script. `uninstall-photo-review-association.ps1 -Variant Shell` chỉ xoá khoá/giá trị
của shell. Cả hai script có `-ClassesPath` (mặc định `HKCU:\Software\Classes`) để test dùng khoá HKCU riêng, KHÔNG ghi registry thật.

Settings > Tích hợp Explorer chạy trong cửa sổ cầu WPF của shell dùng `Environment.ProcessPath` = `PhotoReview.exe`, nên nút "Cập nhật đường
dẫn" tự trỏ về exe đang chạy; khoá verb của nút đó vẫn là `PhotoReview` (một verb chung của app, chủ động bấm mới đổi).

## 4. Lệch thẻ / chưa làm

1. **Smoke chỉ cho exe shell**: `PhotoReview.App.exe` không có `--smoke` và thêm vào là đổi hành vi WPF; exe WPF được kiểm bằng tệp, deps,
   version, commit. Tiếp tục dựa vào `App.Tests` cho khởi động WPF.
2. **Exe shell chưa có icon** (`ApplicationIcon`): mục menu `"exe",0` hiện biểu tượng mặc định. Việc nhỏ cho WP-31/đợt hoàn thiện. CHƯA XÁC NHẬN
   chọn dùng chung `PhotoReview.ico` của WPF.
3. **Chạy `-Smoke` cần phiên desktop**: CI windows-latest chạy được (Shell.Integration.Tests đã dựng HWND ở đó); máy không có desktop thì
   không bật cờ này. Chưa chạy được trên artifact CI thật trước khi merge: kết quả CI của PR này là lần đầu.
4. **Kiểm thủ công**: `PhotoReview.exe` từ thư mục gói chạy `--smoke` thoát 0 (đã đo trên máy dev). Đăng ký registry thật chưa chạy (cố ý).
5. Self-contained (`-RequireSelfContained` của verify-all) giữ nguyên cơ chế, publish thêm shell; chưa chạy trong PR này.

## 5. Đột biến thủ công

15/15 bị bắt (chạy bộ test tương ứng sau mỗi đột biến, khôi phục tệp sau đó):

| # | Đột biến | Bị bắt bởi |
|---|---|---|
| 1-2 | bỏ `PhotoReview.exe` / `Shell.WpfBridge.dll` khỏi danh sách bắt buộc | `VerifyRelease_MissingShellFile_Fails` |
| 3 | bỏ kiểm exe shell rỗng | `VerifyRelease_EmptyShellExe_Fails` |
| 4 | bỏ nhánh assembly vắng trong deps.json | `VerifyRelease_DepsNamesMissingAssembly_Fails` |
| 5 | `-Smoke` bị bỏ qua | `VerifyRelease_SmokeWithFakeShellExe_Fails` |
| 6 | bản shell dùng ProgId của WPF | `Shell_Force_RegistersOwnProgIdAndVerbs` |
| 7 | bỏ cờ opt-in (`-Force`/YES) | `Shell_WithoutForce_NonInteractive_WritesNothing` |
| 8 | `New-Item -Force` lại xoá `OpenWithProgids` | `Shell_Force_LeavesWpfAndUserDefaultsAlone` |
| 9 | bỏ `ShouldProcess` (`-WhatIf` vẫn ghi) | `Shell_WhatIf_WritesNothing` |
| 10 | khoá verb shell = khoá WPF | `Shell_Force_RegistersOwnProgIdAndVerbs` / `..._Unchanged` |
| 11-12 | uninstall shell xoá nhầm khoá/ProgId của WPF | `Shell_Uninstall_RemovesOnlyShellRegistration` |
| 13 | `ci.yml` bỏ `-Smoke` | `CiAndRelease_PublishBothExecutablesTheSameWay` |
| 14 | `publish-release.ps1` bỏ project shell | cùng test |
| 15 | `release.yml` quay lại `dotnet publish` một project | cùng test |

**Lỗi cũ được sửa trên đường đi**: `install-photo-review-association.ps1` dùng `New-Item -Force` trên khoá `<ext>\OpenWithProgids` đã có,
mà `-Force` ĐẶT LẠI khoá (xoá mọi giá trị) nên cài bản WPF xoá luôn mục "Open with" của ứng dụng khác (và của bản shell). Nay chỉ tạo khoá
khi chưa có. Hành vi bản WPF khác duy nhất: không còn xoá mục của app khác.
