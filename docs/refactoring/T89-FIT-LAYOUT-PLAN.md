# T89 — Kế hoạch sửa Fit cần bấm hai lần

- **Ngày lập:** 2026-09-20
- **Branch:** `codex/t89-pointer-anchored-zoom`
- **Phạm vi:** chỉ cơ chế zoom/Fit của ảnh chính; không thay đổi compare, decode, cache, file action hoặc shortcut ngoài zoom/Fit.
- **Trạng thái tổng:** `IN PROGRESS` — transaction Fit và UI viewport refresh đã triển khai; còn GUI acceptance và test WPF layout chuyên biệt.
- **Baseline:** commit `a923106`; wheel thường đã zoom, anchor math đã có, `LayoutTransform` đã thay cho `RenderTransform`; `verify-all.ps1` gần nhất PASS 741/741.

## 1. Hiện tượng và hợp đồng mong muốn

### 1.1 Hiện tượng đã xác nhận thủ công

Sau khi zoom, bấm **Fit** lần đầu làm ảnh gần vừa khung nhưng chưa chuẩn. Bấm **Fit** lần thứ hai thì ảnh vừa khung đúng.

### 1.2 Hợp đồng đích

Một lần bấm Fit phải là thao tác idempotent và hoàn chỉnh:

1. Hủy mọi thao tác bù offset của wheel cũ đang chờ dispatcher.
2. Đặt viewer về `Zoom = 1`, `Stretch = Uniform`.
3. Dùng kích thước vùng hiển thị cuối cùng sau khi scrollbar biến mất.
4. Ảnh nằm đúng trong viewport, không bị cắt và không còn scroll offset cũ.
5. Một lần Fit và hai lần Fit liên tiếp phải cho cùng kích thước/position cuối.
6. Nút Fit, phím Fit và InitialViewMode=Fit phải dùng cùng một hợp đồng.
7. Không dùng `Task.Delay`, sleep hoặc số mili-giây phỏng đoán để chờ layout.

## 2. Phân tích nguyên nhân theo code hiện tại

### 2.1 Nguyên nhân chính đã có bằng chứng source

`ApplyFitViewAsync()` hiện gọi:

```text
ResetFit(ImageScroll.ViewportWidth, ImageScroll.ViewportHeight)
```

`ViewportWidth/ViewportHeight` của `ScrollViewer` phụ thuộc vào scrollbar đang hiện. Khi ảnh đang zoom lớn, scrollbar ngang/dọc thường đang chiếm chỗ. Vì vậy lần Fit đầu nhận một viewport nhỏ hơn vùng hiển thị cuối cùng.

Sau khi `Zoom`, `Stretch`, `MaxImageWidth` và `MaxImageHeight` thay đổi, WPF chạy lại measure/arrange. Ảnh co lại, scrollbar biến mất, làm viewport bên trong rộng/cao hơn. Tuy nhiên `ScrollViewer.ActualWidth/ActualHeight` không đổi; handler `ImageScroll_SizeChanged` vì thế không được bảo đảm chạy. `MaxImageWidth/MaxImageHeight` có thể còn giữ kích thước lấy khi scrollbar vẫn hiện.

Lần bấm Fit thứ hai diễn ra khi scrollbar đã biến mất, nên `ViewportWidth/ViewportHeight` lúc này là kích thước cuối và ảnh Fit chuẩn. Đây là lời giải thích phù hợp trực tiếp với triệu chứng “lần một gần đúng, lần hai chuẩn”.

### 2.2 Lỗi production initialization độc lập nhưng cùng tác động

Trong đường DI production tại `App.xaml.cs`, callback `onApplyInitialViewMode` đang gọi:

```text
Viewer.ApplyInitialViewMode(..., 0, 0)
```

