# PhotoReview — Plan tái cấu trúc (B + C3) — ARCHIVED

**Ngày lập:** 2026-09-16 · **Baseline:** `master` @ `86282cd`  
**Decision:** **B + C3** (chốt 2026-09-16)
- **B:** giữ WPF, tách thành nhiều project theo lớp + MVVM
- **C3:** giữ WPF, thay bộ giải mã ảnh (decoder) sau interface `IImageDecoder`

**Status:** DONE — xem [`task-log-2026-09-21.md`](task-log-2026-09-21.md) và [`../../ACTIVE-TASKS.md`](../../ACTIVE-TASKS.md)

---

## 1. Mục tiêu sơ lược

1. Tách `MainWindow.xaml.cs` thành ViewModel + coordinator (1086 → ≤250 dòng)
2. Chia project theo lớp: Core / Imaging / Platform.Windows / App / Benchmarking (dependency một chiều, test kiến trúc)
3. Thay 69 test "source-presence" bằng test hành vi (xUnit thống nhất)
4. **(C3)** `IImageDecoder` interface; backend nhanh hơn (WIC direct hoặc libjpeg-turbo); fallback luôn là WPF `BitmapImage`
5. Chất lượng hiển thị: EXIF orientation, `BitmapScalingMode=HighQuality`
6. Tối ưu: đọc đĩa ít nhất, dùng nhiều RAM, chất lượng ảnh tốt nhất
7. Giữ nguyên bất biến an toàn (mục 4.6), có CI

**Không phải:** tính năng người dùng mới, đổi UX (ngoài orientation/scale), đa nền tảng, đổi UI framework

---

## 3. So sánh hướng (tóm tắt quyết định B + C3)

| Tiêu chí | B MVVM + layering | **C3 decoder swap** | Khác (A, C1, C2, C4) |
|---|---|---|---|
| Tái sử dụng code | ~90% | ~95% | <90% hoặc 0% |
| Công sức | 8–12 agent-days | +5–9 days | 15–40+ days |
| Rủi ro hồi quy | Trung bình | Thấp–TB | Cao–Rất cao |
| Decode speed | Như cũ | ✓ Cải thiện (direct decode) | WinUI→D3D11 tiềm năng, C4→tối đa nhưng 40+ days |
| Chất lượng | Chỉnh `BitmapScalingMode` | ✓ ICC + EXIF orientation | Toàn quyền (C4) |

**Hướng không chọn:** A (chia thư mục chỉ), C1 (WinUI 3), C2 (Avalonia), C4 (viết lại C++). Lý do ở plan gốc mục 3.

---

## 4.6 Bất biến bắt buộc giữ

| ID | Bất biến | Test |
|----|----------|------|
| INV-1 | Cache key = path/length/mtime/mode/width (**+ backend + orientation** sau C3). Không present pixel cũ khi source đổi | `ImageCacheKeyTests`, `PreviewImageServiceTests` |
| INV-2 | Clear/evict tăng epoch. In-flight hoặc persist cũ không làm entry sống lại | `PreviewImageServiceTests`, `DiskCacheStoreTests` |
| INV-3 | Advance đúng một lần, trước thao tác filesystem. Không `ShowImage` sau khi action xong | T14 → T46 |
| INV-4 | Chỉ một file action chạy tại một thời điểm | T14 → T42 |
| INV-5 | Action hoàn tất sau khi đã đổi folder: không đăng ký undo, không sửa catalog | T14 → T46 |
| INV-6 | Delete → Recycle Bin. Journal Prepared → Committed/Failed. Reconcile khởi động | `OperationJournalTests`, smoke |
| INV-7 | Bỏ qua Explorer snapshot nếu catalog đã tương tác hoặc tập file đã đổi | T14 → T44 |
| INV-8 | Handle `ReadWrite \| Delete`, `SequentialScan`, đóng sau khi đọc (mọi backend) | `FileActionConcurrencyTests`, T80 |
| INV-9 | Mở file → chờ snapshot trước frame. Mở folder → present fallback trước | T14 → T44 |
| INV-10 | Logging tắt mặc định, không tạo file | `AppLogTests` |
| INV-11 | Config hỏng → backup + reset. Config khóa → dùng default trong RAM | `AppSettingsTests` |
| INV-12 | **(C3)** Backend lỗi → fallback `Wpf`. Pixel backend khác không lẫn cache | T84, T87 |

---

## 5. Lộ trình (Waves)

| Wave | Nội dung | Task |
|------|----------|------|
| W0 | Baseline, CI, build tools, git process | T00, T02–T05 |
| **WD** | **Perf diagnosis trước W1** | D00–D13 |
| W1 | Gộp test, khóa INV trên code hiện tại | T10–T14 |
| W2 | Tách Core | T20–T26 |
| W3 | Tách Imaging + Platform, `IImageDecoder`, bỏ static | T30–T35 |
| **WC3** | **C3 parallel sau T31c** | T80–T88 |
| W4 | Chia nhỏ `MainWindow` (MVVM) | T40–T47 |
| W5 | Tách Benchmark, bỏ WinForms | T50–T53 |
| W6 | Hiệu năng R-1…R-7, tích hợp decoder chọn | T60–T66 |
| W7 | ADR, docs, GUI acceptance, release | T71–T74 |

```
W0 → WD (D00…D13) → W1 → W2 → W3 ─┬─► W4 → W5 → W6 → W7
                      └─► WC3 (T80…T86) ─► T87 ──► W6
```

---

## Optim suggestions (R-1..R-7)

- R-1: Session save debounce, worker thread
- R-2: Journal reverse-read at startup or compaction
- R-3: Bỏ `FileInfo` UI thread, dùng catalog metadata
- R-4: `SourceBytesCache` (compressed bytes, read once) + `RamBudgetPolicy`; off by default until T66
- R-5: Benchmark separate from app startup
- R-6: `CatalogEntry` already has length/mtime/dimension
- R-7: **C3**: `WicDirect` / `TurboJpeg` decoder

---

**Full history and decisions (Q1–Q14):** see plan at commit ~`86282cd` or git history.  
**Implementation log:** [`task-log-2026-09-21.md`](task-log-2026-09-21.md)  
**Current status:** [`../../ACTIVE-TASKS.md`](../../ACTIVE-TASKS.md)
