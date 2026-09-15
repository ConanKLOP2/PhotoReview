# An toàn thao tác file và phục hồi

Đối chiếu runtime 2026-09-15: Move/Copy action, Recycle Bin, JSONL journal và Recovery UI đã có; staged cross-volume copy, hash-based verification và crash-boundary state machine dưới đây là yêu cầu chưa triển khai đầy đủ. Reconcile hiện kiểm tra destination Move/Copy theo size, pending Copy còn source có thể bị đánh Failed; không tuyên bố G3 PASS trước fixture/fault matrix.

## Bất biến

1. Không overwrite file đích.
2. Không bỏ nguồn cross-volume trước khi bản đích được xác minh.
3. Không ghi thành công trước khi thao tác hoàn tất.
4. Không Undo xóa file đã bị thay đổi ngoài app.
5. Không thao tác theo tên vừa suy ra mà thiếu kiểm tra path/scope.
6. Không sửa bytes/metadata ảnh vì phân loại.
7. Không retry mù sau crash.
8. Preview/cache và file gốc có root và trách nhiệm tách biệt.

## Chuẩn bị thao tác

Resolve absolute source/destination, kiểm tra đích hợp lệ và không tự trỏ nguồn.
Kiểm tra file thật, source fingerprint, quyền và xung đột.
Không theo reparse point ngoài phạm vi mặc định.
Đóng handle decode không cần thiết.
Ghi journal PREPARED với đường dẫn chính xác trước mutation.
Kiểm tra trùng tên lần nữa ở bước commit; dùng thao tác không overwrite để xử lý race.

## Same-volume Move

Ghi PREPARED → thực hiện move không overwrite → đối chiếu kết quả → COMMITTED.
Nếu crash giữa move và journal: reconcile nguồn/đích và fingerprint.
Không giả định mọi filesystem hay mọi trường hợp move đều atomic.
Undo move chỉ khi đích vẫn là dữ liệu dự kiến và nguồn cũ chưa bị chiếm.

## Cross-volume Move

PREPARED → copy vào temp độc nhất tại đích → flush/đóng → verify size và hash nguồn/đích → rename temp không overwrite → DESTINATION_VERIFIED → kiểm tra nguồn chưa đổi → bỏ nguồn → COMMITTED.
Nếu không đảm bảo nguồn không thay đổi trong copy/xóa, dừng với trạng thái cần kiểm tra, không bỏ nguồn.
Nếu không bỏ được nguồn: giữ cả hai, trạng thái COPIED_SOURCE_REMAINS.
Chi phí hash được đo; ưu tiên chạy ngoài luồng UI.

## Copy

Cùng quy trình temp/verify/commit nhưng giữ nguồn. Undo chỉ xóa đúng bản copy do thao tác tạo ra nếu nội dung không đổi.
Không tuyên bố Undo Copy luôn khôi phục được nếu người dùng sửa đích.

## Xung đột

Trùng đích: skip hoặc tên mới do người dùng lựa chọn; lưu chính xác tên cuối.
Bản đầu không có overwrite.
Không tự coi cùng tên là cùng nội dung.
File bị khóa, mất quyền, hết chỗ, rút ổ: lỗi từng mục, không báo thành công.
Retry phải kiểm tra lại nguồn và đích hiện thời.

## Recovery khi khởi động

| Trạng thái thực tế | Cách xử lý |
|---|---|
| Nguồn có, đích chưa có, temp có | Xác minh temp; tiếp tục hoặc dọn temp đã xác định |
| Nguồn có, đích có | Verify; không tự xóa nguồn khi không rõ |
| Nguồn mất, đích đúng | Hoàn tất journal nếu bằng chứng đủ |
| Cả hai mất | Báo cần can thiệp; không giả thành công |
| Đích khác fingerprint | Conflict; không Undo tự động |
| File cũ đã có người tạo lại | Undo conflict, không ghi đè |

Đối chiếu trước khi resume; task queue không tự replay lệnh destructive không rõ trạng thái.
Cleanup temp chỉ với file đã ghi journal, nằm trong root đích đã xác minh.

## Gate G3

Vượt test trùng đích, source change, khóa file, thiếu chỗ, crash tại từng ranh giới journal, Undo sau sửa ngoài, cross-volume và restart.
Đối chiếu hash bộ fixture trước/sau. Chỉ sử dụng bản sao test đến khi gate đạt.