Trong khi đường test/helper có callback lấy viewport thật từ `MainWindow`. Với `(0,0)`, `ViewerState.UpdateViewport()` không cập nhật `MaxImageWidth/MaxImageHeight`. Nếu ảnh trước đang zoom, hai giá trị này có thể vẫn là `PositiveInfinity`; nếu trước đó Fit bằng một viewport cũ, chúng có thể giữ kích thước cũ. Vì vậy InitialViewMode=Fit sau present ảnh chưa có cùng ngữ nghĩa với nút Fit.

### 2.3 Điều kiện tranh chấp layout

`LayoutTransform` làm scale tham gia measure/arrange, nên thay đổi zoom có thể cần nhiều hơn một lượt dispatcher để các giá trị sau cùng đồng bộ:

- `Image.ActualWidth/ActualHeight`;
- `ScrollViewer.ExtentWidth/ExtentHeight`;
- `ScrollViewer.ViewportWidth/ViewportHeight`;
- `ComputedHorizontalScrollBarVisibility`;
- `ComputedVerticalScrollBarVisibility`;
- `HorizontalOffset/VerticalOffset`.

Chờ một callback ở `DispatcherPriority.Loaded` không phải bằng chứng rằng các giá trị trên đã hội tụ; `Loaded` mô tả phần tử đã load, không phải “mọi lượt layout phát sinh từ binding hiện tại đã ổn định”.

### 2.4 Tác nhân có thể ghi đè Fit

`ImagePresenter` gọi `ApplyInitialViewMode()` khi thumbnail được present và gọi lại khi full preview được present. Nếu người dùng Fit trong lúc pipeline thumbnail → full preview đang chạy, callback present sau đó có thể áp dụng lại mode bằng viewport `(0,0)`. `_viewportOperationVersion` hiện chỉ ngăn continuation wheel cũ; nó không ngăn callback initial-mode đến từ presenter.

### 2.5 Vì sao unit test hiện tại vẫn xanh

Các test chỉ chứng minh `ViewerState` đổi property đúng và anchor math đúng. Chúng không tạo `ScrollViewer`, không làm scrollbar xuất hiện/biến mất và không quan sát nhiều lượt layout WPF. Do đó 741 test PASS không phủ được lỗi hai lần Fit.

## 3. Quyết định thiết kế

### 3.1 Fit là một transaction UI có version

Tạo một coordinator/hàm UI duy nhất cho Fit. Mỗi request tăng `_viewportOperationVersion`; mọi continuation chỉ được thay đổi offset nếu version vẫn hiện hành và cửa sổ còn loaded.

### 3.2 Hội tụ theo trạng thái, không theo thời gian

Sau khi chuyển sang Fit:

1. Đặt state Fit với kích thước vùng client ổn định ban đầu.
2. Yêu cầu WPF cập nhật layout.
3. Chờ một lượt render/layout bằng dispatcher/event thích hợp.
4. Đọc lại viewport, extent và visibility của scrollbar.
5. Nếu viewport thay đổi đáng kể, cập nhật `MaxImageWidth/MaxImageHeight` thêm một lượt.
6. Lặp có giới hạn tối đa 2–3 lượt; dừng sớm khi viewport và extent ổn định trong epsilon.
7. Reset offsets sau lượt layout cuối, rồi xác nhận offset đã clamp về 0.

Không tạo vòng lặp vô hạn; không giữ subscription `LayoutUpdated` sau khi transaction kết thúc; không dùng delay.

### 3.3 Một nguồn viewport duy nhất

Tách phép đo viewport thành helper có hợp đồng rõ:

- kích thước client khả dụng không phụ thuộc trạng thái scrollbar cũ cho lượt đầu;
- kích thước `ScrollViewer.ViewportWidth/Height` sau layout cho lượt xác nhận;
- reject `NaN`, infinity, zero hoặc kích thước chưa sẵn sàng;
- dùng epsilon để tránh cập nhật property liên tục vì sai số subpixel/DPI.

### 3.4 InitialViewMode phải đi qua UI owner

Không để production sink áp dụng Fit với `(0,0)`. Callback present phải yêu cầu MainWindow/UI viewport owner áp dụng mode với kích thước thật, hoặc dùng một viewport provider được nối sau khi window khởi tạo. Không đưa WPF types vào Core/ViewModel chỉ để giải quyết layout.

