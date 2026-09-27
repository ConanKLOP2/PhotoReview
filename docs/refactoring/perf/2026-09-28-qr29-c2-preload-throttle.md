# Q-R29 option C-2 -- ISourceReader seam, slow-link preload/viewer contention measurement

Decision and fix: [decisions/Q-R29-C2.md](../decisions/Q-R29-C2.md). This file: method, raw numbers, caveats (kept
separate per the append-conflict convention in AGENTS.md).

## Method

- **Builds.** Single branch (`perf/qr29-c2-preload-throttle`), before/after comparison done via env var
  (`PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0` for "preload off") and `--slow-link-*` presence rather than two binaries,
  since the seam itself is a pure refactor (confirmed: full test suite unchanged before/after, see the decision
  file) and the fix (`SlowLinkViewerBusyWorkerLimit`) is gated on `DecodeMillisecondsEwma`, which stays 0 on an
  otherwise-idle `ReviewMetrics` in the fast-link case.
- **Harness.** `tools/PhotoReview.Benchmark.Cli --perf-session`, `--slow-link-latency-ms 10 --slow-link-bandwidth-mbps
  20` (typical-wifi midpoint, matching #202's own "typical NAS" cell). `SlowLinkSourceReader` (new,
  `src/PhotoReview.Benchmarking/SlowLinkSourceReader.cs`) wraps `ISourceReader` with the same
  `SharedBandwidthLimiter` #202's `SlowLinkFileSystem` used, wired in by `PerfSession.cs` alongside the existing
  `IFileSystem` override.
- **Fixtures.**
  - F4: `C:\Xiuren\[[WALLPAPER]`, read-only in place, 2,058 files, 18,883,457,837 bytes (checked unchanged before and
    after every run).
  - `fixture500`: 500 files hard-linked from F4 (2-8 MB range, same selection method as #202), 2,505,160,751 bytes,
    built fresh for this pass (`work/diag/qr29c2/fixture500`, not committed -- machine-local scratch per AGENTS.md).
- **Scenarios.** `tools/diag/scenarios/s4-jump.json` (event-driven settle, 1500 ms cap) and `s3-next-burst.json`
  (200x Right @ 33 ms, no settle instrumentation -- read via the post-burst `waitIdle` step's own outcome instead).
  `--mode Preview`, `--cache-dir` isolated per cell, `--repeat 1` (time budget).
- **Machine load.** Not idle: `Get-Counter '\Processor(_Total)\% Processor Time'` read 57.96% with several other
  `claude`/`dotnet` processes active throughout this pass (same shared-box caveat every prior Q-R29 fragment
  documents). Used as the reason to prefer deterministic byte/time arithmetic over raw wall-clock P50/P95 for the
  conclusion (see decision file).

## Raw numbers

| Fixture | Scenario | Preload workers | Link | keySettle P50/P95 (ms) | Timeouts/60 | idleTimeouts | Wall (open+waitIdle+burst+waitIdle) |
|---|---|---:|---|---:|---:|---:|---|
| F4 | s4-jump | default (8) | 10ms/20MB/s | 1511.4 / 1517.0 | 60/60 | 0 | n/a (s4 has no trailing waitIdle beyond the 3s wait step) |
| F4 | s4-jump | 0 | 10ms/20MB/s | 893.2 / 1051.3 | 0/60 | 0 | n/a |
| F4 | s4-jump | default (8) | none (baseline) | 1510.4 / 1519.9 | 60/60 | 1 | n/a |
| fixture500 | s4-jump | default (8) | 10ms/20MB/s | 1507.0 / 1515.0 | 60/60 | 1 (step-2 `waitIdle` itself TIMEOUT at 60090ms) | open 8365ms |
| F4 | s3-next-burst | default (8) | 10ms/20MB/s | n/a | n/a | 1 (final `waitIdle` TIMEOUT at 60238ms, "preload still busy") | ~123s total |
| F4 | s3-next-burst | 0 | 10ms/20MB/s | n/a | n/a | 0 (final `waitIdle` "idle after 1568ms") | ~47.5s total |

`metrics.json` totals for the F4 s4-jump cells (for the arithmetic in the decision file):

- default(8), 10ms/20MB/s: SourceBytesRead=2,110,301,799; SourceReads=138; SourceOpenCount=146; DecodeMilliseconds
  (sum across all concurrent readers)=835,126.
- workers=0, 10ms/20MB/s: SourceBytesRead=776,039,116; SourceReads=41; SourceOpenCount=41; DecodeMilliseconds=35,095.
- default(8), no link (baseline): SourceBytesRead=12,025,038,309; SourceReads=1,376; SourceOpenCount=1,384;
  DecodeMilliseconds=1,227,766 -- the much larger totals here are the F4-exceeds-cache confound (see decision file):
  preload keeps re-reading the folder because it never fully fits the 16 GiB default cache, independent of any
  simulated link.

## Confound and how it was found

The first pass (F4 only) showed **identical** keySettle numbers (~1510 ms P50, 60/60 timeouts) whether or not the
slow-link override was even present, which looked like "the fix has no effect" or "there is no real contention."
Checking `metrics.json` showed `SourceReads`/`CacheMisses` in the thousands for a 60-key scenario -- far more than
one pass over 2,058 files should need -- which pointed at continuous eviction/re-read because F4 (17.6 GiB) exceeds
the default 16 GiB preview cache. Switching to `fixture500` (2.5 GB, fits the cache easily) confirmed this: the
`waitIdle` right after opening `fixture500` still timed out at 60 s, but for an arithmetically different reason --
500 files x ~5 MB / 20 MB/s implies ~125 s minimum to preload the whole folder at that cap, well past every wait
window this harness uses. Both fixtures therefore show the same *surface* symptom (a `waitIdle`/settle step that
requires `PreloadController.IsIdle` never returns true quickly) for two different underlying reasons, neither of
which is machine-load noise. The F4 preload-on-vs-off `s3-next-burst` comparison (previous section) was chosen
specifically because it does not depend on `IsIdle` returning quickly at all -- it only asks "did the post-burst
`waitIdle` finish or hit its cap" -- and isolates the contention question cleanly from both confounds.

## Caveats

- Single run per cell (`--repeat 1`), same time-budget trade-off every Q-R29 fragment before this one made.
- Simulated link only, same SMB/protocol-behavior caveat as #202 and the option-C part-1 fragment.
- `fixture500` is machine-local scratch (`work/diag/qr29c2/`), not committed, same as #202's own fixture.
