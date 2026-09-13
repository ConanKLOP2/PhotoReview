# Quyết định, giả định và rủi ro

## Quyết định đề xuất

| ID | Quyết định | Trạng thái / lý do |
|---|---|---|
| D01 | Windows C#/WPF | Đề xuất; hợp workflow native, xác minh bằng prototype |
| D02 | WPF/WIC đầu tiên | Đề xuất; adapter cho phép đổi decoder |
| D03 | Đánh dấu rồi áp dụng mặc định | Đề xuất; phản hồi nhanh, dễ đổi ý |
| D04 | Ba loại không hàm ý xóa | Chốt theo phạm vi hiện tại |
| D05 | Cache preview thay vì full toàn folder | Đề xuất; có prepare folder trong budget |
| D06 | Không overwrite | Yêu cầu an toàn bản đầu |
| D07 | Offline mặc định | Yêu cầu thiết kế |
| D08 | SQLite cho session/journal | Đề xuất; cần migration/recovery |
| D09 | JPG/PNG trước | Đề xuất; chốt theo bộ mẫu |
| D10 | Chưa hứa nhanh hơn Photos | Chốt; cần benchmark công bằng |

## Chưa biết — không tự coi là đã được người dùng chốt

| ID | Cần biết | Xử lý khi chưa có |
|---|---|---|
| Q01 | Folder mẫu chính xác | Không benchmark giả hoặc di chuyển ảnh |
| Q02 | CPU/RAM/ổ/màn hình | Thu thập có phạm vi khi triển khai |
| Q03 | Tên ba loại và đích | Dùng Loai-1/2/3 trong prototype |
| Q04 | Move ngay hay đánh dấu mặc định | Dùng đề xuất D03, cho cấu hình |
| Q05 | Có cần soi nét/màu chuyên nghiệp | Fit + 100% trước, ghi giới hạn màu |
| Q06 | OS tối thiểu và runtime | Chốt trước đóng gói |
| Q07 | Codec ngoài JPG/PNG | Inventory bộ mẫu trước thêm dependency |
| Q08 | Cache riêng tư trên đĩa | Có clear và RAM-only, giải thích rõ |
| Q09 | Nhu cầu quét folder con | Mặc định tắt |
| Q10 | Có cần portable tuyệt đối | Tách vị trí app/data; chốt khi đóng gói |

## Rủi ro

| ID | Rủi ro | Dấu hiệu | Biện pháp |
|---|---|---|---|
| R01 | Không nhanh bằng Photos | Benchmark G1 không đạt | Profile, thử backend, báo giới hạn |
| R02 | Phân loại sai ảnh | Decode/lệnh về sai thứ tự | ID + generation + test thao tác nhanh |
| R03 | Mất dữ liệu khi Move | Crash/nguồn thay đổi/xung đột | Journal, verify, recovery, no overwrite |
| R04 | RAM tăng không giới hạn | Duyệt lâu vượt budget | Byte accounting, release handles, soak test |
| R05 | Tạo cache làm xem chậm | I/O contention | Priority queue, pause background |
| R06 | Sai hướng/màu | Preview khác full | Fixture orientation/profile |
| R07 | Cache lộ ảnh riêng tư | Preview còn sau xóa nguồn | Clear/RAM-only, không telemetry |
| R08 | Scope tăng quá nhiều | AI/editor/cloud trước MVP | Backlog DEFERRED và gate |
| R09 | Codec hoặc runtime khác máy | Chỉ chạy máy dev | Clean-machine test |
| R10 | DB và filesystem lệch | Crash giữa commit | Reconciliation, không replay mù |

## Nhật ký thay đổi

2026-09-13: tạo bộ tài liệu; tất cả implementation task chưa bắt đầu. Quyết định đề xuất chưa được coi là lựa chọn người dùng đã xác nhận.

