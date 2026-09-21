# DF — Kế hoạch: nhấp đúp chuột thì Fit

- **Ngày lập:** 2026-09-21
- **Baseline:** `master` @ `d602d4c`, cây làm việc sạch.
- **Phạm vi:** ảnh chính trong `MainWindow` (ScrollViewer `ImageScroll` + `MainImage`). Không đụng Compare, decode, cache, file action, Settings.
- **Trạng thái:** `PLANNED` — chưa có dòng code nào được sửa.
- **Ràng buộc từ T89:** không đổi cấu trúc `ApplyFitViewAsync` (tối đa 3 pass, version cancellation) khi chưa có GUI/STA evidence. Tính năng này **chỉ gọi lại** transaction đó, không sửa nó.

## 1. Hợp đồng hành vi

1. Nhấp đúp **nút trái** trên ảnh chính → thực hiện đúng thao tác của nút Fit / phím `F` (cùng `ApplyFitViewAsync`).
2. Kết quả: `Zoom = 1`, `Stretch = Uniform`, offset = 0, viewport hội tụ như T89 (sai lệch ≤ 0.5 DIP so với bấm nút Fit).
3. Idempotent: nhấp đúp khi đã Fit thì không đổi gì nhìn thấy được (không chuyển sang 100%; xem Q-DF1).
4. Không gây side effect khác: không pan, không đổi ảnh, không chạm file/decode/cache, không đọc đĩa.
5. Không kích hoạt khi: nhấp đơn; kéo (pan) vượt ngưỡng; Compare đang hiện; nhấp trên scrollbar; nhấp trên nút overlay; nút chuột phải/giữa.
6. Không dùng `Task.Delay`/timer tự đặt để nhận diện double-click; dùng ngưỡng hệ thống của WPF (`ClickCount`).
7. Phím `F`, nút Fit, InitialViewMode=Fit và double-click phải là các lối vào cùng một hợp đồng (T89.4).

## 2. Hiện trạng code (đã đọc)

| Điểm | Vị trí | Ghi chú |
|------|--------|---------|
| Handler chuột trên ảnh | `MainWindow.xaml:50-53`, `MainWindow.xaml.cs:218-266` | `PreviewMouseLeftButtonDown` gọi `CanPan()`; nếu pan được thì `CaptureMouse()` + `e.Handled = true`. Ở chế độ Fit `CanPan()` = false nên trả về sớm. |
| Fit transaction | `MainWindow.xaml.cs:347-368` | `CancelPan()` → tăng `_viewportOperationVersion` → `ResetFit` → ≤3 pass layout/Render → reset offset. |
| Lối vào Fit hiện có | nút (`FitImage_Click`), phím (`ReviewCommandType.ToggleFit`), hook `ResetFitView()` | Đều đi qua `ApplyFitViewAsync`. |
| Compare | `MainWindow.xaml:60-75` | Panel phủ lên, có handler `MouseLeftButtonDown` riêng (chọn trái/phải); tự chặn sự kiện tới ảnh chính. |

Điểm cần chú ý: `Image` là `FrameworkElement`, **không có** sự kiện `MouseDoubleClick` (chỉ `Control` có). Phải phát hiện bằng `MouseButtonEventArgs.ClickCount == 2` ở `PreviewMouseLeftButtonDown`, và kiểm tra **trước** nhánh `CanPan()`, nếu không nhấp đúp lúc đang Fit sẽ bị bỏ qua.

## 3. Quyết định thiết kế

- **D1:** Thêm nhánh `if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)` vào đầu `MainImage_PreviewMouseLeftButtonDown`: `CancelPan()`, `e.Handled = true`, `_ = ApplyFitViewAsync()`, return. Không thêm handler XAML mới.
- **D2:** Chỉ `== 2` (không `>= 2`): nhấp thứ 3 trở đi rơi về luồng pan bình thường. Hợp lý vì Fit idempotent.
- **D3:** Nếu `ClickCount` không set được từ test (setter có thể là `internal`; xác minh ở DF01), tách thành seam nhỏ `internal void HandleImagePrimaryDown(int clickCount)` để test STA/unit gọi trực tiếp. Không dùng reflection vào `MouseButtonEventArgs`.
- **D4:** `MouseLeftButtonUp` của lần nhấp thứ hai vẫn đi qua `MainImage_PreviewMouseLeftButtonUp`; vì `_panMoved = false` nên không tác dụng phụ.
- **D5:** Cập nhật `ToolTip` của nút Fit và tài liệu người dùng, không thêm cấu hình.

