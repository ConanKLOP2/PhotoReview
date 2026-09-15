# Kế hoạch triển khai IFolderView2 và version ứng dụng

Đối chiếu 2026-09-15: runtime có `ExplorerOrderService`, native COM vtable interop và snapshot validator; cấu trúc/interface đề xuất bên dưới không khớp một-một với file hiện tại. Native Name DESC có một probe trước đây, Date/Size/group matrix và FVW-011 vẫn mở. Version source giữ `1.0.1`; ví dụ bump `1.1.0` là kế hoạch, chưa áp dụng.

Cập nhật: 2026-09-14
Phạm vi: PhotoReview trên Windows 10/11, WPF .NET 10, Explorer desktop.

## 1. Mục tiêu

PhotoReview phải dùng đúng thứ tự đang hiển thị trong cửa sổ File Explorer tương ứng với folder ảnh:

- Name tăng/giảm theo Explorer;
- Date modified tăng/giảm;
- Size tăng/giảm;
- nhiều sort column nếu Explorer cung cấp;
- Group by và chiều group nếu có thể lấy được;
- thứ tự item sau khi Explorer refresh;
- không đảo danh sách bằng phỏng đoán.

Settings phải hiển thị version build để xác nhận chính xác executable đang chạy.

## 2. Giới hạn API cần chấp nhận

`IFolderView2` cung cấp item theo index và metadata của view, nhưng không phải mọi trạng thái giao diện Explorer đều có API công khai đầy đủ. Vì vậy:

1. Chỉ dùng order từ native view khi lấy được đầy đủ item regular-file và folder path khớp.
2. Nếu Explorer đang group item, phải xác định rõ item index có bao gồm group header hay không.
3. Nếu Shell không trả sort/group state hoặc thiếu item, trả `Unavailable`.
4. Fallback phải ổn định: natural Explorer name sort, không đảo ngẫu nhiên.
5. Không ghi đường dẫn hoặc dữ liệu ảnh ra network/telemetry.

## 3. Kiến trúc đề xuất

### 3.1 Interface nghiệp vụ

Tạo interface độc lập để test không cần COM:

```csharp
public interface IExplorerOrderProvider
{
    Task<ExplorerViewSnapshot?> TryGetSnapshotAsync(
        string folder,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

`ExplorerViewSnapshot` gồm:

- canonical folder path;
- ordered regular-file paths;
- sort columns và direction;
- group property/direction nếu đọc được;
- source window identity nếu cần log nội bộ;
- timestamp và provider status.

### 3.2 Phân lớp COM

Không đặt COM declaration trong `MainWindow`.

- `ExplorerOrderService.cs`: public facade, timeout, cancellation, fallback status.
- `ExplorerComInterop.cs`: GUID, COM interface, HRESULT, `IUnknown`/`IServiceProvider` declarations.
- `ShellFolderViewProvider.cs`: tìm Explorer window, lấy view object, QueryInterface `IFolderView2`.
- `ExplorerSnapshotValidator.cs`: canonicalize path, lọc regular file, kiểm tra đủ item và duplicate.
- `ExplorerOrderFake.cs`: test provider.

### 3.3 Lấy native view

Luồng triển khai:

1. Enumerate `Shell.Application.Windows` trên background thread.
2. So sánh `LocationURL` với folder canonical.
3. Lấy `Document`/folder view COM object.
4. Lấy `IServiceProvider` hoặc QueryInterface trực tiếp từ view object.
5. Query service/interface `IFolderView2` bằng IID chính thức.
6. Gọi `ItemCount`.
7. Với từng index, gọi `GetItem` hoặc `GetVisibleItem` để lấy `IShellItem`.
8. Lấy filesystem path bằng `SIGDN_FILESYSPATH`.
9. Đọc `GetSortColumnCount`/`GetSortColumns` và `GetGroupBy` nếu HRESULT thành công.
10. Release mọi COM object trong `finally` trên cùng background thread.

Không gọi COM từ UI thread.

## 4. Quy tắc snapshot và race condition

- Snapshot file ban đầu được tạo một lần.
- Native order chỉ được áp dụng nếu mọi path trong native result thuộc snapshot file.
- Item không phải regular file bị bỏ qua.
- Nếu native result thiếu một ảnh trong snapshot, không dùng một phần order; trả `Unavailable`.
- Nếu folder thay đổi giữa lúc scan và native query, kiểm tra lại `File.Exists` và canonical path.
- `MainWindow` dùng generation token để bỏ kết quả Shell cũ khi người dùng đổi folder.
- Timeout mặc định 2 giây, cấu hình test được.

## 5. Sort/group model

Tạo enum/property không gắn cứng vào UI:

```text
ExplorerSortProperty: Name, DateModified, Size, Type, Unknown
ExplorerSortDirection: Ascending, Descending, Unknown
ExplorerGroupState: None, Active, Unknown
```

Không chuyển native sort thành `ImageSortMode` nếu property là custom/unknown. UI status chỉ hiển thị:

- `Windows Explorer view` khi snapshot hợp lệ;
- `Windows Explorer view unavailable · Name fallback` khi không lấy được;
- không giả nhận là Name nếu thực tế chưa biết.

## 6. Xử lý group by

Ưu tiên:

1. Nếu `IFolderView2` trả item order đã flatten và validator xác nhận đủ ảnh, dùng trực tiếp thứ tự đó.
2. Nếu có group header hoặc item count không khớp, không dùng snapshot.
3. Ghi lý do fallback vào diagnostics/log nội bộ.
4. Không tự tái tạo group bằng metadata vì có thể khác cách Explorer xử lý locale/codec.

## 7. Versioning

### 7.1 Source of truth

Đặt version trong `.csproj`:

```xml
<Version>1.1.0</Version>
<AssemblyVersion>1.1.0.0</AssemblyVersion>
<FileVersion>1.1.0.0</FileVersion>
<InformationalVersion>1.1.0</InformationalVersion>
```

### 7.2 UI

Settings hiển thị:

- semantic version;
- file version;
- build commit nếu lấy được từ MSBuild property;
- runtime target và x64/x86 nếu cần hỗ trợ.

### 7.3 Release gate

- `dotnet build -c Release` phải pass 0 warning/0 error.
- FileVersionInfo của `.exe` phải khớp version source.
- Settings UI phải đọc cùng version từ assembly, không hard-code riêng một giá trị.
- Release folder phải publish lại sau mỗi version bump.
- SHA256 release ghi vào `PROGRESS.md`.

## 8. Test plan

### 8.1 Unit/fake provider

- provider unavailable → name fallback;
- timeout → name fallback;
- cancellation → không publish stale result;
- đủ item, đúng folder → native order accepted;
- thiếu item → rejected;
- duplicate path → rejected;
- virtual item → rejected;
- item ngoài folder → rejected;
- case-insensitive path comparison;
- 99 file và 100 file đều truy vấn native view (không được bỏ qua DESC ở folder nhỏ);
- folder path có Unicode;
- file bị đổi/xóa trong lúc query.

### 8.2 Native integration trên Windows

Chuẩn bị folder fixture có tên:

```text
1.jpg
2.jpg
10.jpg
A.jpg
B.jpg
ảnh-01.jpg
ảnh-02.jpg
```

Chạy từng trường hợp:

1. Name ascending.
2. Name descending.
3. Date modified ascending/descending.
4. Size ascending/descending.
5. Group by Type.
6. Group by Date.
7. Details, List, Large icons.
8. Explorer đóng.
9. Hai cửa sổ cùng folder.
10. Explorer refresh trong lúc app load.

Mỗi case ghi:

- Explorer visible order;
- PhotoReview order;
- provider status;
- elapsed milliseconds;
- fallback reason nếu có.

### 8.3 GUI acceptance

- mở bằng double-click ảnh;
- mở folder từ nút Mở folder;
- mũi tên trái/phải và lên/xuống;
- Enter/Delete không đổi thứ tự ngoài việc loại file khỏi snapshot;
- resize không làm reset index;
- Settings hiển thị version;
- F11/Esc không ảnh hưởng order;
- restart app resume đúng path.

## 9. Performance budget

- Native query không chạy UI thread.
- Timeout hard limit 2 giây.
- Không decode ảnh trong order provider.
- Không enumerate folder lần thứ hai ngoài snapshot cần thiết.
- COM query và release chạy trong cùng worker.
- Log không ghi full path khi logging tắt.
- Với folder lớn, hiển thị ảnh fallback đầu tiên trước, sau đó chỉ thay order nếu generation còn hợp lệ.

## 10. Task checklist

- [x] FVW-001: thêm `ExplorerViewSnapshot` và status enum.
- [x] FVW-002: tách provider interface khỏi `MainWindow`.
- [x] FVW-003: khai báo COM IID/interface chuẩn từ Windows SDK.
- [x] FVW-004: lấy `IFolderView2` từ đúng Explorer view (chờ matrix runtime ở FVW-011).
- [x] FVW-005: đọc item order bằng index.
- [x] FVW-006: lấy filesystem path và release `IShellItem`.
- [x] FVW-007: đọc sort columns/group metadata.
- [x] FVW-008: validator đầy đủ/duplicate/path boundary.
- [x] FVW-009: timeout/cancellation/generation integration.
- [x] FVW-010: fake-provider unit tests.
- [ ] FVW-011: native integration fixture và report (Name DESC đã PASS trên folder Unicode; Date/Size/Group còn chờ matrix).
- [x] FVW-012: cập nhật Diagnostics status/provider reason.
- [x] FVW-013: hiển thị version trong Settings.
- [ ] FVW-014: bump version lên 1.1.0 sau khi FVW-001..012 pass.
- [ ] FVW-015: publish/release hash và cập nhật progress.

## 11. Tiêu chí hoàn thành

Chỉ đánh dấu hoàn thành khi:

1. `IFolderView2` được gọi thật, không phải placeholder.
2. Native integration pass tối thiểu Name/Date/Size cả hai chiều.
3. Group/failure cases fallback an toàn.
4. Không có COM leak quan sát được.
5. Build/test/verify-all pass.
6. Settings hiển thị version của đúng executable đang chạy.
7. Release artifact được publish sau version bump.

## 12. Rủi ro và quyết định

- Explorer không expose đầy đủ mọi custom view state: giữ fallback, không đoán.
- COM vtable sai: bắt buộc dùng declaration đã đối chiếu Windows SDK/primary docs.
- Nhiều cửa sổ cùng folder: chọn cửa sổ active nếu xác định được; nếu không, dùng cửa sổ đầu tiên có snapshot hợp lệ và ghi lý do.
- Explorer đang loading: timeout và fallback, không block UI.
- Windows 11 có thay đổi implementation: native integration test là gate release.
