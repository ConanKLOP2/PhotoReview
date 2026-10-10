---
id: NOWPF-WP11
order: 262
summary: |-
  WP-11 (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): chuyển các lời gọi JsonSerializer trong PhotoReview.Core sang JsonSerializerContext
  sinh mã nguồn (session, journal đọc/ghi); giữ nguyên byte và đặc tả tương thích ngược (có test khoá định dạng). Core không còn
  IL2026/IL3050 khi bật IsAotCompatible (kiểm bằng cờ dòng lệnh, chưa đặt trong csproj - việc đó thuộc WP-33). Phần App,
  Benchmarking, PerfAnalysis để nguyên vì ngoài vùng của WP-11.
---

# NOWPF-WP11-JSONSRCGEN - JSON source generation cho serializer trong Core (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 1-3), thẻ WP-11 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).

## 1. Đã chuyển

| Nơi dùng | Trước | Sau |
|---|---|---|
| `Core/Session/SessionStore.cs` (đọc + ghi session) | `JsonSerializer` reflection, `WriteIndented` | `SessionJsonContext` (`Session/SessionJsonContext.cs`), cùng option |
| `Core/FileActions/OperationJournal.cs` (ghi dòng journal) | `JsonSerializer.Serialize(entry)` mặc định | `JournalJsonContext` (`FileActions/JournalJsonContext.cs`) |
| `Core/FileActions/JournalLineParser.cs` (đọc dòng journal) | `JsonSerializerOptions` với hai converter ghi nhận generic | `JournalLineJsonContext` liệt kê hai converter không generic `TypeRecordingConverter`, `StateRecordingConverter` (lồng trong `JournalLineParser`, có constructor không tham số) |

## 2. Đã có sẵn, không đổi

- `Core/Settings/AppSettingsJsonContext.cs` (config.json, từ P-1) dùng cho `SettingsStore` (Load, Save, Parse, `TrySalvage`). Không có cảnh báo AOT.
- `LanguageCatalog`, `UpdateChecker`, `SettingsStore` (phần kiểm tra thuộc tính) chỉ dùng `JsonDocument` (DOM, không reflection): không cần sinh mã.
- Imaging và Platform không có lời gọi JSON nào (đã kiểm bằng grep `JsonSerializer|JsonNode|JsonDocument`).

## 3. Để nguyên (ngoài vùng WP-11)

| Nơi dùng | Lý do |
|---|---|
| `App/WindowPlacementService.cs` (window-placement.json) | Thuộc WP-08 (placement); thẻ WP-11 cấm chạm App |
| `App/SettingsWindow.xaml.cs`, `App/ActionProfilesWindow.xaml.cs`, `App/BenchmarkWindow.xaml.cs` | App, như trên. `SettingsWindow` dùng `UnsafeRelaxedJsonEscaping` nên cần đối chiếu byte riêng |
| `Benchmarking/BenchmarkModels.cs`, `PerfAnalysis/PerfAnalyzeReport.cs`, `PerfAnalyzeRules.cs`, `NaNAsNullDoubleConverter.cs` | Ngoài Core/Imaging/Platform; công cụ đo, không thuộc đường chạy của app |

## 4. Kiểm chứng

- `tests/PhotoReview.Core.Tests/Json/JsonFormatCompatTests.cs` (7 test) khoá byte và cách đọc của định dạng cũ: session (thụt lề, thứ tự khoá, escape ký tự không ASCII, `UpdatedUtc` dạng `Z`), dòng journal (không thụt lề, `null` được ghi, `Permanent` chỉ khi có), dòng journal cũ có `GroupMembers` đọc đúng, config.json sau Load cũ + Save vẫn có thụt lề và khoá cũ. Được chạy trước khi đổi code: 7/7 xanh trên code cũ.
- Test hiện có: `JournalLineParserTests` (đối chiếu với bộ đọc cũ), `JournalModelTests`, `JournalRecordRoundTripPropertyTests`, `SessionStore*`, `Settings*`, `LenientEnum*`: 881/881 xanh (Core.Tests, filter `Category!=Manual&Category!=Native&Category!=Slow`).
- `ContractSurfaceTests` (Architecture.Tests): 3/3 xanh; hợp đồng v1 không đổi.
- Mutation check (đổi một thứ trong context rồi chạy lại, sau đó khôi phục):
  - bỏ `WriteIndented` của session: 1 test lỗi (byte session);
  - đặt `PropertyNamingPolicy=CamelCase` cho journal ghi: 2 test lỗi (byte dòng journal, round-trip);
  - bỏ `Converters` của `JournalLineJsonContext`: hơn 10 test `JournalLineParser` lỗi (alias "Delete", trạng thái không nhận dạng);
  - bỏ `WriteIndented` của `AppSettingsJsonContext`: 1 test lỗi (config.json).
- Build: `dotnet build PhotoReview.slnx -c Release` 0 lỗi, 0 cảnh báo. Core với `-p:IsAotCompatible=true` (cờ dòng lệnh, không commit): 0 IL2026/IL3050 (trước WP-11: 12 cảnh báo tại 6 lời gọi).

## 5. Việc tiếp theo

- App và Benchmarking/PerfAnalysis (bảng mục 3) cần một gói riêng hoặc gộp vào WP-08 cho placement. Chưa quyết định ở đây.
- WP-33 bật `IsAotCompatible` cho Core trong csproj.
