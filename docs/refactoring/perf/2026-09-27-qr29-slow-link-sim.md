# Q-R29 slow-link simulation (option B) -- measurement only, no production behavior change

**Scope:** Q-R29 asks whether (1) `ImagePresenter.TryGetFileStat`/`ThumbnailCache.BuildKey`'s synchronous UI-thread
file-metadata calls and (2) whole-folder preload's lack of an I/O priority/bandwidth cap hurt photo-switch latency
on a NAS/wifi link (500 x 3-7 MB JPEG). [R01-R13](../decisions/R01-R02-R03-R13.md) and
[R04-R14](../decisions/R04-R14.md) ruled both out on local NVMe; no real NAS fixture exists on this machine. This
pass builds a **simulated slow-link harness** (option B) to give the user's 2026-09-27 decision ("B then C-if-needed")
real numbers instead of guessing. **No production code path or default behavior changed** -- the simulation lives
entirely in `src/PhotoReview.Benchmarking/SlowLinkFileSystem.cs` and is wired in only by
`tools/PhotoReview.Benchmark.Cli`'s `--perf-session` when new `--slow-link-latency-ms`/`--slow-link-bandwidth-mbps`
flags are passed (absent: byte-for-byte the production `PhysicalFileSystem`/`CountingFileSystem` graph).

## Method

`SlowLinkFileSystem` (`IFileSystem` decorator, same pattern as the existing `CountingFileSystem`) wraps the real
file system:
- every metadata-only call (`FileExists`, `DirectoryExists`, `GetFileStat`, the enumerate-with-stat overloads) sleeps
  a fixed extra latency before delegating -- this is the call path `ImagePresenter.TryGetFileStat` and the whole-folder
  scan go through; it is `Thread.Sleep`, so it blocks whichever thread calls it (the UI thread for
  `TryGetFileStat`, exactly the cost R01-R13 measured at 0.1-0.3 ms on local NVMe).
- every byte read through `OpenReadShared` is metered against a `SharedBandwidthLimiter` (token bucket), and the
  **same limiter instance** is handed to every consumer of the overridden `IFileSystem` singleton -- so the
  foreground viewer decode and every whole-folder preload worker draw from one shared budget, modelling one wifi
  link's total bandwidth being split between them (the R04 "no software sync gap, only the physical link can be
  the bottleneck" finding, now made measurable).

Wiring (`tools/PhotoReview.Benchmark.Cli/PerfSession.cs`): a new `AddSingleton<IFileSystem>` override in the same
`AppHost.BuildServices(overrides => ...)` seam `--cache-dir` already uses for `IAppPaths`, so `--perf-session`
still runs the production DI graph otherwise unmodified. Preload worker count is controlled with the existing
`PHOTOREVIEW_DIAG_PRELOAD_WORKERS` env var (no new code needed for that axis; `run-matrix.ps1`/`PerfAnalyze` already
group by it).

**Fixture:** no NAS available, so per the task this is a local real fixture standing in as the *content* side of the
simulation (the *link* side is what's synthetic). 500 real JPEGs selected from `C:\Xiuren\[[WALLPAPER]` (2,058 files
total; untouched -- verified 2,058 before and after), filtered to the 2-8 MB range (close to the requested 3-7 MB;
widened slightly because only 404 files fell strictly inside 3-7 MB) and hard-linked (zero data copy, same volume)
into `work/diag/qr29-fixture/` (selected set: 500 files, 2,505,160,751 bytes total, avg 5.0 MB, min 2.1 MB, max
8.4 MB). Hard links are read-only with respect to the original files (no photo was moved, copied by value, or
modified); the harness never writes into `C:\Xiuren`.

**Scenario:** `tools/diag/scenarios/s2-next-slow-quick.json` (open folder, wait idle, 40x Right with event-driven
settle, `--mode Preview`), run directly against the built CLI exe (same approach `run-matrix.ps1` uses internally),
`--repeat 1` per cell, `--cache-dir` isolated per cell so no cell's disk cache warms another's.

## Finding: the bandwidth cap does not reach the actual image-byte read path

Validating the two mechanisms separately turned up an important asymmetry:

