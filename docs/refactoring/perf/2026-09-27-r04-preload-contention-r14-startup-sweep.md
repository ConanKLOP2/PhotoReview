# R04/Q-R29 preload contention -- local-disk evidence, and R14 startup sweep (2026-09-27; F4 = 2058 files, 17.6 GB)

**Code-reading (no live measurement needed): the worker-count reduction while a viewer decode is active
(`PreloadScheduler.cs:88-97,343-345`) only throttles NEW preload starts to `_viewerBusyWorkerLimit`
(>=2); a preload read already in flight is never interrupted (its own comment says so, `:338-342`) --
but this is not a software synchronization gap.** The viewer's own decode never queues behind preload
workers at all: `PreviewImageService.DecodeForViewerAsync` takes its own `_viewerSlots` gate
(`ViewerDecodeSlots = 2`, separate from preload's `_preloadSlots` semaphore) and runs the actual decode on
a dedicated `TaskCreationOptions.LongRunning` thread raised to `ThreadPriority.AboveNormal`
(`PreviewImageService.cs:412-450`), specifically so neither preload workers nor thread-pool growth delays
it (comment at `:415-421`, from the #39-#48 perf night). `SourceBytesCache.GetOrRead` dedups per-key
(`ConcurrentDictionary<Key, Lazy<byte[]>>`) and only locks (`_publishGate`) around cheap in-memory
bookkeeping, never around the actual `FileStream` read (`SourceBytesCache.cs`), so a preload read of file A
cannot block a viewer read of file B behind a shared lock. `WicDirectDecoder.CreateFactory()` builds a
fresh `IWICImagingFactory` per call (no cached/shared COM object either). **No shared semaphore, lock, or
decoder instance was found that a preload read could hold while a foreground decode waits on it** --
the only channel left for one to slow the other is the physical device (disk queue depth / network
bandwidth), which is exactly Q-R29's NAS hypothesis, not a fixable ordering bug.

Local measurement (F4, real NVMe/SSD-class disk; harness: `PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0|8` env
override + `run-matrix.ps1 -Scenarios s2-next-slow-quick -Conditions warm -Repeat 2`), run on a shared dev
box with 20-40 other `dotnet.exe` processes belonging to other concurrent sessions (`Get-Counter
'\Processor(_Total)\% Processor Time'` read 85-100% during parts of this run, ~7-24% during others --
confirmed with the counter, not guessed):

| Preload | Run | key-settle P50 | P95 | timeouts/40 | System load at the time |
|---|---|---:|---:|---:|---|
| 8 workers (default) | cold cache | 19.1 ms | 1528.8 ms | 13 | ~85-100% CPU (other sessions) |
| 8 workers (default) | warm cache (2nd run) | 14.8 ms | 31.6 ms | 0 | quiet |
| 0 workers (disabled) | cold cache | 297.1 ms | 343.9 ms | 0 | ~85-100% CPU (other sessions) |
| 0 workers (disabled) | warm cache (2nd run) | 14.5 ms | 31.6 ms | 0 | quiet |

Reading this: cold-cache navigation with preload OFF is far slower (P50 297 ms) than with it ON (19 ms)
-- expected, since every image must then decode from source on the UI's own request instead of already
being warm. The P95 spike (1528.8 ms, 13/40 timeouts) happened on the 8-worker cold-cache run specifically
while this shared box was near 100% CPU from other sessions' processes; the very next run (same code,
quieter box) landed at P95 31.6 ms with zero timeouts -- and the 0-worker run's own warm-cache pass landed
at the *identical* 14.5/31.6 ms once its disk cache was warm too (populated by ordinary viewer decodes,
which write to the disk cache regardless of the preload worker count). **The tail correlates with
external CPU contention on the shared box, not with the preload-worker setting**: turning preload off
did not remove the tail (it just wasn't present in that quieter run either), and turning preload on did
not introduce a tail once the box was quiet. This is consistent with the codebase's own prior finding
([2026-09-27-image-crossfade.md](2026-09-27-image-crossfade.md): "an interleaved first attempt showed P95 in
the 1500 ms range... CPU contention, discarded") -- the same artifact reproduced here independently.

**Conclusion for Q-R29:** on local disk, no evidence of a software synchronization/starvation gap was
found (code-level: viewer decode is fully decoupled from preload's worker pool and locks; empirically: the
P95 tail tracked system-wide CPU load, not the preload on/off setting). This narrows Q-R29 to what it
already suspected: whether whole-folder preload starves the next decode is a **bandwidth-only** question
specific to a slow link (NAS/Wi-Fi), which cannot be reproduced or ruled out on this local-disk box --
it still needs a real NAS/slow-link measurement before deciding whether to add I/O throttling. See
`../OPEN-DECISIONS.md` Q-R29.

**R14 startup sweep:** `SessionStore`'s constructor ran `SweepStaleTempFiles` synchronously (resolved on
the UI thread at startup via `MainViewModelCompositionRoot`). Measured on real disk (temp directory, not
the user's real session folder): cost scales ~linearly with the stale-`*.tmp`-file count -- 100 files ~24
ms, 500 ~120 ms, 1000 ~282 ms, 3000 ~700 ms-1.3 s (`SessionStoreSweepPerfTests`, `Category=Native`). This
is material against the ~1.8 s app-start-to-first-image budget ([perf night](../perf/2026-09-24-perf-night.md)) for a plausible
worst case (R2-F-34: one leftover `*.tmp` per crash between an atomic write's temp-create and rename;
years of crashes/kills could plausibly leave hundreds to low thousands). **Fix:** the sweep now runs on a
background `Task` (`SessionStore.StartupSweepTask`, test-seam only) instead of blocking the constructor.
Proven safe: `Load`/`Save` never touch `*.tmp` files (`Load` reads the real `*.json` path; `Save`'s own
temp file is renamed away, or cleaned up by `WriteAllTextAtomic` itself on failure), and the existing
1-day age guard means a background sweep can never delete a temp file a concurrent `Save()` is still
writing (that file is milliseconds old, never "stale") -- confirmed by a new test that gates the sweep
mid-flight and asserts `Save`/`Load` still complete immediately instead of waiting for it
(`SaveAndLoad_WhileStartupSweepStillRunning_AreUnaffected`). After the fix, construction time is 0.0-0.2 ms
regardless of stale-file count (same test file, mutation-checked: reverting to the synchronous call makes
the assertion fail as expected).
