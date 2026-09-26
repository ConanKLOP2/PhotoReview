# AR14 — Lắp ráp controller của `MainViewModel`

**Finding:** F17 · **Quyết định:** Q-AR10 · **Kích thước:** (b) ~0.5 ngày, 1 PR `refactor/ar14-vm-composition` · **GUI:** không · **Agent:** sonnet

## Hiện trạng

`src/PhotoReview.App/ViewModels/MainViewModel.cs:61-125`: ctor nhận 22 tham số qua DI (`MainViewModelCompositionRoot.Create`), rồi **tự** dựng:

- `:106` `new FileActionController(_catalog, _clock, _fileActionService, _undoService, _dialogService, _preloadController, _naturalComparer, () => Settings, this, …)`
- `:110` `new SiblingFolderNavigator(_clock, _catalog, _fileSystem, this, () => _currentSession)`
- `:117` `new DuplicateCleanupController(…, this)`
- `:112` `new InfoOverlayViewModel(() => Settings, _siblingNavigator.FindSiblingImageFolders)`

Ba controller nhận `this` làm sink (`IFileActionSink`, `ISiblingNavigatorSink`, `IDuplicateCleanupSink`) và delegate đọc trạng thái VM (`() => Settings`, `() => _currentSession`). Đây là **vòng phụ thuộc có chủ ý**: controller → sink = VM. Nhận định của lane "inject thẳng qua constructor, rủi ro thấp" là **sai**: DI không thể tạo controller trước VM vì controller cần VM.

Lợi ích thực của việc đổi: VM ngắn hơn ~15 dòng; test VM có thể thay controller bằng fake (hiện các test `MainViewModel*Tests` dùng controller thật với fake service — vẫn chạy nhanh, không có test nào bị chặn).

## Q-AR10 (phần AR14) — phương án

| | (a) Giữ nguyên, đóng AR14 | (b) Factory delegate | (c) Tách sink khỏi VM |
|---|---|---|---|
| Cách | Ghi rõ trong comment ctor rằng vòng sink là chủ ý | Ctor nhận `Func<IFileActionSink, FileActionController>` v.v. (3 delegate), `MainViewModelCompositionRoot` cung cấp; VM gọi `factory(this)` | Tạo `MainViewModelSinks` (class riêng giữ trạng thái catalog/session/status) mà cả VM và controller dùng; DI dựng sinks → controllers → VM |
| Công | 0 | ~60 dòng, 3 file | ~300 dòng, di chuyển 12 method sink (`:662-795`) và trạng thái `_currentSession`, `_statusText`… |
| Đổi hành vi | 0 | 0 | 0 nếu đúng, nhưng dễ lệch thứ tự `OnPropertyChanged` |
| Test thêm | 0 | `CompositionRootTests`: 3 factory tạo đúng controller; VM test có thể fake | Nhiều test VM phải sửa |
| Ưu | Không rủi ro | VM thuần điều phối; fake được controller | Sạch nhất về lý thuyết |
| Nhược | VM vẫn 860 dòng | Thêm 3 delegate chỉ để đảo hướng tạo; không giảm độ phức tạp thật | Chi phí lớn cho lợi ích chưa có nhu cầu |

**Khuyến nghị: (a)** — vòng sink là thiết kế coordinator hợp lệ, không có lỗi hay test bị chặn; (b) chỉ đáng làm khi một task khác cần fake controller trong test VM.

## Nếu chọn (b)

1. `MainViewModelCompositionRoot.Create`: dựng 3 delegate
   ```csharp
   Func<IFileActionSink, Func<AppSettings>, FileActionController> fileActions = (sink, settings) => new FileActionController(catalog, clock, fileActionService, undo, dialogs, preload, natural, settings, sink, folderPicker: picker, fileSystem: fs, rememberFolder: …);
   ```
   (`rememberFolder` hiện là method instance `RememberMoveCopyFolder` của VM → chuyển thành tham số delegate thứ ba hoặc để controller gọi qua sink — thêm `IFileActionSink.RememberMoveCopyFolder`).
2. VM ctor nhận 3 delegate (tham số optional = `null` → dựng như hiện nay, để test cũ không đổi), gọi `factory(this, () => Settings)`.
3. `InfoOverlayViewModel` giữ nguyên (không có vòng).
4. Test: `CompositionRootTests` khẳng định controller trong VM là instance do factory tạo (reflection helper `:96`); một test VM dùng fake `FileActionController` (cần `virtual`/interface `IFileActionController` — thêm interface 6 method mà VM gọi).

## Verification / Acceptance

Gate chung. (a): một comment + Q-AR10 ghi trên `develop`. (b): VM ctor không còn `new *Controller(`; test kiến trúc quét `MainViewModel.cs` cấm `new FileActionController(`.

## Không thuộc phạm vi

Giảm 22 tham số ctor bằng "parameter object" — cân nhắc riêng nếu ctor còn tăng.