## 4. Câu hỏi cần chốt (mặc định đề xuất, không chặn DF00–DF03)

| ID | Câu hỏi | Đề xuất |
|----|---------|---------|
| Q-DF1 | Đang Fit mà nhấp đúp thì làm gì? | Không làm gì (đúng yêu cầu gốc). Toggle Fit ⇄ 100% kiểu Windows Photos để làm việc riêng nếu muốn. |
| Q-DF2 | Vùng nhận double-click? | Chỉ `MainImage` ở v1. Nếu DF01 cho thấy khi zoom < 100% vùng tối quanh ảnh không thuộc `MainImage`, quyết định có mở rộng sang `ImageScroll` (loại trừ scrollbar) hay không. |
| Q-DF3 | Có công tắc tắt gesture trong Settings? | Không. |

## 5. Task

Thứ tự: DF00 → DF01 → DF02 (test đỏ) → DF03 (code xanh) → DF04 → DF05 → DF06 → DF07. Chỉ một người/agent sửa `MainWindow.xaml.cs` tại một thời điểm (phối hợp T89.5/T89.6, OC14–OC18 đang cùng file).

### DF00 — Nhánh và baseline
- **Trạng thái:** `TODO` · **Độc lập:** có
- **Làm:** tạo nhánh `codex/dblclick-fit` từ `master` (AGENTS: không commit thẳng master); chạy `dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"`; ghi số PASS/FAIL baseline; kiểm tra T89.5/T89.6 chưa có PR mở chạm `MainWindow.xaml.cs`.
- **Xong khi:** baseline ghi vào mục 8 của file này; xác nhận không xung đột file scope.

### DF01 — Probe cơ chế chuột WPF (chỉ đọc)
- **Trạng thái:** `TODO` · **Độc lập:** có · **Files:** test/probe tạm, không commit vào production
- **Cần trả lời bằng bằng chứng chạy thật, không suy đoán:**
  1. Sau khi nhấp đơn đã `CaptureMouse()` + `Handled = true`, nhấp thứ hai có `ClickCount == 2` ở `PreviewMouseLeftButtonDown` không?
  2. `MouseButtonEventArgs.ClickCount` có set được từ test không (quyết định D3)?
  3. Khi zoom < 100%, vùng tối quanh ảnh có nằm trong bounds của `MainImage` không (quyết định Q-DF2)?
  4. Nhấp trên scrollbar có sinh `PreviewMouseLeftButtonDown` với `OriginalSource` là `MainImage` không (kỳ vọng: không)?
- **Đầu ra:** 4 dòng kết luận PASS/FAIL trong mục 8.

### DF02 — Test trước (phải đỏ trên baseline)
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF01 · **Files:** `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.*.cs` (mẫu: `PressKey`/`StaTestHost`), có thể thêm `MainWindowBehaviorTests.Fit.cs`
- **Cases bắt buộc** (dùng `StaTestHost.WaitForAsync`, không `Task.Delay`):
  1. Zoom 200% (có scrollbar) → double-click → `Viewer.IsFit`, offset ≤ 0.5, kích thước ảnh bằng kết quả bấm nút Fit.
  2. Đang Fit → double-click → state và kích thước không đổi.
  3. Nhấp đơn ở zoom 200% → không Fit (vẫn `Zoom = 2`).
  4. Kéo vượt ngưỡng rồi nhả → không Fit, offset đã pan.
  5. `ClickCount == 3` → không gọi Fit lần nữa (không tăng version thêm ngoài lần 2).
  6. Nút phải / giữa `ClickCount == 2` → không Fit.
  7. `Compare.IsVisible = true` → double-click không Fit.
  8. Double-click hai lần liên tiếp nhanh → kết quả cuối giống một lần (version cancellation đúng).
  9. Sau double-click không có thêm lần đọc nguồn/decode (đếm qua hook hiện có; nếu chưa có seam thì ghi rõ giới hạn, phụ thuộc TC02).
- **Xong khi:** case 1 thất bại đúng lý do (không Fit) trên `d602d4c`.

