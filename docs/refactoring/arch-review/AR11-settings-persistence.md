# AR11 — Một đường đọc/ghi Settings; round-trip cho control trong Settings

**Finding:** F11, F12 · **Quyết định:** không cần · **Kích thước:** ~0.5 ngày, 1 PR `refactor/ar11-settings-persistence` (2 commit: 11a, 11b) · **GUI:** không · **Agent:** sonnet

## AR11a — xoá đường tĩnh `AppSettings.Load/Save/Saver/Validator/ConfigPath`

**Hiện trạng** (`src/PhotoReview.Core/Settings/AppSettings.cs:101-119, 134-137`):

- `Saver` (`:102`) không được gán ở bất kỳ đâu trong `src`/`tools`/`tests` → `AppSettings.Save(settings)` là **no-op im lặng**. `SettingsWindow.xaml.cs:555-558` gọi nó khi `_store is null` (nhánh test-only).
- `Load(path)` (`:105`) dùng `File.Exists`/`File.ReadAllText` — chỗ duy nhất trong Core bỏ qua `IFileSystem`; lặp lại phần nhỏ của `SettingsStore.Load` nhưng **không** có `TrySalvage`/backup/normalize (INV-11).
- `Load()` không tham số trả về `new AppSettings()` (nhánh `path is null`). `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.Explorer.cs:299` gọi `AppSettings.Load().Shortcuts.Next` với comment "đọc cùng nguồn MainWindow dùng" — thực tế luôn nhận default. Comment tương tự ở `MainWindowBehaviorTests.Actions.cs:232`.
- `Validator` (`:103`) được gán ở `src/PhotoReview.App/Services/WpfKeyNameValidator.cs:14` (static mutable, thứ tự khởi tạo ngầm); `ValidateShortcuts` (`:137`) fallback `SimpleKeyNameValidator`.
- `ConfigPath` (`:101`) chỉ còn `tools/PhotoReview.Benchmark.Cli/PerfSession.cs:165` dùng để in cảnh báo.
- Người dùng khác: `tests/PhotoReview.App.Tests/AppSettingsTests.cs:77,90` (`Load(fixturePath)` cho fixture v1/v2).

**Thay đổi**

1. Xoá `Saver`, `Save`, `Load`, `ConfigPath` khỏi `AppSettings`. Giữ `Clone`, `CurrentConfigVersion`, `ValidateShortcuts` (xem bước 4).
2. `SettingsWindow.xaml.cs:555-558`: `_store` thành bắt buộc (`SettingsStore store` không nullable trong ctor); bỏ nhánh `else AppSettings.Save(...)`. Kiểm tra mọi `new SettingsWindow(` trong tests truyền store (dùng `SettingsStore` với `IFileSystem` giả — đã có pattern ở `SettingsWindowRedesignTests`).
3. `AppSettingsTests.cs:77,90`: đọc fixture qua `SettingsStore.Load` với `FakeFileSystem` (fixture copy vào fake) hoặc method `SettingsStore.Parse(string json)` mới (internal, `InternalsVisibleTo` đã có cho Core.Tests). Ưu tiên `Parse`: tách phần deserialize + `Migrate` + `??=` ra khỏi `Load` để test v1/v2 không cần file thật.
4. `Validator` static → thuộc tính instance trên `SettingsStore` (`IKeyNameValidator KeyNames`), gán trong `App.ConfigureServices` khi đăng ký `SettingsStore` (`new SettingsStore(fs, paths, new WpfKeyNameValidator())`). `AppSettings.ValidateShortcuts(settings)` → `SettingsStore.ValidateShortcuts(settings)`. Xoá `WpfKeyNameValidator.cs:14` (gán static) và `SettingsWindow.xaml.cs:385,541` dùng validator từ store.
5. `PerfSession.cs:165`: dùng `AppPaths.FromEnvironment().ConfigFile` trực tiếp.
6. `MainWindowBehaviorTests.Explorer.cs:299` và `Actions.cs:232`: đọc phím `Next` từ `SettingsStore` mà `AppHost` của test đang dùng (`services.GetRequiredService<SettingsStore>().Current.Shortcuts.Next`), sửa comment.
7. Test kiến trúc mới `tests/PhotoReview.Architecture.Tests/CoreFileSystemBoundaryTests.cs`: quét `src/PhotoReview.Core/**/*.cs` (dùng `RepoScan`/`TestSourceScanner` sẵn có), cấm regex `\b(File|Directory|FileInfo|DirectoryInfo)\.` ngoài allowlist `IO/PhysicalFileSystem.cs`, `AppPaths.cs`, `Settings/SettingsStore.cs` (nếu còn dùng để tạo thư mục — kiểm tra khi làm; mục tiêu allowlist chỉ 2 file). Allowlist là file text như `test-quality-allowlist.txt`.

**Tests / mutation**

- `SettingsStoreTests`: `Parse` migrate v1 → enums (chuyển 2 test từ `AppSettingsTests`). Mutation: bỏ `Migrate` trong `Parse` → đỏ.
- `CoreFileSystemBoundaryTests`: mutation: thêm `File.Exists("x")` tạm vào một file Core → đỏ.
- Integration `PressNext`: mutation: đổi `Shortcuts.Next` trong store giả của test sang `"F9"` → test phải bấm F9 (hoặc fail rõ), không còn bấm default.

**Acceptance:** `grep -rn "AppSettings\.\(Load\|Save\|Saver\|Validator\|ConfigPath\)" src tools tests` = 0; Core không có `File.*` ngoài allowlist; test kiến trúc mới xanh.

## AR11b — round-trip cho các thuộc tính CÓ control

**Hiện trạng:** `SettingsWindowRedesignTests.cs:29-45` liệt kê 27 thuộc tính "UiControlledProperties" và **chỉ** kiểm tra các thuộc tính còn lại sống sót qua open + Save. Các thuộc tính có control được map tay ở `LoadFields` (`:293-337`) và `Save_Click` (`:472-571`); quên một dòng ở `Save_Click` = giá trị người dùng chọn không được lưu, không test nào bắt.

**Thay đổi**

1. Test mới `tests/PhotoReview.Integration.Tests/SettingsWindowRoundTripTests.cs` (STA, cùng harness với `SettingsWindowRedesignTests`): với mỗi thuộc tính trong `UiControlledProperties` (trừ `ConfigVersion`, `Actions`, `Shortcuts`, `UiLanguage` có cửa sổ/logic riêng), tạo `AppSettings` A với giá trị **khác default** (enum: giá trị kế tiếp; bool: đảo; int: default+1 trong khoảng hợp lệ), mở cửa sổ với A, gọi `Save_Click` không đổi gì → store nhận đúng A. Bảng giá trị "khác default" là một `Dictionary<string, object>` trong test; thuộc tính mới không có trong bảng → test fail với thông báo "thêm giá trị cho X" (tripwire, cùng tinh thần `TestFilterDriftTests`).
2. Không đổi mã sản phẩm trừ khi test lộ lỗi (nếu lộ, sửa trong cùng PR và ghi vào mô tả).

**Mutation:** comment một dòng gán trong `Save_Click` (ví dụ `Settings.KineticPanEnabled = …`) → test đỏ đúng thuộc tính đó.

## Verification

Gate chung; `tools/i18n-check.ps1` không cần (không đổi chuỗi).

## Không thuộc phạm vi

Chuyển `LoadFields`/`Save_Click` sang binding hai chiều (thay đổi lớn ở XAML, không có lỗi hiện hữu để biện minh).