### 3.5 Bảo toàn wheel zoom

Giữ wheel thường giống Windows Photos và anchor tại con trỏ. Thay đổi Fit không được làm wheel chậm, không làm mất clamp min/max và không làm phím Zoom In/Out đổi hợp đồng.

## 4. Danh sách task triển khai

### T89.1 — Ghi probe layout tái hiện lỗi

- **Trạng thái:** `TODO` — source analysis đã xác định được stale viewport; chưa thêm probe runtime.
- **Có thể chạy độc lập:** Có, read-only/diagnostic.
- **Files dự kiến:** `{App}/MainWindow.xaml.cs`, test/probe chuyên biệt nếu cần.
- **Làm:** thu thập trước Fit, sau state change, sau từng layout pass và sau reset offset: zoom, stretch, max image size, actual image size, viewport, extent, offsets, computed scrollbar visibility và operation version.
- **Đầu ra:** một trace chứng minh viewport lần đầu thay đổi khi scrollbar biến mất; probe không bật mặc định trong production.
- **Xong khi:** có thể phân biệt rõ stale viewport, present callback ghi đè và offset reset quá sớm.

### T89.2 — Tách snapshot và tiêu chí ổn định

- **Trạng thái:** `IN PROGRESS` — transaction dùng giới hạn 3 pass; snapshot/epsilon helper riêng chưa tách.
- **Có thể chạy độc lập:** Có; có thể giao agent riêng viết pure helper/tests.
- **Files dự kiến:** `{App}/MainWindowHelpers.cs`, `{AppT}/ViewModels/ViewerStateTests.cs` hoặc test helper mới.
- **Làm:** thêm kiểu snapshot thuần dữ liệu và helper so sánh viewport/extent với epsilon; validate finite/positive; định nghĩa số lượt tối đa.
- **Đầu ra:** logic hội tụ có unit test, không phụ thuộc timing máy.
- **Xong khi:** test phủ unchanged, scrollbar transition, subpixel noise, invalid measurement và non-convergence cap.

### T89.3 — Thay Fit một-pass bằng transaction hội tụ

- **Trạng thái:** `DONE (implementation)` — `ApplyFitViewAsync` dùng tối đa 3 lượt `UpdateLayout` + Render, version cancellation và reset offset cuối.
- **Có thể chạy độc lập:** Không; phụ thuộc T89.2.
- **Files dự kiến:** `{App}/MainWindow.xaml.cs`.
- **Làm:** sửa `ApplyFitViewAsync()` để tăng version, áp dụng state Fit, chờ layout/render, đo lại, cập nhật viewport nếu cần, chờ lượt cuối rồi reset offsets. Mọi continuation kiểm tra version và `IsLoaded`.
- **Đầu ra:** một click tự thực hiện phần “click lần hai” ở bên trong nhưng chỉ khi measurement cho thấy cần thiết.
- **Xong khi:** một click và hai click cho cùng snapshot cuối; request cũ không ghi offset sau request mới.

### T89.4 — Đồng nhất mọi entry point của Fit

- **Trạng thái:** `DONE (implementation)` — nút, shortcut và compatibility hook dùng cùng transaction UI.
- **Có thể chạy độc lập:** Không; phụ thuộc T89.3.
- **Files dự kiến:** `{App}/MainWindow.xaml.cs`, `{App}/ViewModels/MainViewModel.cs` nếu cần thu hẹp API.
- **Làm:** nút, shortcut, compatibility hook và command đều gọi transaction UI duy nhất. Không để đường nào chỉ gọi `ViewerState.ResetFit()` rồi bỏ qua layout.
- **Đầu ra:** ma trận entry point → cùng kết quả.
- **Xong khi:** không còn đường Fit tương tác nào bypass coordinator.

### T89.5 — Sửa InitialViewMode production dùng viewport thật

