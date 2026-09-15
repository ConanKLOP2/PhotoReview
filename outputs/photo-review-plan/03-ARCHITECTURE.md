# Kiến trúc kỹ thuật

Đối chiếu runtime 2026-09-15: đây là kiến trúc dự kiến. Code hiện dùng WPF code-behind ở `MainWindow`, `SessionStore` JSON, `OperationJournal` JSONL, RAM LRU và thumbnail cache; không có SQLite, ViewModel riêng, DecodeScheduler/FolderScanner/FileOperationService độc lập hay folder watcher. Các module/bất biến bên dưới là mục tiêu refactor theo [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md).

## Lựa chọn dự kiến

C# + WPF + .NET còn được hỗ trợ tại thời điểm triển khai. WPF/WIC là decoder đầu tiên để đo, chưa là kết luận hiệu năng. SQLite lưu phiên và journal; cấu hình đơn giản lưu JSON. Bản Windows self-contained x64 trước.

Nguồn kỹ thuật:
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.bitmapimage.decodepixelwidth
- https://learn.microsoft.com/en-us/windows/win32/wic/-wic-about-windows-imaging-codec

## Các module

| Module | Trách nhiệm |
|---|---|
| UI/ViewModels | Hiển thị trạng thái, command, focus, shortcut |
| FolderScanner | Liệt kê incremental, filter, natural sort, exclude đích |
| ReviewSession | ID ảnh, thứ tự, nhãn, bỏ qua, lịch sử duyệt |
| ImagePipeline | Decode thumbnail/preview/full, orientation, màu |
| DecodeScheduler | Ưu tiên, concurrency giới hạn, cancel/bỏ kết quả cũ |
| MemoryCache | Byte budget, pin ảnh hiện tại, eviction |
| DiskCache | Preview versioned, invalidation, quota |
| FileOperationService | Copy/Move/Undo, kiểm tra và journal |
| Persistence | SQLite migration, transaction, recovery |
| Diagnostics | Đo mốc thời gian, lỗi và benchmark export |

UI không gọi trực tiếp thao tác file hoặc giải mã nặng trên UI thread.
Decoder có interface riêng để thử backend khác.
Luồng ảnh không giữ file handle lâu sau decode nếu không cần, tránh cản Move.

## Mô hình dữ liệu dự kiến

- Session: id, sourceRoot, scanOptions, sortMode, currentImageId, mode, timestamps.
- ImageItem: id, sessionId, originalPath, currentPath, relativePath, size, modifiedUtc, width, height, format, reviewState, categoryId.
- Category: id, label, destinationRoot, key.
- ReviewAction: id, imageId, previousState, nextState, operationId, timestamp.
- FileOperation: id, type, source, destination, tempPath, sourceFingerprint, state, verification, error, timestamps.
- CacheEntry: imageFingerprint, variant, requestedSize, orientation/color policy version, path, byteSize, lastAccess.
- Settings: schemaVersion, RAM/disk budget, previewPolicy, privacy choices.

Đường dẫn không làm khóa chính vì Move đổi đường dẫn. Fingerprint nhanh gồm path/size/mtime không chứng minh nội dung giống hệt; hash chỉ dùng ở bước cần bảo đảm.

## Concurrency

Mỗi lần điều hướng tăng generation token. Chỉ publish decode khi imageId và generation còn khớp.
Giới hạn hàng đợi, deduplicate request cùng ảnh/variant.
Ưu tiên hiện tại → full khi zoom → ảnh tiếp → lân cận → thumbnail visible → prepare folder.
Khởi điểm đo concurrency 1/2/4; không chọn theo số CPU một cách máy móc.
Một codec không hủy được: không publish kết quả cũ; không tiếp tục xếp vô hạn.
File mutation tuần tự trong một phiên ở bản đầu; khóa mục tiêu để tránh hai lệnh đụng nhau.

## Bộ nhớ và cache

Budget tính theo byte bitmap thực tế cộng allowance được đo, không chỉ số ảnh.
Pin bitmap đang hiển thị; ảnh lân cận eviction khi pressure.
Không decode toàn bộ full-res theo mặc định.
Preview theo viewport pixel và DPI; thay kích thước cần debounce.
Disk key có schema/pipeline version; ghi temp rồi rename; cache lỗi xóa và rebuild.
Eviction không đụng file gốc. Xóa cache chỉ nhắm root cache đã resolve.
Chuẩn bị cả folder nghĩa chuẩn bị preview, RAM giữ trong budget; không hứa tất cả luôn resident.

## Persistence và filesystem

Transaction SQLite không bao trùm filesystem. Dùng journal trước/sau thao tác và đối chiếu khi khởi động.
Schema có version/migration và backup trước migration cần thiết.
Folder watcher chỉ báo thay đổi; reconcile snapshot, không tin mọi event luôn đến.
Ngăn hai instance cùng mutate một phiên.
Không theo junction/symlink đệ quy mặc định để tránh vòng lặp và thoát scope.

## Bảo mật và riêng tư

Không network cho workflow chính; log mặc định hạn chế đường dẫn đầy đủ.
Export diagnostics có xem trước nếu gồm tên file.
Cache có nội dung ảnh: quản lý quota, xóa cache/lịch sử, chế độ RAM-only.
Codec hỏng hoặc dữ liệu quá lớn: giới hạn và lỗi có thể phục hồi.

