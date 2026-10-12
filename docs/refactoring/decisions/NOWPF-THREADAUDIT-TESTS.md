---
id: NOWPF-THREADAUDIT-TESTS
order: 1000
summary: |-
  RÃ  soÃ¡t thread-id/thá»i gian test: sá»­a 5, luáº­t má»›i.
---

# NOWPF-THREADAUDIT-TESTS - rÃ  soÃ¡t danh tÃ­nh luá»“ng vÃ  thá»i gian trong test (2026-10-12)

Bá»‘i cáº£nh: CI Ä‘á» ngáº«u nhiÃªn ba láº§n trong 24 giá» cÃ¹ng má»™t lá»›p lá»—i - (a) so `ManagedThreadId` cá»§a luá»“ng Ä‘Ã£ cháº¿t (id bá»‹ tÃ¡i dÃ¹ng:
`WicGeneratedComTests`, Imaging `DecodeOriginal...`), (b) láº¥y luá»“ng thread-pool lÃ m "luá»“ng gá»i" rá»“i kháº³ng Ä‘á»‹nh viá»‡c KHÃ”NG cháº¡y á»Ÿ
luá»“ng Ä‘Ã³ (Ä‘Ã£ sá»­a á»Ÿ `PreloadSchedulerMutationTests`), (c) Ä‘o thá»i gian thá»±c/CPU/Æ°u tiÃªn luá»“ng dÆ°á»›i táº£i (`GpuKeepAliveTests`, má»™t
test Ä‘Ã£ sá»­a báº±ng seam Ä‘á»“ng há»“ á»Ÿ #463). Quy táº¯c rÃºt ra náº±m á»Ÿ [TESTING.md](../../TESTING.md) má»¥c "Thread identity and timing under load".

## 1. PhÆ°Æ¡ng phÃ¡p

- QuÃ©t `tests/` (Core, Imaging, App, Shell, Shell.Integration, Integration, Platform, TestSupport): `ManagedThreadId`,
  `CurrentManagedThreadId`, `Thread.CurrentThread`, `IsThreadPoolThread`, `Stopwatch`/`Elapsed`/`GetThreadTimes`/`Priority`, vÃ  cÃ¡c
  tÃªn/chÃº thÃ­ch "off the caller / same thread".
- TiÃªu chÃ­ AN TOÃ€N: id (hoáº·c Ä‘á»‘i tÆ°á»£ng) Ä‘Æ°á»£c so vá»›i má»™t luá»“ng Sá»NG suá»‘t phÃ©p so sÃ¡nh (UI/dispatcher thread chuyÃªn dá»¥ng cá»§a test, luá»“ng
  gá»i `new Thread` Ä‘ang cháº·n, worker khÃ´ng bao giá» thoÃ¡t) hoáº·c so trong thÃ¢n Ä‘á»“ng bá»™ khÃ´ng cÃ³ `await`. Rá»¦I RO: luá»“ng pool lÃ m "luá»“ng
  gá»i" qua `await`, so vá»›i luá»“ng Ä‘Ã£ cháº¿t, hoáº·c cá»­a sá»•/khoáº£ng thá»i gian thá»±c cho má»™t kháº³ng Ä‘á»‹nh "cÃ³ Ä‘á»§ N sá»± kiá»‡n".
- TÃ¡i hiá»‡n: vÃ²ng láº·p 300-400 láº§n trong má»™t tiáº¿n trÃ¬nh (xUnit, Release) vá»›i 12 luá»“ng Ä‘á»‘t CPU ná»n; trÆ°á»›c vÃ  sau khi sá»­a.

## 2. Báº£ng phÃ¢n loáº¡i (70 láº§n dÃ¹ng ManagedThreadId / 22 file + test thá»i gian)

| Test / vá»‹ trÃ­ | CÆ¡ cháº¿ | PhÃ¢n loáº¡i | LÃ½ do / xá»­ lÃ½ |
|---|---|---|---|
| `JournalDurabilityTests.SafeMode_PreparedAndMutationRunOffCallerThread_FastModeKeepsPreparedInline` | so id cá»§a luá»“ng test (pool) vá»›i luá»“ng ghi sá»± kiá»‡n sau `await` | **Rá»¦I RO - Ä‘Ã£ sá»­a** | luá»“ng pool gá»i bá»‹ tráº£ vá» pool á»Ÿ `await`, rá»“i tá»± cháº¡y viá»‡c: 299/300 Ä‘á» dÆ°á»›i táº£i. Nay: `new Thread` cháº·n `GetResult()`, so `Thread` (NotSame/Same) qua `FsEvent.Thread` |
| `GpuKeepAliveTests.ReactivationAfterTheDeadline_ResumesOnTheSameDevice` | cá»­a sá»• 60 ms thá»i gian thá»±c, cáº§n >= 3 láº§n cháº¡m | **Rá»¦I RO - Ä‘Ã£ sá»­a** | 29/30 Ä‘á» dÆ°á»›i táº£i; nay Ä‘á»“ng há»“ giáº£ + chá» tráº¡ng thÃ¡i ngá»§ (`IdleWaits`/`IsBlocked`) |
| `GpuKeepAliveTests.NonPositiveInterval_...` | Ä‘áº¿m sá»‘ láº§n cháº¡m trong 300 ms thá»i gian thá»±c (cáº­n dÆ°á»›i 2) | **Rá»¦I RO - Ä‘Ã£ sá»­a** | 2/30 Ä‘á»; nay cáº­n TRÃŠN theo thá»i gian trÃ´i (táº£i chá»‰ lÃ m cháº­m hÆ¡n) + chá» sá»‘ng >= 40 láº§n |
| `GpuKeepAliveTests.ActivationNeverShortensAnEarlierDeadline` | háº¡n chÃ³t 1500 ms thá»i gian thá»±c, chá» 3 láº§n cháº¡m | **Rá»¦I RO - Ä‘Ã£ sá»­a** | 1/30 Ä‘á»; Ä‘á»“ng há»“ giáº£ |
| `GpuKeepAliveTests.Activation_...ThenStopsAtTheDeadline` | cá»­a sá»• 150 ms thá»i gian thá»±c | Rá»¦I RO (láº­p luáº­n; 30/30 xanh) - Ä‘Ã£ sá»­a | cÃ¹ng lá»›p vá»›i Reactivation; Ä‘á»“ng há»“ giáº£, quan sÃ¡t tráº¡ng thÃ¡i |
| `GpuKeepAliveTests.IntervalIsRespected_NotABusyLoop`, `HugeInterval_...` | Ä‘áº¿m cháº¡m trong cá»­a sá»• thá»±c | Rá»¦I RO (láº­p luáº­n) - Ä‘Ã£ sá»­a | Interval: cáº­n trÃªn theo thá»i gian trÃ´i; Huge: chá» cÃ³ giá»›i háº¡n (káº¹p 1 s so vá»›i 60 s) |
| `GpuKeepAliveTests` (FakeWork) | `OpenThreadId`/`TouchThreads`/`DisposeThreadId` kiá»ƒu int | AN TOÃ€N, siáº¿t láº¡i | Ä‘á»•i sang Ä‘á»‘i tÆ°á»£ng `Thread` (khÃ´ng phá»¥ thuá»™c id tÃ¡i dÃ¹ng) |
| `ExplorerOrderServiceSeamTests.QueriesAreSerializedOnOneStaThread` | `HashSet<int>` id luá»“ng, `Assert.Single` | AN TOÃ€N, siáº¿t láº¡i | id tÃ¡i dÃ¹ng cÃ³ thá»ƒ che luá»“ng thá»© hai: Ä‘á»•i sang `HashSet<Thread>` |
| `ImmediateUiSchedulerTests.Post_RunsTheActionInline...` | so id luá»“ng hiá»‡n táº¡i, thÃ¢n Ä‘á»“ng bá»™ | AN TOÃ€N | khÃ´ng `await`, cÃ¹ng luá»“ng sá»‘ng |
| `FileLogTimingAndHandleTests.Flush_DrainedEventDisposed...` | id luá»“ng hiá»‡n táº¡i trong `FirstChanceException` | AN TOÃ€N | thÃ¢n Ä‘á»“ng bá»™; 40/40 xanh dÆ°á»›i táº£i; ngÆ°á»¡ng `< 1000 ms` rá»™ng so vá»›i 0 ms |
| `FileLogTimingAndHandleTests.Shutdown_StuckWriter...` | `InRange(1500, 3900)` ms | AN TOÃ€N (cáº­n rá»™ng) | ngÃ¢n sÃ¡ch chung ~2 s, biÃªn 1,9 s; 40/40 xanh dÆ°á»›i táº£i |
| `RecoveryRecycleGroupTests.Retry_GroupRecycle_RunsTheBinCallsOffTheCallerThread` | luá»“ng gá»i `new Thread` cháº·n | AN TOÃ€N | id luá»“ng gá»i sá»‘ng vÃ  cháº·n trong khi viá»‡c cháº¡y |
| `RecoveryRetryServiceTests.RetryMoveOrCopyAsync_RunsMutationOffCallerThread` | luá»“ng gá»i riÃªng + SynchronizationContext | AN TOÃ€N | hai id láº¥y khi cáº£ hai luá»“ng cÃ²n sá»‘ng |
| `UndoServiceGroupTests` (RunOnDedicatedThread, 3 chá»—) | luá»“ng gá»i `new Thread` cháº·n | AN TOÃ€N | nhÆ° trÃªn |
| `NavigationStatWorkerTests` (2 test) | worker chuyÃªn dá»¥ng (khÃ´ng thoÃ¡t) vs luá»“ng test/continuation | AN TOÃ€N | worker sá»‘ng suá»‘t tiáº¿n trÃ¬nh |
| `SourceBytesCacheTests.GetOrRead_ReadsOnCallingThread` | luá»“ng `new Thread`, so id vá»›i `LastReadManagedThreadId` | AN TOÃ€N | cÃ¹ng má»™t luá»“ng, Ä‘á»c trong khi luá»“ng sá»‘ng |
| `SourceBytesCacheRobustnessTests.LateReaderAfterPublish...` | id luá»“ng LongRunning Ä‘ang Ä‘á»— vs luá»“ng tháº¯ng | AN TOÃ€N | luá»“ng "muá»™n" sá»‘ng Ä‘áº¿n khi `winnerDone` |
| `PreloadKickOffCallerTests` (3) | id luá»“ng test giá»¯a `TrackCaller` vÃ  `StopTracking` | AN TOÃ€N | khoáº£ng Ä‘á»“ng bá»™, khÃ´ng `await` |
| `PreviewImageServiceMutationTests`, `PreloadSchedulerMutationTests`, `ViewerDecodePriorityTests` | Ä‘á»‘i tÆ°á»£ng `Thread`/cá» pool/priority | AN TOÃ€N | Ä‘Ã£ so Ä‘á»‘i tÆ°á»£ng Thread (#463/#464) |
| `ImagePresenterTests.StatOffUiThread` | id luá»“ng test; má»i stat cháº¡y trÃªn `NavigationStatWorker` riÃªng | AN TOÃ€N (láº­p luáº­n) | worker Ä‘Æ°á»£c táº¡o sau khi láº¥y id vÃ  sá»‘ng suá»‘t; chÆ°a Ä‘á»•i vÃ¬ khÃ´ng cÃ³ Ä‘Æ°á»ng stat nÃ o khÃ¡c ngoÃ i `StatNow` |
| `AppRound3GapTests`, `DispatcherUiSchedulerTests`, `WpfPresentationSinkTests` | so id vá»›i `Dispatcher` thread cá»§a test | AN TOÃ€N | thread sá»‘ng Ä‘áº¿n `Dispose`; so trÆ°á»›c khi táº¯t |
| `Win32UiDispatcherTests` (7), `MessageLoopFrameClockTests` | so vá»›i `UiThread.ManagedThreadId` | AN TOÃ€N | UI thread chuyÃªn dá»¥ng sá»‘ng Ä‘áº¿n `await using` |
| `ReviewCatalogThreadGuardTests` | luá»“ng khÃ¡c vá»›i luá»“ng Ä‘Ã£ gáº¯n | AN TOÃ€N | chá»§ sá»Ÿ há»¯u cÃ²n sá»‘ng khi luá»“ng thá»© hai cháº¡y |
| `FileHashServiceTests` | `Assert.Null(LastReadManagedThreadId)` | AN TOÃ€N | khÃ´ng so id |
| `ImagePresenterTests.CompareBusyAndDimensions`, `ViewerDecodePriorityTests` | ghi `Thread.Priority` do code dÆ°á»›i test Ä‘áº·t | AN TOÃ€N | giÃ¡ trá»‹, khÃ´ng pháº£i thá»i gian |
| `FixturePerfTest.FixtureGenerationCpuBudget` (Slow) | CPU time cá»§a luá»“ng gá»i <= 10 s | AN TOÃ€N (cáº­n rá»™ng) | CPU time, Ä‘o 1-2 s |
| `ShellIdleCpuReportTests`, `KineticPanFrameMeasurementTests`, `SessionStoreSweepPerfTests` | CPU/Æ°u tiÃªn/thá»i gian thá»±c | KhÃ´ng cháº¡y trong CI | `Category=Manual` hoáº·c `Native` (lá»c `!Manual&!Native`) |
| Fuzz/hang-guard: `ParserMutationFuzzTests` (worst < 500 ms), `RawContainerFuzzTests` (p95), `PreviewCacheFileMutationTests`, `PreloadSafetyTests`, `SessionStoreTests`... | ngÃ¢n sÃ¡ch thá»i gian chá»‘ng treo | AN TOÃ€N (cáº­n rá»™ng, ghi nháº­n) | ParserFuzz 60/60 xanh dÆ°á»›i táº£i (2,4 triá»‡u láº§n parse); ngÃ¢n sÃ¡ch >= 1000 láº§n thÆ°á»ng lá»‡; khÃ´ng Ä‘á»•i |
| Test dÃ¹ng `Stopwatch` lÃ m Ä‘á»“ng há»“ giáº£ (`BurstPreloadTests`, `KeepWarmTests`, `FrameStatsTests`, `Animator`...) | `Stopwatch.Frequency` lÃ m Ä‘Æ¡n vá»‹ | AN TOÃ€N | Ä‘á»“ng há»“ do test Ä‘iá»u khiá»ƒn |

## 3. Báº±ng chá»©ng

| Test | TrÆ°á»›c (dÆ°á»›i 12 luá»“ng Ä‘á»‘t CPU) | Sau |
|---|---|---|
| `JournalDurabilityTests.SafeMode_...` | 299/300 Ä‘á» (`Assert.NotEqual`) | 400/400 xanh |
| `GpuKeepAliveTests` x7 | Reactivation 29/30, NonPositive 2/30, NeverShortens 1/30 Ä‘á» (30 láº§n) | 300/300 xanh má»—i test |
| Mutation (src táº¡m, Ä‘Ã£ hoÃ n nguyÃªn) | - | Journal: cháº¡y `Move` ngay trÃªn luá»“ng gá»i -> Ä‘á» (NotSame). GpuKeepAlive: bá» háº¡n chÃ³t -> Activation/Reactivation Ä‘á»; cho phÃ©p rÃºt ngáº¯n háº¡n chÃ³t -> NeverShortens Ä‘á»; bá» ngá»§ khoáº£ng -> Interval/NonPositive Ä‘á»; káº¹p trÃªn 1000 -> Huge Ä‘á»; káº¹p dÆ°á»›i 0 -> NonPositive Ä‘á» |

Ghi chÃº: Ä‘á»™t biáº¿n "bá» káº¹p dÆ°á»›i" (`Math.Min(intervalMs, 1000)`: khoáº£ng Ã¢m khÃ´ng káº¹p vá» 1) KHÃ”NG lÃ m NonPositive Ä‘á» - vá»›i khoáº£ng Ã¢m
`SetWaitableTimer` nháº­n má»‘c tuyá»‡t Ä‘á»‘i vÃ  váº«n cháº¡m ~1 láº§n/ms trÃªn mÃ¡y nÃ y (chÆ°a tÃ¬m hiá»ƒu sÃ¢u; hÃ nh vi quan sÃ¡t Ä‘Æ°á»£c tÆ°Æ¡ng Ä‘Æ°Æ¡ng káº¹p 1 ms).
Äá»™t biáº¿n káº¹p dÆ°á»›i 0 (khoáº£ng 0 = háº¹n giá» tÆ°Æ¡ng Ä‘á»‘i 0) thÃ¬ Ä‘á» (62 láº§n cháº¡m / 52 ms).

## 4. Luáº­t kiáº¿n trÃºc

`TestQualityRulesTests.TestsDoNotCompareManagedThreadIds` (rule `threadid`): cáº¥m `Assert.Equal/NotEqual(...)` cÃ³ tham sá»‘ lÃ 
`...ThreadId` hoáº·c biáº¿n `caller|worker|pool|mutation|continuation|owner|dispatcher|bound...Thread`, ngoÃ i allowlist co dáº§n (11 file
AN TOÃ€N á»Ÿ báº£ng trÃªn). KhÃ´ng cáº¥m `Assert.All(..., id => Assert.NotEqual(callerThread, id))` ngoÃ i tÃªn biáº¿n Ä‘Ã£ liá»‡t kÃª (regex dá»±a
tÃªn): giá»›i háº¡n Ä‘Ã£ biáº¿t, Ä‘á»•i láº¥y khÃ´ng dÆ°Æ¡ng tÃ­nh giáº£ á»Ÿ bá»™ Ä‘áº¿m kiá»ƒu `perThread`/`OutstandingOnThisThread` (Ä‘Ã£ loáº¡i).

## 5. Ngân sách tài liệu

Dòng của fragment này làm `OPEN-DECISIONS.md` vượt ~130 byte quanh trần 48 KB (48 992 / 49 152 byte trên master); trần đã được nâng gấp đôi riêng ở #473 nên PR này không đổi `tools/docs-budget.ps1` nữa.
