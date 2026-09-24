# ADR 0006: Đa ngôn ngữ bằng file JSON bên ngoài, cộng đồng tự sửa được

- **Trạng thái:** Chấp nhận (Accepted), 2026-09-24 (Q-L1..Q-L8). Kế hoạch thực hiện: `docs/refactoring/I18N-PLAN.md` (L00–L12).
- **Ngày:** 2026-09-24

## Bối cảnh

Toàn bộ chữ trên UI đang viết cứng trong XAML và C#, trộn tiếng Việt với tiếng Anh; không có hạ tầng đa ngôn ngữ.
Yêu cầu: trước mắt có English và Tiếng Việt, và **người dùng hoặc cộng đồng tự sửa, tự đóng góp bản dịch**
mà không cần build lại hay biết Visual Studio.

## Quyết định

1. **Catalog là file JSON phẳng** (`key → text`) có khối `_meta` (`code`, `name`, `nativeName`, `authors`,
   `formatVersion`, `plural`). Placeholder có tên (`{count}`), số nhiều bằng hậu tố `.one`/`.other`
   (`plural: "none"` cho vi/ja/zh luôn dùng `.other`). Định dạng tương thích Weblate/Crowdin.
2. **English là ngôn ngữ nguồn**, nhúng vào assembly Core và luôn đủ key. Tìm key theo thứ tự:
   file người dùng (`%LocalAppData%\PhotoReview\Languages`) → file đi kèm (`<app>\Languages`) → English nhúng → chính tên key.
   Danh sách ngôn ngữ = quét các file `*.json`; thêm ngôn ngữ không cần sửa code.
3. **File dịch là dữ liệu không tin cậy.** Giới hạn kích thước; key có placeholder lạ hoặc ngoặc hỏng bị loại và ghi log;
   file hỏng bị bỏ qua. `SafeFormatter` không bao giờ ném exception. Không có biểu thức, không có code.
4. **API an toàn lúc build:** một source generator đọc `en.json` sinh `Tr.<Key>(args)` và `TrKeys.<Key>`;
   sai tên key hoặc sai số tham số là lỗi build.
5. **Localizer ambient** (`Localizer.Current`, thay nguyên tử khi đổi ngôn ngữ) — giống `CultureInfo.CurrentUICulture`.
   Chọn cách này thay vì inject `ILocalizer` vào mọi service vì `StatusFormatter` và các thông báo Core là static/đơn giản,
   và tránh đổi hàng chục constructor. Test đặt `Localizer.Current` qua `ModuleInitializer`.
6. **XAML dùng markup extension `{loc:Tr key}`** bind vào indexer của `LocalizationSource` → đổi ngôn ngữ và
   "Tải lại bản dịch" có hiệu lực ngay, không restart. Chữ do ViewModel định dạng cập nhật ở lần cập nhật kế tiếp.
7. Chỉ đổi `CurrentUICulture`; `CurrentCulture` (định dạng số/ngày) giữ theo Windows. Log, CSV, journal, báo cáo perf
   **không** dịch (AGENTS.md quy tắc 4). Journal lưu mã lỗi ổn định + text English; UI dịch theo mã.

## Hệ quả

- Người đóng góp chỉ sửa một file JSON; `tools/i18n-check.ps1` và CI kiểm tra parity/placeholder/% hoàn thành.
- Đổi nghĩa câu English phải đổi tên key để bản dịch cũ không âm thầm sai.
- Chi phí runtime: nạp catalog lúc khởi động (mục tiêu < 5 ms), mỗi lần chuyển ảnh thêm một lần tra dictionary.
- Thêm một project generator (`netstandard2.0`) vào solution.

## Phương án đã cân nhắc

- **`.resx` + satellite assembly:** chuẩn .NET, nhưng sửa một chữ phải build lại, người dịch cần công cụ Visual Studio — trái yêu cầu cộng đồng.
- **gettext `.po`:** công cụ dịch tốt nhưng cần thư viện ngoài cho WPF, placeholder/plural phức tạp hơn nhu cầu.
- **Inject `ILocalizer` vào mọi service:** thuần DI hơn nhưng đổi nhiều constructor và test mà không thêm lợi ích thực tế.
- **Bắt buộc restart khi đổi ngôn ngữ:** đơn giản hơn nhưng người dịch không thấy kết quả ngay (Q-L8 chọn đổi trực tiếp).