- **Trạng thái:** `IN PROGRESS` — thêm refresh khi `CurrentImage`/`MainImage` đổi để bù callback production `(0,0)`; cần kiểm tra GUI và cân nhắc loại bỏ hẳn `(0,0)` ở sink.
- **Có thể chạy độc lập:** Có thể khảo sát song song; khi merge phải phối hợp T89.3.
- **Files dự kiến:** `{App}/App.xaml.cs`, `{App}/Services/WpfPresentationSink.cs`, `{App}/MainWindowHelpers.cs`, interface/callback liên quan nếu thật sự cần.
- **Làm:** bỏ callback production `ApplyInitialViewMode(..., 0, 0)`; chuyển yêu cầu về UI owner có viewport thật. Bảo đảm thumbnail và final preview không ghi đè một Fit mới hơn của người dùng.
- **Đầu ra:** production và test dùng cùng hợp đồng viewport; navigation generation hoặc operation version xử lý callback cũ.
- **Xong khi:** đổi ảnh ở InitialViewMode=Fit cho kết quả đúng ngay, kể cả ảnh trước đang zoom.

### T89.6 — Kiểm soát event SizeChanged/layout feedback

- **Trạng thái:** `IN PROGRESS` — thêm `MainImage.SizeChanged` và giới hạn 3 pass; chưa có probe CPU/layout loop.
- **Có thể chạy độc lập:** Không.
- **Files dự kiến:** `{App}/MainWindow.xaml.cs`.
- **Làm:** giữ resize-window cập nhật Fit nhưng tránh feedback loop; không dựa riêng vào `ImageScroll_SizeChanged`; chỉ cập nhật khi measurement hữu hiệu và khác quá epsilon.
- **Đầu ra:** resize/fullscreen/DPI không làm rung kích thước hoặc chạy layout vô hạn.
- **Xong khi:** resize liên tục vẫn ổn định và không tăng CPU bất thường.

### T89.7 — Test hành vi WPF và regression

- **Trạng thái:** `TODO`
- **Có thể chạy độc lập:** Có sau khi API T89.2/T89.3 ổn định.
- **Files dự kiến:** `{AppT}` và/hoặc integration test STA mới.
- **Làm:** tạo test có `ScrollViewer` + `Image`, dispatcher STA và scrollbar Auto; bắt đầu từ zoom có cả hai scrollbar, gọi Fit một lần, pump layout, kiểm tra viewport/extent/offset/state.
- **Cases bắt buộc:** ảnh dọc, ảnh ngang, chỉ scrollbar ngang, chỉ scrollbar dọc, cả hai scrollbar, DPI/subpixel, window resize, Fit lặp lại, Fit trong lúc wheel continuation cũ, Fit giữa thumbnail và final preview.
- **Đầu ra:** test thất bại trên baseline `a923106` ít nhất ở case một-click convergence và pass sau sửa.
- **Xong khi:** test chứng minh triệu chứng hai-click không tái diễn ở level WPF.

- **Tiến độ 2026-09-20:** thêm unit test loại bỏ letterbox và bù anchor theo screen delta; targeted 19/19, App 169/169. Test STA với ScrollViewer thật vẫn còn `TODO` nếu GUI tiếp tục sai.
- **Tiến độ 2026-09-20:** triển khai pan bằng kéo chuột trái trên ảnh zoom; helper pan đã có test bốn hướng/clamp. App 171/171 và full xUnit 745/745; GUI pan acceptance còn chờ.

### T89.8 — GUI acceptance

- **Trạng thái:** `TODO`
- **Có thể chạy độc lập:** Không; thực hiện sau automated tests.
- **Người thực hiện:** người dùng hoặc môi trường có quyền điều khiển native window.
- **Checklist:** wheel ở tâm/bốn mép; zoom nhiều nấc; Fit một lần; Fit bằng phím; đổi ảnh sau zoom; thumbnail→full preview; resize; fullscreen; ảnh dọc/ngang; kiểm tra lần Fit thứ hai không làm ảnh nhảy thêm.
- **Bằng chứng:** ghi PASS/FAIL từng case và screenshot nếu còn sai.
- **Xong khi:** lần Fit đầu chuẩn; lần Fit thứ hai không tạo thay đổi nhìn thấy.

