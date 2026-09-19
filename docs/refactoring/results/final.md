# T66 — Benchmark cuối sau T87

Ngày chạy: 2026-09-19 · branch `codex/w6-performance` · commit decoder `ec120fd`.

## Phạm vi và độ tin cậy

Baseline sau T87 được chạy bằng `tools/diag/run-matrix.ps1`, fixture F1 (58 ảnh JPEG 24 MP) và F4 (519 ảnh JPEG), mode Preview, điều kiện warm. S1/S3/S6/S9 chạy cell mới sau T87; S2 chạy warm-up và một run đo đầy đủ 100 lần Next vì mỗi run mất khoảng 2,5–3 phút theo interval 1,5 giây. Các run D07 trước đó có repeat=3 được dùng để đối chiếu độ ổn định. T65 chưa được đưa vào A/B vì `SourceBytesCache` vẫn chưa triển khai; WPF là backend mặc định theo Q8.

## Kết quả baseline sau T87

| Scenario | Fixture | Count | First P50/P95 (ms) | Final P50/P95 (ms) | Hit rate | Peak WS | Ghi chú |
|---|---|---:|---:|---:|---:|---:|---|
| S1 open-folder | F1 | 1 | 182.0 / 182.0 | 346.9 / 346.9 | 0% | 221 MB | N<20, chỉ thao tác mở folder |
| S2 next-slow | F1 | 101 | 17.8 / 153.2 | 52.5 / 335.3 | 42.6% | 694 MB | 100/100 key, 0 lỗi |
| S3 next-burst | F1 | 201 | 6.9 / 8.5 | 6.9 / 8.5 | 98.6% | 660 MB | 200/200 key, 0 lỗi; 57 incomplete do burst/presentation overlap |
| S6 zoom | F1 | 1 | 191.8 / 191.8 | 349.3 / 349.3 | 0% | 252 MB | N<20, chỉ thao tác zoom |
| S9 large | F1 | 201 | 8.7 / 113.0 | 8.7 / 265.2 | 71.1% | 701 MB | 200/200 key, 0 lỗi |
| S9 large | F4 | 201 | 159.7 / 242.7 | 383.5 / 498.9 | 0% | 856 MB | 200/200 key, 0 lỗi; 80 incomplete |

Ở các nhóm chậm, S2/S9 chủ yếu nằm ở decode và thumbnail: S2 lần lượt khoảng 50.4%/42.1%; S9 F1 khoảng 57.5%/39.7%; S9 F4 khoảng 52.9%/45.6%. S3 burst chuyển tỷ trọng sang render (81.7%), phù hợp với áp lực trình diễn khi key đến nhanh.

## So sánh với D07

- S2 warm giữ hit rate khoảng 42.6% và final P50 khoảng 52–55 ms, không có hồi quy rõ sau T87.
- S3 giữ hit rate khoảng 98.6% và final P50 khoảng 6.9–7.6 ms; worker count trước đó không cho thấy khác biệt rõ.
- S9 F1 sau T87 có final P50 8.7 ms, tốt hơn các run F1 trước đó khoảng 386–395 ms khi đo profile khác; so sánh tuyệt đối cần lưu ý workload/điều kiện cache khác nhau.
- S9 F4 vẫn là trường hợp nặng: final P95 498.9 ms, peak WS khoảng 856 MB; chưa có cơ sở bật SourceBytesCache mặc định.

## Quyết định đầu vào T65

T66 cung cấp baseline source-open/RAM/decode sau T87 nhưng chưa cho thấy lợi ích đủ chắc chắn để bật cache byte mặc định. T65 tiếp tục ở trạng thái feature flag, mặc định tắt; chỉ A/B khi implementation và test lifecycle hoàn tất.

Các raw run nằm dưới `work/diag/runs/t66-t87-20260919/`; đây là dữ liệu local, không commit.
