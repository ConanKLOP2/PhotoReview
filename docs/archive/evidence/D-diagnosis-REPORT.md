# D12 — Báo cáo chẩn đoán hiệu năng

Ngày: 2026-09-19  
Branch: `codex/w6-performance`  
Phạm vi: D07 reduced, các biến thể worker/flag và một cold-os cell.

## Phạm vi và độ tin cậy

Các run chính đã hoàn tất và đều ghi `session.json`, `errors=[]`, `idleTimeouts=0`:

- S1/F1/Preview: warm, cold-app, cold-diskcache; 3 lần mỗi ô.
- S2/F1/Preview: warm, cold-app, cold-diskcache; 1 lần mỗi ô trong batch hoàn chỉnh.
- S3/F1/Preview: warm, cold-app, cold-diskcache; 1 lần mỗi ô.
- S6/F1/Preview: warm, cold-app, cold-diskcache; 1 lần mỗi ô.
- S9/F4/Preview: warm, cold-app, cold-diskcache; 1 lần mỗi ô.
- S3/F1/Preview/warm: worker 0/2/4/8/12; 1 run chính mỗi ô.
- S3/F1/Preview/warm: baseline, `PREREAD=1`, `DISABLE_DISKCACHE=1`, và cả hai flag; 1 run chính mỗi ô.
- S3/F1/Preview/cold-os: 1 cell sau reboot.

Các run một lần là bằng chứng thăm dò, không dùng để kết luận thống kê cuối. Raw output nằm trong `work/diag/runs/` và không commit.

## Kết quả chính

| Nhóm | Kết quả quan sát |
|---|---|
| S1 open folder | first P50 khoảng 220–253 ms; final P50 khoảng 419–493 ms; decode chiếm khoảng 41–43% ở nhóm chậm |
| S2 next slow | warm final P50 khoảng 54,8 ms; hit rate khoảng 42,6%; decode và thumbnail là hai thành phần lớn |
| S3 next burst | 200 Next/run; hit rate khoảng 98,6%; worker 0–12 không tạo khác biệt rõ; các run flag có final P50 khoảng 6,9–7,4 ms |
| S6 zoom | working set khoảng 250 MB; không đủ RamHit để kết luận preload |
| S9 large/F4 | final P50 khoảng 386–395 ms; decode chiếm khoảng 52–58%; working set khoảng 852–870 MB |
| Cold-os S3 | pass; 200 Next; presented 143; hits 141; misses 43; peak working set khoảng 671 MB |

## Quy tắc R-* đã kích hoạt

- **R-DEC:** có bằng chứng ở S1, S2 và S9; decode là nút thắt đáng kể.
- **R-UI:** có ở S3 RamHit; `t_render` chiếm khoảng 80% trở lên trong nhóm điều hướng nhanh. Cần D08/PresentMon nếu muốn tách render thật khỏi thời gian harness.
- **R-THREAD:** DispatcherLongOp >16 ms xuất hiện trong các nhóm, nhưng chưa đủ để kết luận nguyên nhân sản phẩm.
- **R-PRE:** S3 có tỷ lệ RamHit cao và RamHit P95 thấp; chưa cho thấy thiếu coverage nghiêm trọng.
- **R-CONT:** worker matrix chưa cho thấy xu hướng rõ; mỗi ô chỉ một run.
- **R-GC:** các run hiện tại không cho thấy GC là nút thắt chính.
- **R-IO/R-DISK/R-FOLDER:** chưa đủ bằng chứng vì chưa bật Procmon/source-open instrumentation phù hợp hoặc chưa có cặp disk-hit/source-miss cần thiết.

## Trạng thái giả thuyết H1–H15

- **Có bằng chứng một phần:** H1, H4, H8, H11, H13.
- **Chưa rõ:** H2, H3, H5, H6, H7, H9, H10, H12, H14, H15.
- **Không được xác nhận bởi D07 hiện tại:** H4 ở mức worker 0–12 không cho thấy thay đổi rõ; H11 không nổi bật trong các run đã đo.

Phân loại này không thay thế D08/D09/D02; các giả thuyết cần ETW, counters hoặc Procmon vẫn để `CHƯA RÕ`.

## Đề xuất thứ tự

1. Giữ `RamBudgetPolicy` và worker mặc định hiện tại; chưa có bằng chứng để tăng worker hoặc bật SourceBytesCache mặc định.
2. Ưu tiên một đợt D08 nhỏ cho S3/S6 nếu cần phân biệt render thật với thời gian routed-event harness.
3. Giữ Q5: SourceBytesCache tắt mặc định cho đến khi T66 có source-open và memory evidence.
4. Giữ Q8: tích hợp setting/fallback trước, WPF vẫn là default cho đến khi GUI acceptance đạt.
5. T52 settings binding đã hoàn tất; xử lý compatibility marker là task riêng.

## Mục tiêu hiệu năng đề xuất để người dùng xác nhận

- S1 final P95: mục tiêu theo dõi ≤ 600 ms trên F1.
- S2 final P95: mục tiêu theo dõi ≤ 400 ms trong điều kiện warm/cold-app.
- S3 RamHit final P95: ≤ 50 ms.
- S6: cần đo frame time thật trước khi đặt ngưỡng render.
- S9: theo dõi riêng working set và final P95; chưa đặt ngưỡng cứng trước khi có D09/F5.

