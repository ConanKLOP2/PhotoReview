---
id: NOWPF-WP28B
order: 970
summary: |-
  WP-28b (NO-WPF, 2026-10-12): mo rong G-PIX: 16 khung WPF moi (compare 2 anh, F11/DPI man hinh gia, bitmap RAW doi kich thuoc) + crossfade 0-100 % so voi blend doc lap. Do: vien/canvas compare giong tung byte, anh o PSNR >= 40,6 dB, F11/DPI lech 0 px, blend lech <= 1 muc; 18 dot bien, 17 bat, 1 tuong duong. WP28B-FIRSTFRAME: khung dau sau doi layout o thu nho > 4x la Linear (CHUA XAC NHAN). Chua kiem RAW that.
---

# NOWPF-WP28B-GOLDEN-PIXEL-EXT - mo rong golden pixel G-PIX (2026-10-12)

The: WP-28 ([NOWPF-WP28-GOLDEN-PIXEL](NOWPF-WP28-GOLDEN-PIXEL.md) muc 4 diem 3 "Chua co"), nguong: muc 7.3 (2) cua
[NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md). Tien de: [WP-21](NOWPF-WP21-PRESENTATION.md) (compare, crossfade), [WP-25](NOWPF-WP25-WINDOWING.md) (F11, DPI,
seam man hinh gia). Khong sua ma san xuat (chi test, cong cu, golden); app WPF va lien ket `.jpg` khong doi.

## 1. Da lam

| Mang | File | Noi dung |
|---|---|---|
| Danh muc | `tests/PhotoReview.TestSupport/PixelGolden/PixelGoldenExtCatalog.cs` | `Frames` (F11/DPI + bitmap-size: `PixelGoldenCase` thuong, them `BitmapWidth/Height` = kich thuoc bitmap khac ban goc), `Compare` (`PixelGoldenCompareCase`: hai o, chon, anh chinh Fit duoi panel). Man hinh gia 720x480 px (nho hon moi runner CI 1024x768) |
| Bo ghi WPF | `tests/PhotoReview.Integration.Tests/Golden/PixelGoldenExtRecorder.cs` | `MainWindow` THAT khong hien, `ComparePanel` that (dat `CompareViewModel` nhu luc nap so sanh); `PixelExtGoldenRecordTests` (ten khop bo loc cua `record-golden.ps1`), `PixelGoldenExtWpfConformanceTests` (UI, PSNR >= 42 dB). Golden: `tests/Fixtures/golden/pixel/{cmp,f11,rawsize}-*.png` |
| Cua so shell | `tests/PhotoReview.Shell.Tests/Golden/PixelGoldenWindowRig.cs` | MOT "cua so" song qua nhieu khung: viewport that, MOT be mat D2D WARP duoc `Resize` (khong tao lai), MOT cache GPU + composer; `Transition` = client DIP + DPI moi, `Frame` ve nhu `Present` (Resize bề mat -> layout -> ve) |
| Compare | `PixelGoldenCompareRenderer.cs` | ve bang `PresentationComposer` + `CompareLayer`, do vien/anh/panel/vong le 8 DIP |
| Test | `PixelGoldenExtTests.cs` (29), `PixelGoldenCrossfadeTests.cs` (8) | xem muc 2; `Category=Integration` (WARP, CHAY TREN CI) |
| Cua so that | `tests/PhotoReview.Shell.Integration.Tests/Windowing/ShellFullscreenFrameGeometryTests.cs` | `ShellStartup` THAT + seam `IFullscreenNative` (man hinh gia 720x480): sau F11 + `WM_DISPLAYCHANGE` va sau `WM_DPICHANGED`, hop Fit ma renderer ve = Fit cua man hinh gia |

Ghi lai golden: `tools/diag/record-golden.ps1` (ghi luon ca khung moi) hoac `PHOTOREVIEW_GOLDEN_RECORD=1` + `tools/run-tests-hidden.ps1 -DesktopName <ten>`
voi `--filter FullyQualifiedName~PixelExtGoldenRecordTests`. Ngan sach dung luong: khung co so <= 5 MB, khung mo rong <= 5 MB (moi nhom mot gioi han).

## 2. Nguong va so do (may dev, WARP; CI cung so vi phia shell tat dinh)

