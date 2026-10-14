---
id: NOWPF-PERF-GATE3
order: 1340
summary: |-
  Gate hiệu năng đợt 3 đo lại trên máy yên (2026-10-14, R2R và AOT cùng commit, 10-12 vòng xen kẽ): G1 cửa sổ thấy được AOT 112 ms ĐẠT / R2R 321 ms HỤT; NW-5 AOT ảnh đầu 206 ms ĐẠT; G3 Settings lần sau 48 ms ĐẠT (nay là hộp thoại Win32, số 289 ms cũ là cầu WPF); G2 key->frame P95 AOT 8,8 -> 4,8 ms (P50 4,4) vẫn HỤT <= 3 ms (sàn = một khung 2,7-3,7 ms). Hai tối ưu: giữ GPU ấm ngay sau ảnh đầu (phím đầu 36-507 -> 5-7 ms) và khung rỗi vẽ ngay không chờ lưới vblank. Cú đứng 300-500 ms là của desktop ẩn (0/20 trên desktop thật). CHƯA XÁC NHẬN.
---

# NOWPF-PERF-GATE3 - đo lại gate đợt 3 + hai tối ưu nhỏ (2026-10-14)

Số đo, bảng phân rã, nhiễu và hạn chế: [perf/2026-10-14-gate3-measure.md](../perf/2026-10-14-gate3-measure.md).
Nền: [NOWPF-WP21STALL](NOWPF-WP21STALL-GPU-KEEPWARM.md), [NOWPF-AOT-LOCAL-TRIAL](NOWPF-AOT-LOCAL-TRIAL.md), [NOWPF-WP29](NOWPF-WP29-PERF-PROBE.md).

## 1. Kết quả theo gate (máy yên 1-22 % CPU, một máy)

| Gate | Mục tiêu | Trước | Sau | |
|---|---|---|---|---|
| G1 cửa sổ thấy được AOT | <= 150 ms | 112-114 | 112 | ĐẠT |
| G1 cửa sổ thấy được R2R | <= 150 ms | 321-325 | 321 | HỤT (runtime 76 + JSON 107 ms) |
| NW-5 ảnh đầu AOT | <= 350 ms | 208 | 204-206 | ĐẠT |
| G2 key->frame P95 AOT (R2R) | <= 3 ms | 8,8 (9,6), phím đầu tới 507 | 4,8 (6,4), phím đầu 5-7 (9-11) | HỤT |
| G3 Settings lần sau | <= 150 ms | 289 (cầu WPF cũ) | 48 | ĐẠT |

## 2. Hành vi mới (hai commit, không đổi hành vi nhìn thấy)

- `perf(shell-gpu)`: `KeepWarmController.WarmAtLaunch()` kéo dài giữ ấm GPU (mặc định 5 s, cùng công tắc `PHOTOREVIEW_GPU_KEEPWARM=0`) ngay khi ảnh đầu đã Present, không chờ tương tác.
  Chỉ khi cửa sổ được activate (không smoke/test), không WARP. Chi phí ~4 % engine GPU + 0,6 % một lõi trong 5 s.
- `perf(shell-frame)`: `TimerFrameClock` vẽ ngay khi không có khung nào trong một chu kỳ vừa qua (khung đầu sau khi rỗi), thay vì chờ điểm lưới vblank; hoạt ảnh liên tục vẫn neo lưới.
  Flip-model Present vẫn lấy ở vblank kế nên không tearing; key->frame bớt ~2,5 ms.

## 3. CHƯA XÁC NHẬN (cần chủ dự án)

| Điểm | Phương án | Ưu | Nhược |
|---|---|---|---|
| Giữ ấm lúc khởi động | (a) BẬT 5 s như đã làm (khuyến nghị) | Phím đầu hết bị chặn khi bấm trong 5 s đầu; không đổi hành vi | Phím đầu SAU 5 s không tương tác vẫn có thể bị chặn 25-50 % lần |
| | (b) 10-15 s hoặc liên tục khi active (WP21-STALL b) | Hết chặn hẳn khi đang dùng | Tiêu thụ nền (pin, nhiệt) |
| | (c) bỏ commit này | Không tiêu thụ thêm | Phím đầu lại bị chặn 36-507 ms ở 8/10 lượt |
| Khung rỗi vẽ ngay | (a) giữ như đã làm (khuyến nghị) | key->frame 7 -> 4,5 ms, hình hiện sớm hơn hoặc bằng | Đổi cadence khung đơn lẻ (không tearing; test đủ) |
| | (b) hoàn tác (`git revert d0258b2b`) | Cadence cũ | key->frame cao hơn ~2,5 ms |
| G1 R2R <= 150 ms | (a) chấp nhận: gate chỉ tính cho bản AOT (khuyến nghị nếu AOT thành gói mặc định) | Khớp số đo | R2R còn 321 ms |
| | (b) phát hành AOT làm mặc định | G1 ~115 ms, ảnh đầu ~207 ms, WS -21 % | Cần chốt NOWPF-AOT-LOCAL-TRIAL (LƯU Settings, RAW, cập nhật chưa kiểm) |
| | (c) tối ưu R2R (JSON khởi động, thứ tự DI/config) | -25 đến -90 ms | Diff lớn/đụng thứ tự khởi tạo: không làm khi chưa chọn (b) |
| G2 <= 3 ms | (a) hạ mục tiêu về <= 5 ms P95 (khuyến nghị, số đo 4,8) | Khớp sàn một khung 2,7-3,7 ms | Mục tiêu thẻ gốc đổi |
| | (b) giữ <= 3 ms, giảm chi phí khung (Linear thay HQC khi chỉ đổi ảnh, gộp 2 khung/phím) | Có thể xuống ~3,5-4 ms | Đổi chất lượng ảnh/luồng HUD, vẫn khó <= 3 |

Hoàn tác: `git revert` từng commit; không ảnh hưởng dữ liệu hay cấu hình.