### T89.9 — Full validation, trạng thái và phát hành branch

- **Trạng thái:** `TODO`
- **Có thể chạy độc lập:** Không.
- **Làm:** chạy test mục tiêu, App suite, `tools/verify-all.ps1`, publish đúng thư mục mặc định, verify-release; cập nhật tracker và `task_on_progress.md`; commit nhỏ, push branch, cập nhật PR.
- **Xong khi:** toàn bộ gate PASS, GUI acceptance PASS, T89 chuyển `DONE`.

## 5. Thứ tự thực hiện và hợp nhất multi-agent

Nếu dùng multi-agent theo yêu cầu riêng của người dùng:

1. Agent A: T89.1 — trace/reproduction, không sửa behavior.
2. Agent B: T89.2 — pure convergence helper và unit tests.
3. Agent C: T89.5 — audit production sink/presentation ordering, đề xuất patch trong file scope riêng.
4. Coordinator: hợp nhất bằng chứng, thực hiện T89.3/T89.4/T89.6, sau đó nhận tests T89.7.

Không cho hai agent cùng sửa `MainWindow.xaml.cs`. Mỗi agent phải bàn giao file scope, diff, test và giả định; coordinator chịu trách nhiệm giải quyết callback/version contract trước khi merge.

## 6. Tiêu chí hoàn thành định lượng

- Fit lần đầu và lần thứ hai khác nhau không quá 0.5 DIP ở kích thước/position.
- `HorizontalOffset` và `VerticalOffset` cuối ≤ 0.5 DIP khi ảnh Fit không cần scroll.
- Không còn scrollbar khi ảnh đã Fit và kích thước nguồn cho phép Uniform nằm trong viewport.
- Transaction Fit tối đa 3 layout pass và tự kết thúc.
- Không subscription event bị giữ lại sau transaction/cửa sổ đóng.
- Wheel anchor tests, ViewerState tests và WPF layout tests đều pass.
- `verify-all.ps1`, smoke, fault injection, publish và verify-release pass.
- GUI ảnh dọc/ngang xác nhận Fit một lần chuẩn và Fit lần hai không nhảy.

## 7. Rủi ro và biện pháp giảm thiểu

- **Layout loop:** giới hạn pass, epsilon, version cancellation.
- **Race thumbnail/full preview:** gắn request với navigation/viewport operation generation; callback cũ không được thắng thao tác người dùng mới.
- **UI latency:** không delay; tối đa vài layout/render pass; không decode lại ảnh.
- **DPI và scrollbar metrics:** đo từ control sau layout, không hard-code chiều rộng scrollbar.
- **Window đóng giữa await:** kiểm tra version, `IsLoaded` và dispatcher shutdown.
- **Regression zoom:** giữ anchor helper và wheel path độc lập; chạy lại edge cases min/max.
- **Kiến trúc:** không đưa `ScrollViewer`/WPF vào Core hoặc `ViewerState`.

## 8. Rollback

- Giữ mỗi thay đổi behavior trong commit riêng sau plan commit.
- Có thể revert commit implementation mà vẫn giữ plan/bằng chứng.
- Mốc quay lại hiện tại: `a923106`.
- Không reset hoặc sửa trực tiếp `master`; mọi thay đổi tiếp tục trên `codex/t89-pointer-anchored-zoom`.

## 9. Điều không làm trong task này

- Không thay decoder backend, preview cache hoặc loading mode.
- Không thay UI Settings ngoài phần liên quan trực tiếp đến áp dụng InitialViewMode.
- Không đổi zoom range/step nếu GUI acceptance không chứng minh cần thiết.
- Không sửa Compare viewer.
- Không đánh dấu T89 `DONE` chỉ dựa trên unit tests.