| Ca | Nguong | So do | Khung |
|---|---|---|---|
| Compare: vien 2 DIP, canvas panel, o rong, o dang nap | GIONG TUNG BYTE voi WPF (khong co dong 7.3: day la phang) | lech 0 | `cmp-*` |
| Compare: anh trong o (Uniform) | Fit cua 7.3: PSNR >= 32, SSIM >= 0,93, MAE <= 4; hinh hoc <= 1 px | PSNR **40,6-45,5**, SSIM >= 0,992, MAE 0,24-0,88; dich (0,0) | `cmp-photo-*`, `cmp-orient-*` |
| Compare: vong le 8 DIP cua anh chinh | Fit | PSNR 45,6 (DPI 1,5: 49,1) | `cmp-*` |
| Compare: o dung bo loc HQ nhu anh chinh | o == anh chinh ve vao hop cua o, tung pixel | lech 0 | `cmp-photo-pair-none` (thu nho 4x) |
| F11 man hinh gia, Fit | Fit: PSNR >= 32 | **44,2-44,3** (portrait letterbox 47,9 khung day du) | `f11-screen-fit-*` |
| F11, 100 % | giong tung byte | PSNR vo cuc; khung 77,2 dB | `f11-screen-z100-origin` |
| DPI 1,5 cung so pixel / khac so pixel | Fit | **44,2** / **49,1** | `f11-dpi150-*-fit` |
| DPI 1,5 cung so pixel, 100 % | NearIdentical (max <= 1) | max 0, PSNR vo cuc | `f11-dpi150-samepx-z100-origin` |
| RAW: preview 480x320 giua canh cua ban goc 1920x1280, Fit / 100 % | Fit / Zoom | **47,1** / **46,7** | `rawsize-*-preview` |
| RAW: ban day du | Fit / giong tung byte | **44,4** / vo cuc | `rawsize-fit-full`, `rawsize-z100-full` |
| Crossfade t = 0 / 100 % | GIONG TUNG BYTE khung cu / khung moi; het hieu ung nha dong ho | lech 0 | `PixelGoldenCrossfadeTests` (DPI 1 va 1,5) |
| Crossfade giua chung | `a*cu + (1-a)*moi`, a = (1-t)^2, <= 2 muc (KHONG phai nguong 7.3: WPF khong co khung tuong duong) | lech toi da **1** (t = 0,25; 0,5; 0,75; DPI 1 va 1,5) | idem |
| Crossfade lai giua chung / duoi compare | tween cu bi huy, khong con dau vet; panel compare che lop mo, le 8 DIP van la blend | dung | idem |

## 3. Phat hien / quyet dinh

1. **WP28B-FIRSTFRAME (CHUA XAC NHAN):** khung ve ngay sau khi cua so doi layout (thoat F11 720x480 -> 480x320, doi DPI/so pixel) o anh bi thu nho > 4x dien
   tich (anh 1200x800 vao 480x320) la khung **Linear** (`ScalingPolicy`, WP-21: HQC 24 MP ton 27-45 ms GPU/khung), HQ chi den sau `SettleMs` = 120 ms.
   Do: khung dau PSNR 45,4 dB so voi khung HQ dung yen (anh chup), nen khong thay duoc; WPF luon HQ. Test ghim: khung dau `Degraded` va khung
   dung yen giong tung byte cua so luon o trang thai do. Phuong an: (a) giu (khuyen nghi); (b) bo ha bo loc khi doi layout (anh 24 MP co the chan 27-45 ms
   khung dau sau F11/doi DPI).
2. **Khung ve do shell co khung truoc F11 (`Transition`) la mo hinh hoa, khong phai HWND:** test pixel dung viewport that va be mat `Resize` that
   nhung nap `ClientSizeDip/DpiScale` bang tay (nhu `ShellMainWindow.OnClientSizeChanged/OnDpiChanged/Present` lam). Doan keo HWND (SetWindowPos,
   WM_DPICHANGED, Refit) duoc kiem rieng (`ShellWindowingTests`, `ShellFullscreenFrameGeometryTests`). Khong doc duoc pixel cua cua so that vi `TryReadPixels`
   chi chay tren be mat offscreen; them seam vao ma san xuat bi bo qua de khong dung chung file voi cac goi dang chay.
3. **Nhap F11 tren man hinh gia:** `ShellFullscreen.Apply` dung `FullscreenController` (monitor that cua HWND), con seam `IFullscreenNative` chi cho `Refit`;
   nen test cua so that dua cua so len man hinh gia 720x480 bang `WM_DISPLAYCHANGE` (doi do phan giai luc dang F11), khong phai luc nhan F11.
4. **RAW:** repo khong co file RAW (khong co corpus), nen "RAW" o day la anh tong hop `photo` o hai kich thuoc bitmap qua duong "bitmap keo gian ra layout cua
   ban goc"; **CHUA kiem giai ma RAW that (LibRaw)** va chua kiem preview nhung co ti le/khung khac ban goc.