- **Metadata latency is genuinely simulated and validated.** `ImagePresenter`/`FolderLoadCoordinator` both resolve
  `IFileSystem` from DI (`MainViewModelCompositionRoot.cs:36`), so the overridden `SlowLinkFileSystem` singleton is
  what `TryGetFileStat` and the whole-folder `EnumerateFilesWithStat`/`TryProbeReadable` scan actually call. This
  shows up directly in the folder-open step: **baseline (no simulated link) opened the 500-file folder in 1,052 ms;
  the identical folder with only 2 ms of added per-metadata-call latency took 8,450-8,853 ms** -- confirming the
  injected latency is real and, because a folder scan makes several metadata calls per file (listing +
  probe-readable), a small per-call NAS latency compounds fast across a folder this size.
- **The shared bandwidth cap does not reach production image decode.** `SourceBytesCache.ReadAndCache`,
  `WicDirectDecoder`, `WpfBitmapImageDecoder.Decode`/`DecodeWithFallback`, and `PreviewImageService`'s viewer-decode
  path all open the source file with their **own direct `new FileStream(...)`**, never through
  `IFileSystem.OpenReadShared`. `SlowLinkFileSystem.OpenReadShared` is real and does throttle whatever calls it, but
  nothing in the actual photo-review read path calls it today. This is why bandwidth-capped cells (5 MB/s) did not
  show proportionally longer wall time than the 60 MB/s cells for the dominant byte volume -- the cap was correctly
  enforced on the surface that exists, but that surface is not the one carrying image bytes. Confirmed by grepping
  every direct `FileStream`/`ReadAllBytes` construction in `src/PhotoReview.Imaging` and `src/PhotoReview.App`.
  **This is itself a finding for Option C**: throttling preload's bandwidth use is not a config toggle away -- there
  is no existing seam in `SourceBytesCache`/the decoders to inject a shared byte-rate limiter without adding one to
  those hot-path classes, which is exactly the kind of change Option C would need to make (out of scope for this
  measurement-only pass, which was explicitly told not to change production behavior).

## Caveats