### DF03 — Hiện thực
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF02 · **Files:** `src/PhotoReview.App/MainWindow.xaml.cs` (chỉ `MainImage_PreviewMouseLeftButtonDown` + seam nếu cần)
- **Làm:** theo D1–D4; không sửa `ApplyFitViewAsync`; không thêm comment ngoài một dòng giải thích vì sao nhánh này phải đứng trước `CanPan()`.
- **Xong khi:** toàn bộ DF02 xanh; diff dưới ~20 dòng.

### DF04 — Hồi quy pan / wheel / Fit hiện có
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF03
- **Làm:** chạy lại `MainWindowHelpers` tests (anchor, pan 4 hướng/clamp), `ViewerStateTests`, `ShortcutRouterTests`; thêm case: nhấp đơn + kéo pan vẫn hoạt động sau khi từng double-click; wheel-zoom ngay sau double-click vẫn neo đúng con trỏ.
- **Xong khi:** không test cũ nào đổi kết quả.

### DF05 — Tooltip và tài liệu
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF03
- **Làm:** cập nhật `ToolTip`/`AutomationProperties.Name` của nút Fit ở `MainWindow.xaml:81` để nhắc double-click (giữ tiếng Việt có dấu, chạy mojibake guard TS04); thêm 1 dòng vào `README.md`/`APP-MECHANISMS-VI.md` nếu mô tả thao tác chuột; cập nhật `task_on_progress.md` và `ACTIVE-TASKS`.
- **Xong khi:** guard mojibake PASS, không còn tài liệu mô tả sai thao tác.

### DF06 — GUI acceptance (thủ công, do người dùng hoặc môi trường có native window)
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF03 · gộp chung phiên với T89.8
- **Checklist:** ảnh dọc/ngang; zoom 200% và 400% rồi double-click; zoom 25% rồi double-click vào vùng tối và vào ảnh; đang Fit double-click; double-click ngay sau wheel; double-click trên scrollbar và trên nút overlay; fullscreen; Compare mở; double-click rồi bấm nút Fit không thấy ảnh nhảy.
- **Bằng chứng:** PASS/FAIL từng dòng, screenshot nếu sai.

### DF07 — Validation và phát hành nhánh
- **Trạng thái:** `TODO` · **Phụ thuộc:** DF04–DF06
- **Làm:** `./tools/verify-all.ps1` (test + smoke + fault-injection + publish + verify-release); commit nhỏ theo task; push nhánh `codex/dblclick-fit`; mở PR vào `master`.
- **Xong khi:** gate xanh, DF06 PASS, không commit thẳng master.

## 6. Tiêu chí hoàn thành

- Double-click ở mọi trạng thái zoom đưa về đúng trạng thái Fit của nút; tối đa 3 pass layout, tự kết thúc.
- Không regression pan/wheel/phím; không subscription hay timer mới.
- Nhấp đơn, kéo, chuột phải/giữa, scrollbar, overlay, Compare đều không kích hoạt Fit.
- Không thêm đọc đĩa hay decode.

## 7. Rủi ro

| Rủi ro | Giảm thiểu |
|--------|-----------|
| Nhánh Fit đặt sau `CanPan()` nên đang Fit thì bị nuốt | D1 yêu cầu đặt trước; case 2 trong DF02 |
| Nhấp đơn đã capture chuột làm mất `ClickCount` | DF01 mục 1; nếu mất, đổi sang `PreviewMouseLeftButtonUp` + đếm theo `SystemParameters.DoubleClick*` |
| Xung đột với T89.5/T89.6 trong `MainWindow.xaml.cs` | Một chủ sở hữu file; rebase sau khi T89 merge |
| Fit chạy khi ảnh chưa nạp | `ApplyFitViewAsync` đã kiểm tra `IsLoaded`; thêm case ảnh rỗng vào DF02 nếu DF01 cho thấy lỗi |
| Test dựa vào timing | Chỉ dùng `WaitForAsync`/pump dispatcher |

## 8. Ghi chép thực hiện

_(điền baseline DF00 và kết luận DF01 tại đây)_

## 9. Không làm

- Không toggle 100%, không double-click cho Compare, không đổi `ApplyFitViewAsync`, không thêm Settings, không hỗ trợ cử chỉ cảm ứng riêng.
