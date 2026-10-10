---
id: NOWPF-HANDOFF-20261010
order: 402
summary: |-
  Bàn giao 2026-10-10: trạng thái đợt NO-WPF (đợt 1 gần xong), việc dở theo PR, kiểm toán test CI (A cổng CI, B an toàn dữ liệu: 3 lỗ hổng test thật), việc kế tiếp theo thứ tự ưu tiên, bẫy vận hành.
---

# Bàn giao 2026-10-10 (đọc file này đầu tiên khi tiếp tục)

Kế hoạch gốc: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) và thẻ gói [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Mỗi gói đã merge có fragment `NOWPF-WPxx-*.md` cạnh file này. Người dùng nói chuyện bằng tiếng Việt.

## 1. Đã vào master
- Phản hồi người dùng 2026-10-09 (#383-#391): External Editor luôn hiện, Copy tên/đường dẫn, menu gọn, Fit width thứ hai (phím 4)
  và chuột giữa gán được, mở file đúng vị trí, undo Delete tại chỗ, từ chối Delete khi ổ không có Thùng rác, nhãn zoom,
  Click-zoom không toggle, hết khung zoom lệch cuộn, WebP/HEIC qua WIC (cần HEVC extension), menu chuột phải tuỳ biến,
  menu Explorer cho thư mục, vuốt touchpad, Refresh (phím R). Không làm: mục 18 (Open Folder quét thư mục con), mục 20 (rename).
- Khởi động: P-1 (#396, -31 %), cloak và placement (#394), budget docs 32 KB cho architecture.md và OPEN-DECISIONS.md (#393).
- NO-WPF: WP-01 khung và hợp đồng v1 (#399), WP-13a (#400), WP-11 (#401), WP-13b (#402), WP-02 (#408), WP-07 (#410),
  WP-19a (#404), WP-08 (#403), WP-14 (#406), WP-18 (#409), WP-10 golden (#405), bước pan phím bằng nhau (#407).

## 2. Đang mở hoặc dở (xem `gh pr list`)
- **#411 WP-16 engine viewport**: `ViewportInputGoldenTests.EveryRecordedScript_ReplaysOnTheEngine` XANH với golden thật (85 kịch bản
  chặn; 512 -> 0 checkpoint lệch). Nguyên nhân đều ở RUNNER, không phải engine: (1) override JSON đọc phân biệt hoa thường nên
  `keyboardZoomStepPercent` bị bỏ qua (bước wheel 0,1 thay vì 0,5; Ctrl+wheel ở chế độ Navigate không áp); (2) bộ ghi hiện ảnh đầu
  với cài đặt mặc định + Fit RỒI MỚI áp override (runner áp trước); (3) thiếu Touchpad hint, chuột giữa, trạng thái nút trái ở `move`,
  con trỏ kịch bản (PointerPosition), Timestamp theo `frame`; (4) `Next/Previous` là no-op. 5 kịch bản `ArrowPan-pending-rule-change|`
  bị loại khỏi phát lại (như WPF, WP10 mục 5) vì ghi theo quy tắc pan CŨ: chờ ghi lại golden bằng `tools/diag/record-golden.ps1`
  rồi bỏ tiền tố và dòng `continue` trong test. Chưa bật auto-merge. Test G-VIEW (Shell.Tests) 103 xanh.
- **#412 WP-04 cache đĩa bằng WIC** (WIP): mã và fixture tương thích hai chiều (`tests/Fixtures/PreviewCache`) có, 17 + 4 test xanh.
  Chưa: Architecture/App tests, đo hiệu năng 24 MP, mutation, decision fragment. Cần quyết: codec WPF đang là mặc định khi không truyền
  codec (WP-06 phải chuyển sang tiêm bắt buộc); ba thay đổi hành vi nhỏ ghi trong mô tả PR (EXIF hỏng vẫn dùng cache, Rgba64 đục được cache,
  PNG alpha qua Pbgra32).
- **#413 WP-05 TurboJpeg/LibRaw ra PixelBuffer** (WIP): parity byte-exact ở full size và DCT factor; fine-scale MAE 0,26-3,6
  (không byte-exact với WIC Fant, chọn giữ hoặc dùng IWICBitmapScaler). Chưa: mutation, bench P50, parity LibRaw native
  (máy không có RAW corpus), docs-budget.
- **#414 WP-03 WIC decode ra PixelBuffer** (WIP): mã và test xong, parity byte-exact 30 ảnh x 8 orientation x {full, DecodeBox}, WebP 32 ca,
  thumbnail 40 ca; Imaging.Tests xanh, Architecture 86 xanh. Chưa: decision fragment (`NOWPF-WP03-*`), mutation, bench decode 24 MP
  (rủi ro perf: `GC.AddMemoryPressure` cho buffer tạm). Đề xuất v1.1: khoá `ExifOrientation.IsTransposed`/`Normalize`. Có thể còn sót thư mục
  `.claude/worktrees/wp03-base` (xoá tay).
- **#415 WP-15 renderer Direct2D** (WIP): `DeviceResources`, `D2DRenderSurface`, `D2DDrawContext`, `GpuImageCache` (LRU theo byte), tile
  <= 16384 không lộ đường nối, mất thiết bị (tạo lại, xoá cache, `DeviceRecreated`), swap chain flip-model, MaxFrameLatency 1, 8 orientation.
  Shell.Tests Rendering 91 xanh (kể cả Native WARP). 1:1 byte-exact; PSNR 47-56 dB; pan CPU submit P95 0,26-1,74 ms (<= 3 ms đạt);
  upload 24 MP P50 ~40 ms. RỦI RO: HQC thu nhỏ 24 MP mỗi khung 37-45 ms trên GPU (zoom < 100 % trên ảnh original ở WP-21; ở Fit vẽ
  preview cỡ viewport nên không gặp). Chưa: mutation, decision fragment (order dự kiến 542) và generate/check-open-decisions, đo lại khi máy
  rảnh (số hiện nhiễu vì CPU 99 %). Đề xuất v1.1: C-09 `static abstract` cản fake và không dùng được làm type argument, thay bằng
  `IRenderSurfaceFactory`.
- Golden pan phím (5 kịch bản `ArrowPan-pending-rule-change|`): #407 đã merge, cần ghi lại bằng `tools/diag/record-golden.ps1`
  và bỏ hằng `InputScriptCatalog.ArrowPanPending` (xem NOWPF-WP10-GOLDEN-RECORDER mục 5).
- Hợp đồng v1.1 (C-07: dời IImageSurface, IFitSurface, ViewportSnapshot sang App.Shared): PR nhỏ của lead; đặt
  `PHOTOREVIEW_REGENERATE_CONTRACTS=1` rồi chạy `ContractSurfaceTests.WriteApprovedSurface`.
- **WP-14 đã merge (#406) nhưng 3 đột biến SỐNG SÓT** (22/25 bị bắt; log thô trong scratchpad `mutesults.txt`): M7 `MessageLoop.Run` chạy việc Background kể cả khi còn message chờ; M23 `Win32UiSynchronizationContext.Post` dùng Background thay Normal; M24 `YieldAsync` dùng RunContinuationsAsynchronously (comment nói phải inline). Cần 3 test: Background nhường message đang chờ; SynchronizationContext.Post chạy trước việc Background xếp trước đó; continuation sau `await YieldAsync(p)` chạy trong cùng việc mức p.
- Gói chưa mở: WP-06 (tách Imaging.Wpf, phụ thuộc WP-03/04/05), WP-09, WP-12, WP-17, WP-20 đến WP-37.

## 2b. Tiến độ sau bàn giao (cùng ngày, theo thứ tự)
Lỗ hổng test đã có PR riêng, mỗi PR đã kiểm XANH trên master và ĐỎ khi hoàn tác đúng lỗi (đột biến):
- #417 B-02 (group Recycle không opt-in phải bị từ chối), #418 C-01 (ghim 70 khoá config.json), #419 B-03 (IRecycleBin production phải tự
  khai báo CanRecycle/FitsInRecycleBin/DeletePermanently; còn sửa `BenchmarkRecycleBin.TempCopyDeleter`), #420 D-01 (2 test zoom-detail
  chạy trong CI), #421 C-03 (mặc định hiệu năng bằng literal).
- Chưa làm trong số "Top": C-04 (Save sau Load config bản mới mất trường lạ), C-05 (optional trùng mandatory), D-02/D-03 (hook
  IDialogService, StaTestHost fail khi AppLog có Error), B-04..B-08, A-01..A-07 (CODEOWNERS, job Native/Slow, baseline số test), sửa runner #411.
- #413 (WP-05) đã được đồng bộ master bằng `tools/sync-pr-branch.sh`.
- **#411 (WP-16) runner đã nói đúng từ vựng WP-10** (commit 158fae4f): `command` (ToggleFit, ZoomActualSize, FitWidth/2, FitHeight, ZoomIn/Out,
  ClickZoom, SetClickZoomLevel, ZoomToLevelMenu, SetDpi, LoadImage, SwapSourceSize) và `key` qua ShortcutRouter. Cả 90 kịch bản chạy; còn
  **512 checkpoint lệch golden** — việc điều tra tiếp theo. Chênh đầu tiên: `wheel-zoom-in-to-step-limit` zoom engine 0,68/1,18/1,68 vs golden
  0,28/0,38/0,48 (engine bước 0,5, golden 0,1 mỗi nấc wheel: nghi `ViewerState.ZoomStep`/bước wheel của runner khác MainWindow, vì cả hai
  dùng KeyboardZoomStepPercent); `wheel-navigate-mode-ctrl-wheel-zooms`: engine vẫn ở Fit (IsFit True) trong khi golden zoom 0,28 (nghi
  MouseWheelAction=Navigate + Ctrl không áp qua SettingsOverridesJson, hoặc WheelInput thiếu tham số Touchpad). Chạy lại:
  `dotnet test tests/PhotoReview.App.Tests --filter FullyQualifiedName~ViewportInputGoldenTests` (không sửa golden).

## 3. Quyết định chủ dự án còn mở
- NE-3 thanh cuộn (a: tự vẽ), NE-4 menu (a: native + dark qua uxtheme), NE-6 single-instance prefix riêng, NE-7 đóng băng UI
  trên bản WPF từ đợt 3, NE-9 chất lượng scale: chốt TRƯỚC đợt 3.
- Khởi động: NW-5 (<= 600 ms R2R) chưa đạt (782 ms). Bước kế: (a) self-contained + R2R composite (-140 ms, 174 MB),
  (b) hoãn UI thấy được tới sau ảnh đầu (-100 ms). P-STARTUP-FIRST-IMAGE: liên kết .jpg đang trỏ bản build; bản publish R2R nhanh hơn
  150-250 ms. Chế độ chạy nền (giữ tiến trình, khay): người dùng chọn nhầm rồi huỷ, chưa làm, hỏi lại.
- ĐÃ CHỐT 2026-10-10 (người dùng chọn b): fine-scale của WP-05 dùng **WIC Fant (IWICBitmapScaler)**, không dùng PixelAreaResampler
  tự viết (chậm hơn WIC 30-90 %, vượt ngưỡng 5 %). WP-06 nối codec theo hướng này; đo lại benchmark lúc máy yên.
- ĐÃ LÀM 2026-10-10: liên kết .jpg trỏ bản publish R2R `C:\MyProjects\PhotoReview\publish-r2r` (15 MB, phụ thuộc framework,
  git-ignored cục bộ); phải publish lại sau mỗi lần merge mã. Phương án 174 MB (self-contained + composite) chưa chọn.
- Native AOT (đợt 5): cần MSVC x64 và Windows 11 SDK (4-5 GB); ổ C: gần đầy (14 GB trống), dọn `work/diag/tune-runs`.
- Sửa Fit width/Fit height lệch 5 DIP (nửa độ dày thanh cuộn) trong PointerInputController: PR riêng trên app WPF.

## 4. Kiểm toán test CI (người dùng: "nếu test sai hậu quả rất nghiêm trọng")
**A, cổng CI** (đã xác minh bằng `gh api` và `--list-tests`): CI chỉ chạy HotPath, UI, Integration. Native, Slow, Manual KHÔNG bao giờ
chạy trong CI: Thùng rác thật (`NativeRecycleBinTests`), junction/SEC-01, nén journal đồng thời, RAW thật. Branch protection:
required `build-test-publish` và `repo-checks`, KHÔNG bắt buộc review, `strict:false`, admin bypass, không CODEOWNERS; workflow RAW
corpus chưa từng chạy. Top 5 sửa trước: (1) CODEOWNERS + review bắt buộc cho `.github/**`, Architecture.Tests, allowlist, verify-all,
test.runsettings, bật enforce_admins, bước diff allowlist với master; (2) job Native/Slow trên runner GitHub (VM dùng một lần, an toàn
cho Recycle Bin), hằng đêm và bắt buộc trước release; (3) baseline số test theo project từ trx, fail khi 0 hoặc giảm hoặc Skipped > 0;
(4) lên lịch raw-corpus.yml (thêm Core.Tests), đổi `return` sớm thành skip nhìn thấy được; (5) vá luật: regex CORE-FS bỏ lọt
`new FileInfo(p).Delete()`, bỏ opt-out Integration của TEST-OS, lượt test Debug cho ReviewCatalog thread guard, `strict:true` hoặc merge queue.

**B, an toàn dữ liệu** (12/37 đột biến đã chạy; script `auditB-mutate2.ps1` trong scratchpad của phiên, chạy lại được):
- **B-02 HIGH (đột biến M07 SỐNG SÓT)**: `FileActionService.ExecuteGroupAsync` dòng ~144 `if (permanent && !request.AllowPermanentDelete)`:
  xoá kiểm tra này thì group Recycle trên ổ không có Thùng rác xoá VĨNH VIỄN mà không test nào đỏ (0/1026). Test cần thêm: fake bin
  `CanRecycle=false`, `ExecuteGroupAsync(Recycle, AllowPermanentDelete=false)` phải ném `CoreRecycleUnsupportedDrive`, không gọi
  DeletePermanently, mọi member còn nguyên, journal không có dòng.
- **B-03**: `IRecycleBin` có mặc định `CanRecycle => true` / `FitsInRecycleBin => true` (IRecycleBin.cs:15,34); shell no-WPF quên override
  sẽ coi mọi ổ có Thùng rác. Thêm test kiến trúc ép override hoặc bỏ mặc định.
- **B-01**: Move khác volume + journal Fast: ADR 0007 chỉ cam kết process-crash, không power-loss; quyết định sản phẩm
  (copy + flush đích rồi mới xoá nguồn).
- B-04 preflight group chỉ 1 test cho "đích đã tồn tại"; B-05 RecoveryRetry group không kiểm lại định danh trước xoá; B-06 Recycle đơn không
  hậu kiểm "nguồn đã biến mất", fake bin trong FileActionServiceTests không xoá file mà vẫn xanh; B-07 `WindowsRecycleBin` chưa từng
  mutation-test (chốt chặn cuối); B-08 M04 (fsync) và M12 (rollback Copy) mỗi cái chỉ 1 test; B-09 test Native file-action ngoài CI.
- Chưa chạy: M05, M06 (Recycle đơn bỏ CanRecycle/opt-in) và M13-M37 (Undo, Recovery, WindowsRecycleBin, FileActionController).
**C (settings/catalog/cache; ~50 đột biến Core chạy thật, phần Imaging CHƯA đột biến vì baseline Imaging có ~30 test đỏ sẵn ở
SourceBytesCache/RawDecoder*Cache trong worktree audit — nguyên nhân chưa rõ, nghi môi trường chung)**:
- **C-01 HIGH, đột biến SỐNG SÓT**: đổi tên khoá JSON của `ClickZoomKeyTogglesFit` thì 0 test đỏ; mọi round-trip dùng cùng context nên tự khớp.
  Một "refactor sạch" làm MẤT CẤU HÌNH người dùng lặng lẽ khi nâng cấp. Cần: golden config.json v3 đầy đủ khoá (sinh từ bản đã phát hành,
  đóng băng) + test khoá danh sách tên khoá + byte-lock `Serialize(new AppSettings())` (C-02).
- C-03 SỐNG SÓT: đổi mặc định `MemoryReserveBytes`, `PreviewDiskCacheCapacityBytes`, `PreloadMemoryLoadLimit`, `UseSourceBytesCache`,
  `DefaultKeyboardZoomStepPercent` không test đỏ (test so với chính hằng số): thêm assert literal. C-04 ĐÃ KHOÁ (test đặc trưng, known limitation: Save sau khi Load file
  ConfigVersion > 3 ghi lại v3, mất trường lạ, không backup — chờ chủ dự án quyết). C-05 ĐÃ KHOÁ (Theory mọi cặp mandatory x optional).
- C-06/C-07/C-08: PreviewCacheFile v5-reject, round-trip kích thước gốc, alpha-reject, DiskCacheStore, PreviewImageService đều Slow ngoài CI;
  JPEG CMYK/progressive/gray chỉ có ở Native; nhiều test HotPath RAF/DNG/ORF `return` sớm khi thiếu corpus (xanh giả).
- C-11/C-12/C-13/C-14 (chưa đột biến, chỉ đọc mã): không có fixture .pv4 v6/v7 sinh từ bản phát hành; không ghim identity hash cache đĩa
  (va chạm khoá phục vụ ảnh sai); ExifSummaryCodec đối xứng nên đổi bit không bị bắt; persist alpha chỉ ở Slow.
- Đã khoá tốt: Save không ghi đè khi Load hỏng hoặc bị khoá, HiddenContextMenuItems từ cờ cũ, MiddleClickAction, sắp xếp tự nhiên và tie-break.
- Còn nợ: 20 đột biến phía Imaging (I1-I15: orientation WicDirect/TurboJpeg, bỏ kiểm alpha, đổi header PreviewCacheFile, đổi identity hash đĩa,
  ExifSummaryCodec, prune order) sau khi xác định vì sao baseline Imaging có test đỏ.
**D (UI/tích hợp; chỉ 1/20 đột biến đã chạy, script `auditD-mut.ps1` M01-M20 chạy lại được)**:
- **D-01 HIGH**: 2 test gating `MainWindowZoomDetailTests` (zoom-to-100 % đổi preview sang bản gốc; Refresh khi đang zoom) có trait `UI`+`Slow`
  nhưng KHÔNG `Integration` nên không bao giờ chạy trong CI. Thêm `Integration` và test kiến trúc "Slow kèm UI phải kèm Integration".
- **D-02 HIGH (chưa đột biến M19/M20)**: không test UI nào xác nhận hộp thoại lỗi/xác nhận XUẤT HIỆN; `WpfDialogHost` (Stryker-disabled) luôn trả Yes;
  composition không truyền dialog service thì `?.ShowError` im lặng. Cần hook IDialogService ghi lại trong TestAppHost.
- **D-03 HIGH**: lỗi ở task nền fire-and-forget (`FireAndLog`) chỉ `AppLog.Error`, không test nào đỏ; StaTestHost.Pump nên fail khi AppLog có Error.
- D-05: đột biến M01 (bỏ khôi phục tại chỗ khi undo Recycle, #386) CHẾT ở App.Tests nhưng SỐNG ở 245 test UI: chưa có test cửa sổ thật cho undo-xoá.
- D-06/D-08: `[GoldenFact]` thiếu file golden thì Skip (xanh với 0 so sánh), reader khoan dung, kịch bản `Expected` rỗng xanh giả: golden thiếu = FAIL,
  schema chặt, Expected.Count > 0. D-07: scanner TEST-02/OS/TAUTOLOGY không thấy attribute dẫn xuất `*Fact`/`*Theory`.
- D-09: replay golden dựng lại controller bằng tay (drift khỏi MainWindow thật); pan phím chưa gating; D-10: test Native Shell ngoài CI (struct size viết tay,
  cùng tác giả). D-04/D-11..D-16 mức thấp-vừa (guard hộp thoại không ai đọc trong vài Rig, `_ = NextAsync()` không quan sát, `run-tests-hidden.ps1` bỏ qua
  giá trị trả về `GetExitCodeProcess`, Soak flake dưới tải).
- Baseline máy tải: `SoakLeakTests.Navigation_Soak_Plateaus` timeout 100 s và `APP-P01` flaky; không phải lỗi logic.

## 4b. Nguyên nhân chính xác #411 đỏ
`InputScriptRunner.Apply` (App.Tests/Viewport) ném `NotSupportedException` ở bước `command` với `Command = ToggleFit`: runner chỉ biết Fit, FitWidth,
FitWidth2, FitHeight, ZoomIn, ZoomOut, ActualSize, ClickZoom, ClickZoomLevel; bộ ghi WP-10 xuất tên `ReviewCommandType` (vd `ToggleFit`) cộng
`SetClickZoomLevel`, `ZoomToLevelMenu`, `SetDpi`, `LoadImage`, `SwapSourceSize` (xem `InputScriptRecorder.CommandAsync` trên master). Sửa: ánh xạ
đủ từ vựng đó trong runner (không sửa golden), rồi xem golden còn lệch ở đâu.
**E (quét tĩnh)**: 42 test Native/Slow/Manual về dữ liệu ngoài CI; 54 test tự thoát sớm (22 RAW corpus, 7 WebP); 150 khối catch rỗng;
vài `Assert.InRange` gần như không ràng buộc (`PhotoReviewPerfEventTests:131`, `KineticPanTests:353`); số test Core/Imaging lệch ~360
so với đếm attribute (chưa giải thích).

## 5. Bẫy vận hành đã gặp
- Xung đột NGỮ NGHĨA giữa PR: hai PR đúng riêng lẻ vẫn vỡ biên dịch khi gộp (IImageSurface sang PointD/KeyId). `strict:false` nên CI
  không bắt trước khi merge: build nhánh SAU khi sync master, trước khi để auto-merge.
- Bảng OPEN-DECISIONS.md xung đột ở mọi PR: dùng `tools/sync-pr-branch.sh <worktree>` (lấy bản master, sinh lại, kiểm `order` trùng,
  giữ cả hai phía khoá i18n cuối file).
- Scratchpad dùng chung giữa agent: file tạm phải có tiền tố tên gói (đã va chạm mutate.py, mutation-results.txt).
- Agent ghi nhầm file vào checkout chính (file rỗng `Win32UiDispatcher.cs`, `NO-WPF-EXEC-PLAN.md`, file `max` trong #407): kiểm
  `git status` của checkout chính định kỳ. `AppSettingsPocoTests.cs` untracked có từ trước phiên, để nguyên.
- Windows: xoá worktree dài cần tiền tố long-path; thư mục tạm có thể bị khoá (Permission denied), git đã gỡ đăng ký.
- Tránh heredoc shell cho file chứa backtick; dùng công cụ ghi file.
- Build Release ở checkout chính sau mỗi `git merge --ff-only origin/master` (AGENTS.local.md).