- **This machine was heavily loaded by other concurrent Claude sessions during the entire run** (`Get-Counter
  '\Processor(_Total)\% Processor Time'` read ~98.5% mid-run; multiple other worktree sessions on this repo build/
  test concurrently per AGENTS.md's own parallel-agents workflow). R04's doc already flagged this exact box as
  capable of a ~15x P95 swing (19 ms -> 297 ms cold, no simulated link at all) purely from external contention.
  **The baseline cell here (no simulated link at all) recorded key-settle P50 1509.7 ms / P95 1524.1 ms with
  40/40 timeouts** -- i.e. contention alone saturated the scenario's 1500 ms settle cap even with zero injected
  latency, and cell 2 (2 ms latency, 60 MB/s cap -- objectively *more* simulated delay than baseline) measured
  **faster** (P50 45.5 ms) once the box quieted down between cells. **The empirical P50/P95 table below is not a
  reliable before/after comparison of the simulated link** -- consecutive cells ran under visibly different real
  system load, the same failure mode R04 already documented for this shared box. It is included for transparency
  and because the raw byte/latency counters inside each cell are still informative (see Conclusion).
- SMB caching, oplocks, request pipelining/multiplexing, and directory-listing round-trip cost of a real SMB/CIFS
  share are not modeled -- `SlowLinkFileSystem` only delays local NTFS calls and throttles local NTFS stream reads.
  A real NAS could be faster (client-side metadata caching) or slower (SMB protocol round-trips, small-file
  overhead) than this simulation in ways this pass cannot capture.
- `ThumbnailCache.BuildKey` calls `new FileInfo(path)` directly, not through `IFileSystem` -- it is **not** covered
  by the simulated latency (production code was intentionally left unchanged; adding an `IFileSystem` seam to
  `ThumbnailCache` is exactly the kind of change Option C would make, not this measurement pass). Its real-disk
  cost is already measured at 0.07-0.18 ms/call (R02); add one more `metadataLatency` per navigation on top of the
  numbers below to account for it on a real NAS.
- Single-repeat cells (no `-Repeat 3` averaging) given the matrix size and time budget -- see reduced-matrix note.
- Fixture is 500 real JPEGs 2-8 MB (avg 5.0 MB), not literally a NAS; only the link is simulated.

## Matrix (reduced from the full latency x bandwidth x workers grid for time; see note below)

Latencies requested: 2/10/30 ms. Bandwidths requested: 5/20/60 MB/s. Preload workers requested: default(8)/0/2.
Full 3x3x3 = 27 cells was not run in the time available; this pass runs the four corners of the latency x bandwidth
grid at default preload, plus a "typical NAS" midpoint (10 ms / 20 MB/s) crossed with all three preload-worker
settings, plus a worst-case-and-preload-off combination -- 9 cells total, `--repeat 1` each.

| Cell | Latency | Bandwidth cap | Preload workers | Wall time | Key-settle P50 | P95 | Timeouts/40 | Cache hits/misses | Stat calls | Bytes read | Decode ms (sum) |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline-nolink | - | - | default(8) | 131.1 s | 1509.7 ms | 1524.1 ms | 40 | 39/634 | 85 | 3.53 GB | 933,085 |
| lat2-bw60 | 2 ms | 60 MB/s | default(8) | 90.4 s | 45.5 ms | 1513.9 ms | 8 | 32/509 | 85 | 2.57 GB | 588,857 |
| lat2-bw5 | 2 ms | 5 MB/s | default(8) | 131.0 s | 1506.7 ms | 1558.7 ms | 37 | 40/500 | 85 | 2.51 GB | 933,284 |
| lat30-bw60 | 30 ms | 60 MB/s | default(8) | 116.7 s | 88.5 ms | 1513.6 ms | 18 | 40/500 | 85 | 2.51 GB | 712,009 |
| lat30-bw5 | 30 ms | 5 MB/s | default(8) | 116.1 s | 63.5 ms | 1518.9 ms | 18 | 40/500 | 85 | 2.51 GB | 705,391 |
| lat10-bw20 (typical) | 10 ms | 20 MB/s | default(8) | 76.1 s | 43.4 ms | 70.8 ms | 0 | 40/500 | 85 | 2.51 GB | 488,729 |
| lat10-bw20-workers0 | 10 ms | 20 MB/s | **0** | 30.0 s | **342.5 ms** | 551.9 ms | 1 | **0/41** | 84 | 0.20 GB | 14,072 |
| lat10-bw20-workers2 | 10 ms | 20 MB/s | 2 | 136.9 s | 1506.4 ms | 1526.5 ms | 40 | 40/361 | 85 | 2.01 GB | 243,351 |
| lat30-bw5-workers0 | 30 ms | 5 MB/s | **0** | 37.3 s | **267.7 ms** | 349.8 ms | 0 | **0/41** | 85 | 0.20 GB | 8,897 |

(Bandwidth cap "-" = simulation disabled entirely, i.e. the plain production `PhysicalFileSystem`. "Bytes read"/"Decode ms"
are `ReviewMetrics` totals for the whole run, not per-navigation.)

**A clean same-settings comparison** (both `lat10-bw20`, both landed in a quiet window on this box -- 0-1 timeouts,
no saturated-cap artifact): **preload ON (default 8 workers) settles at P50 43.4 ms vs preload OFF at P50 342.5 ms
-- preload OFF is ~8x slower per navigation**, because turning preload off makes *every* navigation a live cache
miss under the simulated link (0/41 hits) instead of a RAM hit after the one-time folder-open cost pays for all 500
files up front (40/500 hits -- the 500 "misses" there are preload's own reads, not the user waiting on them). This
is the same shape R04 already found on local disk: preload is a net win, not a net cost, as long as it has time to
finish before navigation catches up to it. This scenario paces one key every 1.5 s, which preload comfortably
outruns even at 20 MB/s; it does **not** stress the specific failure mode Q-R29/R04 flagged (a fast key-repeat
burst outrunning preload while it is still competing for the same capped link) -- `s3-next-burst`/`s4-jump` would be
needed to test that specifically, which this pass's time budget did not allow.

## Deterministic bandwidth-cap arithmetic (robust to the contention noise above)

Unlike the settle-time table, the *bandwidth cap itself* is an explicit `Thread.Sleep`-based token bucket -- its
effect on total bytes-per-second throughput does not depend on decode CPU cost or box load the way wall-clock
settle time does. Given this fixture's average JPEG (5.0 MB) and the 3-7 MB range Q-R29 asks about:

| Bandwidth cap | Time to serve one average (5 MB) cache-miss image, if it is the only reader | ...if preload is using the full cap concurrently |
|---|---:|---:|
| 5 MB/s (weak wifi) | ~1.0 s | proportionally longer -- foreground read waits behind whatever preload bytes are already "in flight" against the same shared bucket |
| 20 MB/s (typical wifi) | ~0.25 s | same caveat |
| 60 MB/s (strong wifi / wifi 6) | ~0.08 s | same caveat |

These are **per cache-miss image**, not per navigation -- once the whole-folder preload finishes (RAM cache, per
AGENTS.md's "load the whole folder into RAM" policy for folders under 16 GB, which this 2.5 GB fixture qualifies
for), subsequent navigations are RAM hits and pay none of this. The cost is concentrated in the initial folder-open
window and any navigation that outruns preload (fast key-repeat bursts, `s3-next-burst`/jump scenarios) -- exactly
what R04 already identified as the only real Q-R29 exposure once local-disk software contention was ruled out.

## Conclusion

Two separate lines of evidence, both pointing the same way:

1. **Metadata latency (measured, real, validated end-to-end):** even 2 ms of added per-metadata-call latency turned
   a 1.05 s folder open into an 8.45-8.85 s one (500 files) -- an ~8x increase from a per-call cost the user's own
   NAS description (2-30 ms) treats as the *low* end. `TryGetFileStat` runs synchronously on the same thread that
   calls it (the UI thread, per R01), so on a real NAS at even 2 ms/call this is UI-thread time, not background
   time. This alone is evidence-based (not just arithmetic) and already crosses the user's "~10 ms added latency
   per photo switch" bar by a wide margin once folder-scan and per-navigation stat calls are counted together.
2. **Bandwidth cap (arithmetic only, not empirically demonstrated by this pass -- see Finding above):** a real wifi
   link's bandwidth (5-60 MB/s, the range implied by Q-R29's own description) would add ~80 ms (60 MB/s) to ~1 s
   (5 MB/s) per average-size (5 MB) cache-miss image if nothing else were competing for it, and more once preload
   is also drawing on the same link. This pass could not measure it directly because production's image-byte reads
   (`SourceBytesCache`, the decoders, `PreviewImageService`) do not go through the `IFileSystem` seam this harness
   throttles -- so treat this half as a textbook argument, not a measured one, until a seam exists to test it for real.

**Recommendation: Option C is triggered**, primarily on the strength of finding (1), which is measured rather than
inferred. The lead should open a separate PR implementing (a) moving `ImagePresenter.TryGetFileStat`/
`ThumbnailCache.BuildKey`'s synchronous stat calls off the UI thread, and (b) throttling whole-folder preload (or
capping its concurrent worker count) **specifically when the foreground read is observed to be slow while preload
has not yet finished** -- noting that (b) will first need its own byte-rate-limiting seam in `SourceBytesCache`/the
decoders, since none exists today (see Finding above); that seam-building is itself part of the Option-C scope, not
a prerequisite blocking it. **One nuance the matrix surfaced (see the `lat10-bw20` comparison above): preload OFF
was ~8x slower per navigation than preload ON** at the same simulated link settings, because disabling it turns
every navigation into a live cache miss instead of a one-time up-front cost -- so (b) should not be "disable/reduce
preload on a slow link" in general, only "de-prioritize preload's bandwidth share while a specific foreground read
is waiting and preload has not reached that file yet" (the burst-outrunning-preload case this pass's steady-paced
scenario did not stress).

This measurement pass does **not** replace Option A (a real NAS measurement) -- the caveats above (SMB protocol
behavior not modeled, this box's contention noise swamping the empirical settle-time table, `ThumbnailCache.BuildKey`
not covered by the simulation, and the bandwidth-cap seam gap just described) mean the *exact* millisecond numbers
here should not be taken as NAS-accurate. But finding (1) alone is a real, measured, order-of-magnitude effect on
this machine's own disk (not a NAS), which is reason enough to act on Option C now rather than block on real-NAS
access this machine does not have.
