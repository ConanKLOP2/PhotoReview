---
id: NOWPF-WP32
order: 960
summary: |-
  WP-32 spike S1 AOT: khả thi; ảnh đầu 355-390 ms (R2R 821-933).
---

# NOWPF-WP32 - spike S1 Native AOT trên shell Win32 (2026-10-12)

Thẻ: WP-32 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); G0.1..G0.7 và NW-2 trong [NO-WPF-MIGRATION-PLAN](NO-WPF-MIGRATION-PLAN.md).
Số đo và danh sách cảnh báo đầy đủ: [perf/2026-10-12-no-wpf-spike.md](../perf/2026-10-12-no-wpf-spike.md). Phụ thuộc: NW-2 (người dùng đã cài toolchain 2026-10-12),
WP-20/WP-29 (shell + probe). Dùng chính shell thật (không viện "minimal shell": không cần).

## 1. Đã làm

| Việc | Kết quả |
|---|---|
| `dotnet publish ... -p:PublishAot=true` đúng lệnh thẻ | **NETSDK1207** ở project `netstandard2.0` (Localization.Generator): thuộc tính toàn cục chảy vào ProjectReference |
| `PublishAot` đặt trong csproj shell, Release | ILC chạy; 80 cảnh báo bị `TreatWarningsAsErrors` (Release) coi là lỗi |
| `-p:TreatWarningsAsErrors=false` + `vswhere` trong PATH | link xong, `PhotoReview.exe` 13,3 MB, smoke mã 0 |
| `startup-probe.ps1` xen kẽ R2R vs AOT, 3 lô x 10 vòng | AOT ảnh đầu 355 / 390 / 389 ms, R2R 821 / 933 / 908 ms |
| Phát hiện | `EventSourceSupport` mặc định tắt trong AOT: perf CSV rỗng, probe chết "timeout waiting for the first frame"; bật nó (+1 MB) |

Code đổi (một file, chỉ khi `-p:ShellAot=true`): `src/PhotoReview.Shell.Win32/PhotoReview.Shell.Win32.csproj` thêm `PublishAot`, `EventSourceSupport` có điều kiện và
tắt `PublishReadyToRun` cùng điều kiện. Không đổi hành vi bản thường (build/publish R2R giữ nguyên, 0 cảnh báo).

## 2. Bảng G0

| # | Đích | Đo được (AOT, máy bận 40-65 % CPU) | Kết luận |
|---|---|---|---|
| G0.1 | ảnh đầu median <= 200 ms | 355-390 ms (cửa sổ hiện ~145 ms) | **hụt**; NW-5/gate đợt 5 "<= 350 ms": sát ngưỡng, chưa kết luận (máy bận) |
| G0.2 | nhanh hơn bản hiện tại >= 700 ms | WPF ~1,2 s (wave 3) -> 0,36-0,39 s: ~-0,8 s | đạt (so với WPF, theo wave 3) |
| G0.3 | AOT nhanh hơn R2R >= 40 ms | -466..-543 ms cùng lô xen kẽ | **đạt xa** (độ lớn đội lên do tải nền; thứ tự chắc chắn) |
| G0.4 | 0 cảnh báo IL2xxx/IL3xxx | 80 (70 `WindowsRecycleBin`, 8 `ExplorerOrderService`, 2 IL3053) | **hụt**, nhưng chỉ 2 file; DI/Toolkit/Json/Enum.Parse/GeneratedCom không cảnh báo |
| G0.5 | key->frame P95 trong cache <= 3 ms | 17-20 ms (R2R 32-45 ms), P50 như nhau | hụt, độc lập với AOT (WP21-STALL + tải nền) |
| G0.6 | pan 100 %: khung > 16,7 ms <= 1 % | không đo | cần test Manual chạy trong AOT |
| G0.7 | <= 20 MB | exe 13,3; thư mục 27,9 (20,0 nếu bỏ native WPF) | đạt/sát ngưỡng |

## 3. Phán quyết (đề xuất, CHƯA XÁC NHẬN)

AOT **khả thi** cho shell: không có rào cản kỹ thuật cứng ngoài hai nhóm đã định danh (COM `dynamic` -> WP-33; cửa sổ phụ WPF -> WP-35). Lợi ích thực: -0,47..-0,54 s
ảnh đầu so với R2R trên máy bận, -28 % working set, không cần cài .NET runtime. Chi phí: 2 gói code (WP-33, WP-34) + cửa sổ phụ (WP-35, lớn nhất) + CI (WP-36).
Theo quy tắc kế hoạch (NO-GO nếu G0.1 hoặc G0.2 hụt) G0.1 hụt về chữ; nhưng G0.1 là mục tiêu ước lượng ban đầu cho viewer tối thiểu, số đo bản đầy đủ đã vượt xa
mọi bản WPF và G0.3 đạt, nên đề xuất: **đi tiếp WP-33 -> WP-34 -> WP-36 với AOT là mục tiêu**, đồng thời bảo toàn bản hybrid R2R (WPF cầu) làm lối thoát cho tới khi
WP-35 xong. Quyết định cuối thuộc người dùng (xem NOWPF-UNCONFIRMED-DECISIONS: WP32-GO).

## 4. Chưa làm / việc kế

- A/B R2R vs AOT lúc máy rảnh (< 15 % CPU) để chốt G0.1/NW-5 (350 ms); pan G0.6 trong tiến trình AOT; kiểm runtime nhánh "cầu vắng" của `SecondaryWindowPorts`.
- WP-33 (bỏ `dynamic`), WP-34 (IsAotCompatible + dùng publish AOT làm cổng, giữ `EventSourceSupport`), WP-35, WP-36 (bỏ FrameworkReference WindowsDesktop khỏi bản AOT,
  CI, `vswhere` trong PATH). Chi tiết trong perf doc mục 4.