5. **WP28B-WARP-EDGE (2026-10-12, sua do ngau nhien tren CI):** `RawPreviewToFullDecode_...(rawsize-fit-*)` do ngau nhien (55 px lech 1 muc) vi so
   sanh "khung sau khi doi bitmap" voi "cua so moi" theo tung byte. Do bang 15 lich su tien trinh (cac khung ve truoc trong cung process): lech 0 / 55 / 64 /
   71 / 163 / 292 px (toi da 0,12 % cua 600x400), delta toi da 1, chi o cot 299 va 599 (giua va cuoi khung); moi khung ve LAI sau do giong tung byte.
   Khong phai san pham (cung dau vao, khong dong ho/trang thai chung; `Degraded` = false, khung la HQ) ma la WARP: lan ve DAU cua mot hinh hoc thu nho HQ
   (HighQualityCubic, hieu ung scale nhieu pass cua D2D) sau khi tien trinh da ve hinh hoc thu nho khac. Nguong moi chi cho ca thu nho HQ (Fit: RAW,
   F11/DPI Fit, thoat F11): delta <= 1 va <= hai cot day du (2 x chieu cao = 0,33 % cua 600x400); ca 100 % (NearestNeighbor) van giong tung byte.
   Loi that (Linear thay HQ, lech hop, anh khac) vuot ca hai con so rat xa; dot bien (khung Linear sau doi bitmap, hop lech 0,25 px, orientation, blend) van bi bat.

## 4. Kiem chung

- Build Release 0 canh bao / 0 loi; `PixelGoldenExt*` + `PixelGoldenCrossfade*` 37 test xanh (Shell.Tests); `ShellFullscreenFrameGeometryTests` 2 test xanh tren
  desktop an `PRGpixExt`; `PixelGoldenWpfConformanceTests` + `PixelGoldenExtWpfConformanceTests` xanh tren desktop an.
- Ngay cac test shell khong doc kich thuoc/so man hinh cua runner: moi kich thuoc la cua ca, be mat la WARP offscreen, khong cua so nao duoc mo (trừ test
  cua so that dung cua so tren desktop an va dong trong `finally`/`Dispose`).

## 5. Dot bien (18, thu cong: sua mot cho -> build -> loc `PixelGoldenExt|PixelGoldenCrossfade` -> tra lai bang `git checkout`)

17/18 bi bat; 1 tuong duong. Hai ca song sot luot dau: M11 (lap bang test moi `Compare_CellPicture_IsDrawnWithTheSameHighQualityFilter...`),
M13 (nham muc tieu: `FitAxis` khong duoc Fit mac dinh dung; chuyen sang `CalculateDisplaySize`, bi bat).

| # | Dot bien | Bi bat boi |
|---|---|---|
| M01 | opacity bat dau cua lop cu 1 -> 0,9 | 7 test (t = 0, blend, khoi dong lai) |
| M02 | easing QuadraticOut -> Linear | 5 test (blend giua chung) |
| M03 | lop crossfade khong bao gio duoc ve | 8 test |
| M04 | thu tu lop: compare duoi crossfade | `Crossfade_UnderACompareFrame...` |
| M05 | mau vien chon #32CD32 -> #33CD32 | 6 test compare (vien byte-identical) |
| M06 | le o 4 -> 5 | 6 test |
| M07 | do day vien 2 -> 3 | 7 test |
| M08 | chon dao nguoc | 7 test |
| M09 | le panel 8 -> 9 | 6 test |
| M10 | ti le Uniform Min -> Max | 6 test |
| M11 | o compare ve bang NearestNeighbor | `Compare_CellPicture_IsDrawnWithTheSameHighQualityFilter...` (moi) |
| M12 | snap pixel-exact lam tron len | 6 test (100 %, DPI) |
| M13 | `CalculateDisplaySize` bo DPI | 3 test (RAW 100 %, DPI 1,5 100 %) |
| M14 | `DeviceResources.Resize` giu ti le cu | 3 test (be mat khong nhan DPI moi) |
| M15 | nguong ha bo loc 4x -> 400x | 2 test (`expectDegraded`) |
| M16 | orientation 6 giai ma nhu 8 | `Compare_...cmp-orient-1-vs-6-left` |
| M17 | `ShellFullscreen.Refit` khong dich cua so | 2 test cua so that |
| M18 | `OnDpiChanged` bo `UpdateTargetDecodeBox()` | TUONG DUONG: `UpdateFitSize` (goi tu layout khi viewport doi) cung cap nhat `Viewer.DpiScale`/hop decode |

Mutation M01-M16 chay tren Shell.Tests, M17-M18 tren Shell.Integration.Tests (desktop an). Khong tim thay loi san xuat that.

## 6. Chua lam

- Giai ma RAW that + preview nhung khac ti le (can corpus RAW).
- Crossfade tren HWND that (khong doc duoc pixel cua cua so); so voi WPF `OutgoingImage` bang chup man hinh that - nhu WP28-REFERENCE.
- F11/DPI tren may hai man hinh that (xem NOWPF-WP25 muc 4).
