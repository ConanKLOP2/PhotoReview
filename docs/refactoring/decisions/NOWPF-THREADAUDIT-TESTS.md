---
id: NOWPF-THREADAUDIT-TESTS
order: 1000
summary: |-
  Rà soát thread-id/thời gian test: sửa 5, luật mới.
---

# NOWPF-THREADAUDIT-TESTS - rà soát danh tính luồng và thời gian trong test (2026-10-12)

Bối cảnh: CI đỏ ngẫu nhiên ba lần trong 24 giờ cùng một lớp lỗi - (a) so `ManagedThreadId` của luồng đã chết (id bị tái dùng:
`WicGeneratedComTests`, Imaging `DecodeOriginal...`), (b) lấy luồng thread-pool làm "luồng gọi" rồi khẳng định việc KHÔNG chạy ở
luồng đó (đã sửa ở `PreloadSchedulerMutationTests`), (c) đo thời gian thực/CPU/ưu tiên luồng dưới tải (`GpuKeepAliveTests`, một
test đã sửa bằng seam đồng hồ ở #463). Quy tắc rút ra nằm ở [TESTING.md](../../TESTING.md) mục "Thread identity and timing under load".

## 1. Phương pháp

- Quét `tests/` (Core, Imaging, App, Shell, Shell.Integration, Integration, Platform, TestSupport): `ManagedThreadId`,
  `CurrentManagedThreadId`, `Thread.CurrentThread`, `IsThreadPoolThread`, `Stopwatch`/`Elapsed`/`GetThreadTimes`/`Priority`, và các
  tên/chú thích "off the caller / same thread".
- Tiêu chí AN TOÀN: id (hoặc đối tượng) được so với một luồng SỐNG suốt phép so sánh (UI/dispatcher thread chuyên dụng của test, luồng
  gọi `new Thread` đang chặn, worker không bao giờ thoát) hoặc so trong thân đồng bộ không có `await`. RỦI RO: luồng pool làm "luồng
  gọi" qua `await`, so với luồng đã chết, hoặc cửa sổ/khoảng thời gian thực cho một khẳng định "có đủ N sự kiện".
- Tái hiện: vòng lặp 300-400 lần trong một tiến trình (xUnit, Release) với 12 luồng đốt CPU nền; trước và sau khi sửa.

## 2. Bảng phân loại (70 lần dùng ManagedThreadId / 22 file + test thời gian)

| Test / vị trí | Cơ chế | Phân loại | Lý do / xử lý |
|---|---|---|---|
| `JournalDurabilityTests.SafeMode_PreparedAndMutationRunOffCallerThread_FastModeKeepsPreparedInline` | so id của luồng test (pool) với luồng ghi sự kiện sau `await` | **RỦI RO - đã sửa** | luồng pool gọi bị trả về pool ở `await`, rồi tự chạy việc: 299/300 đỏ dưới tải. Nay: `new Thread` chặn `GetResult()`, so `Thread` (NotSame/Same) qua `FsEvent.Thread` |
| `GpuKeepAliveTests.ReactivationAfterTheDeadline_ResumesOnTheSameDevice` | cửa sổ 60 ms thời gian thực, cần >= 3 lần chạm | **RỦI RO - đã sửa** | 29/30 đỏ dưới tải; nay đồng hồ giả + chờ trạng thái ngủ (`IdleWaits`/`IsBlocked`) |
| `GpuKeepAliveTests.NonPositiveInterval_...` | đếm số lần chạm trong 300 ms thời gian thực (cận dưới 2) | **RỦI RO - đã sửa** | 2/30 đỏ; nay cận TRÊN theo thời gian trôi (tải chỉ làm chậm hơn) + chờ sống >= 40 lần |
| `GpuKeepAliveTests.ActivationNeverShortensAnEarlierDeadline` | hạn chót 1500 ms thời gian thực, chờ 3 lần chạm | **RỦI RO - đã sửa** | 1/30 đỏ; đồng hồ giả |
| `GpuKeepAliveTests.Activation_...ThenStopsAtTheDeadline` | cửa sổ 150 ms thời gian thực | RỦI RO (lập luận; 30/30 xanh) - đã sửa | cùng lớp với Reactivation; đồng hồ giả, quan sát trạng thái |
| `GpuKeepAliveTests.IntervalIsRespected_NotABusyLoop`, `HugeInterval_...` | đếm chạm trong cửa sổ thực | RỦI RO (lập luận) - đã sửa | Interval: cận trên theo thời gian trôi; Huge: chờ có giới hạn (kẹp 1 s so với 60 s) |
| `GpuKeepAliveTests` (FakeWork) | `OpenThreadId`/`TouchThreads`/`DisposeThreadId` kiểu int | AN TOÀN, siết lại | đổi sang đối tượng `Thread` (không phụ thuộc id tái dùng) |
| `ExplorerOrderServiceSeamTests.QueriesAreSerializedOnOneStaThread` | `HashSet<int>` id luồng, `Assert.Single` | AN TOÀN, siết lại | id tái dùng có thể che luồng thứ hai: đổi sang `HashSet<Thread>` |
| `ImmediateUiSchedulerTests.Post_RunsTheActionInline...` | so id luồng hiện tại, thân đồng bộ | AN TOÀN | không `await`, cùng luồng sống |
| `FileLogTimingAndHandleTests.Flush_DrainedEventDisposed...` | id luồng hiện tại trong `FirstChanceException` | AN TOÀN | thân đồng bộ; 40/40 xanh dưới tải; ngưỡng `< 1000 ms` rộng so với 0 ms |
| `FileLogTimingAndHandleTests.Shutdown_StuckWriter...` | `InRange(1500, 3900)` ms | AN TOÀN (cận rộng) | ngân sách chung ~2 s, biên 1,9 s; 40/40 xanh dưới tải |
| `RecoveryRecycleGroupTests.Retry_GroupRecycle_RunsTheBinCallsOffTheCallerThread` | luồng gọi `new Thread` chặn | AN TOÀN | id luồng gọi sống và chặn trong khi việc chạy |
| `RecoveryRetryServiceTests.RetryMoveOrCopyAsync_RunsMutationOffCallerThread` | luồng gọi riêng + SynchronizationContext | AN TOÀN | hai id lấy khi cả hai luồng còn sống |
| `UndoServiceGroupTests` (RunOnDedicatedThread, 3 chỗ) | luồng gọi `new Thread` chặn | AN TOÀN | như trên |
| `NavigationStatWorkerTests` (2 test) | worker chuyên dụng (không thoát) vs luồng test/continuation | AN TOÀN | worker sống suốt tiến trình |
| `SourceBytesCacheTests.GetOrRead_ReadsOnCallingThread` | luồng `new Thread`, so id với `LastReadManagedThreadId` | AN TOÀN | cùng một luồng, đọc trong khi luồng sống |
| `SourceBytesCacheRobustnessTests.LateReaderAfterPublish...` | id luồng LongRunning đang đỗ vs luồng thắng | AN TOÀN | luồng "muộn" sống đến khi `winnerDone` |
| `PreloadKickOffCallerTests` (3) | id luồng test giữa `TrackCaller` và `StopTracking` | AN TOÀN | khoảng đồng bộ, không `await` |
| `PreviewImageServiceMutationTests`, `PreloadSchedulerMutationTests`, `ViewerDecodePriorityTests` | đối tượng `Thread`/cờ pool/priority | AN TOÀN | đã so đối tượng Thread (#463/#464) |
| `ImagePresenterTests.StatOffUiThread` | id luồng test; mọi stat chạy trên `NavigationStatWorker` riêng | AN TOÀN (lập luận) | worker được tạo sau khi lấy id và sống suốt; chưa đổi vì không có đường stat nào khác ngoài `StatNow` |
| `AppRound3GapTests`, `DispatcherUiSchedulerTests`, `WpfPresentationSinkTests` | so id với `Dispatcher` thread của test | AN TOÀN | thread sống đến `Dispose`; so trước khi tắt |
| `Win32UiDispatcherTests` (7), `MessageLoopFrameClockTests` | so với `UiThread.ManagedThreadId` | AN TOÀN | UI thread chuyên dụng sống đến `await using` |
| `ReviewCatalogThreadGuardTests` | luồng khác với luồng đã gắn | AN TOÀN | chủ sở hữu còn sống khi luồng thứ hai chạy |
| `FileHashServiceTests` | `Assert.Null(LastReadManagedThreadId)` | AN TOÀN | không so id |
| `ImagePresenterTests.CompareBusyAndDimensions`, `ViewerDecodePriorityTests` | ghi `Thread.Priority` do code dưới test đặt | AN TOÀN | giá trị, không phải thời gian |
| `FixturePerfTest.FixtureGenerationCpuBudget` (Slow) | CPU time của luồng gọi <= 10 s | AN TOÀN (cận rộng) | CPU time, đo 1-2 s |
| `ShellIdleCpuReportTests`, `KineticPanFrameMeasurementTests`, `SessionStoreSweepPerfTests` | CPU/ưu tiên/thời gian thực | Không chạy trong CI | `Category=Manual` hoặc `Native` (lọc `!Manual&!Native`) |
| Fuzz/hang-guard: `ParserMutationFuzzTests` (worst < 500 ms), `RawContainerFuzzTests` (p95), `PreviewCacheFileMutationTests`, `PreloadSafetyTests`, `SessionStoreTests`... | ngân sách thời gian chống treo | AN TOÀN (cận rộng, ghi nhận) | ParserFuzz 60/60 xanh dưới tải (2,4 triệu lần parse); ngân sách >= 1000 lần thường lệ; không đổi |
| Test dùng `Stopwatch` làm đồng hồ giả (`BurstPreloadTests`, `KeepWarmTests`, `FrameStatsTests`, `Animator`...) | `Stopwatch.Frequency` làm đơn vị | AN TOÀN | đồng hồ do test điều khiển |

## 3. Bằng chứng

| Test | Trước (dưới 12 luồng đốt CPU) | Sau |
|---|---|---|
| `JournalDurabilityTests.SafeMode_...` | 299/300 đỏ (`Assert.NotEqual`) | 400/400 xanh |
| `GpuKeepAliveTests` x7 | Reactivation 29/30, NonPositive 2/30, NeverShortens 1/30 đỏ (30 lần) | 300/300 xanh mỗi test |
| Mutation (src tạm, đã hoàn nguyên) | - | Journal: chạy `Move` ngay trên luồng gọi -> đỏ (NotSame). GpuKeepAlive: bỏ hạn chót -> Activation/Reactivation đỏ; cho phép rút ngắn hạn chót -> NeverShortens đỏ; bỏ ngủ khoảng -> Interval/NonPositive đỏ; kẹp trên 1000 -> Huge đỏ; kẹp dưới 0 -> NonPositive đỏ |

Ghi chú: đột biến "bỏ kẹp dưới" (`Math.Min(intervalMs, 1000)`: khoảng âm không kẹp về 1) KHÔNG làm NonPositive đỏ - với khoảng âm
`SetWaitableTimer` nhận mốc tuyệt đối và vẫn chạm ~1 lần/ms trên máy này (chưa tìm hiểu sâu; hành vi quan sát được tương đương kẹp 1 ms).
Đột biến kẹp dưới 0 (khoảng 0 = hẹn giờ tương đối 0) thì đỏ (62 lần chạm / 52 ms).

## 4. Luật kiến trúc

`TestQualityRulesTests.TestsDoNotCompareManagedThreadIds` (rule `threadid`): cấm `Assert.Equal/NotEqual(...)` có tham số là
`...ThreadId` hoặc biến `caller|worker|pool|mutation|continuation|owner|dispatcher|bound...Thread`, ngoài allowlist co dần (11 file
AN TOÀN ở bảng trên). Không cấm `Assert.All(..., id => Assert.NotEqual(callerThread, id))` ngoài tên biến đã liệt kê (regex dựa
tên): giới hạn đã biết, đổi lấy không dương tính giả ở bộ đếm kiểu `perThread`/`OutstandingOnThisThread` (đã loại).

## 5. Ngân sách tài liệu

Dòng của fragment này làm `OPEN-DECISIONS.md` vượt ~130 byte quanh trần 48 KB (48 992 / 49 152 byte trên master); trần đã được nâng gấp đôi riêng ở #473 nên PR này không đổi `tools/docs-budget.ps1` nữa.
