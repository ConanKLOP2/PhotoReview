# PhotoReview — Trạng thái hiện hành

Cập nhật 2026-09-15. Source baseline: `master` tại commit `c87d14a`. Tài liệu này phân biệt chức năng đã có trong code, bằng chứng kiểm thử, và các gate còn thiếu. Các plan lập từ 2026-09-13/14 vẫn hữu ích như thiết kế hoặc lịch sử, nhưng không thay thế source hiện hành.

## Runtime và phạm vi

- Ứng dụng WPF trên `.NET 10`, bản project `1.0.1`, build Windows x64. Source chính ở `PhotoReview.App`; test executable ở `PhotoReview.Tests`.
- Mở folder/file bằng command line, nút chọn folder hoặc kéo-thả. Scan top-level, lọc extension qua `ImageFileTypes`, hiển thị ảnh fallback trước khi chờ snapshot native Explorer. Native provider dùng STA worker và `IFolderView2`; nếu snapshot thiếu/không hợp lệ, giữ sort cấu hình.
- Preview/Original/Fast, RAM LRU, preload lân cận/toàn folder có memory-pressure guard, disk thumbnail cache có quota/clear. Các policy 16 GB chỉ là ngưỡng cấu hình; chưa có benchmark chứng minh hiệu quả trên folder ảnh thực.
- Compare cặp ảnh, hash/size tùy chọn, action profiles Move/Copy/Recycle, batch duplicate có dry-run, journal và Recovery UI. Đây là chức năng hiện có, nhưng crash boundary và workflow GUI vẫn cần nghiệm thu thực.
- Pending Move/Copy reconciliation hiện chỉ xác nhận destination theo kích thước. Pending Copy với nguồn vẫn tồn tại có thể bị đánh Failed dù copy đã hoàn tất; đây là finding correctness cần xử lý ở FA-04/05.
- Undo Move qua phím/context hiện không cùng tuyến với Undo Recycle Bin; không xem Ctrl+Z là bằng chứng đã khôi phục Delete trong mọi trạng thái.
- Session/config hiện lưu JSON; operation journal lưu JSONL. Không có SQLite, ViewModel/command registry độc lập, folder watcher hay installer được triển khai trong source baseline.

## Bằng chứng xác nhận

- Commit `c87d14a` đã được push lên `origin/master`; local/remote cùng revision tại thời điểm snapshot.
- Sau commit: `dotnet run --project PhotoReview.Tests -c Release` PASS; `dotnet publish PhotoReview.App/PhotoReview.App.csproj -c Release --self-contained false -o PhotoReview.App/bin/Release/net10.0-windows/publish` PASS; release-file verifier PASS, FileVersion `1.0.1.0`.
- Contract tests gồm nhiều assertion theo chuỗi source; PASS không đồng nghĩa GUI, latency hoặc crash recovery đã đạt.
- Native Name DESC đã có probe trên một folder thực trước đây. Date/Size/grouped view và Unicode/large-folder matrix chưa được xác nhận đầy đủ.
- `tools/verify-all.ps1` mặc định đọc artifact ở `outputs/release/PhotoReview-framework-dependent`, khác thư mục publish bắt buộc trong `AGENTS.md`. Verify đúng artifact hiện hành bằng `tools/verify-release.ps1 -ReleaseDirectory PhotoReview.App/bin/Release/net10.0-windows/publish`; provenance từ HEAD/build stamp là hạng mục FA-14.

## Implementation đợt 1

- Hash/session/cache/sort/compare/drag-drop và một số guard MainWindow đã được triển khai sau audit. Test mới bao phủ compare pair khác folder và numeric filename vượt 12 chữ số; toàn bộ contract runner PASS.
- Chưa đánh dấu các gate tương ứng DONE vì vẫn thiếu test GUI, cancellation stress, crash boundary, real Explorer matrix và benchmark P95.

## Gate còn mở

1. Fixture manifest và benchmark lạnh/ấm: key-to-visible-frame, P50/P95, bytes/reads nguồn, decoded RAM/GC và contention của preload.
2. Test hành vi trên fixture file thật cho Move/Copy/Recycle/Undo, fault injection ở từng ranh giới journal/filesystem, cross-volume và destination conflicts.
3. Windows GUI acceptance: keyboard/focus, drag-drop, Compare, zoom/DPI, Recycle Bin recovery và Explorer Name/Date/Size/group.
4. Sau các gate trên mới quyết định version bump hoặc tuyên bố release hoàn chỉnh.

## Nguồn trạng thái

- [FUNCTION-AUDIT-PLAN.md](FUNCTION-AUDIT-PLAN.md): finding và task tối ưu theo function, thứ tự thực hiện, acceptance.
- [TASKS.md](TASKS.md): backlog sản phẩm PR-xxx, giữ trạng thái lịch sử; đối chiếu với gate hiện hành trước khi đổi DONE.
- [AUDIT-REPORT.md](AUDIT-REPORT.md): evidence và findings theo đợt audit.
- [PROGRESS.md](PROGRESS.md): nhật ký lịch sử; snapshot ngày cũ không phải trạng thái build hiện tại.
