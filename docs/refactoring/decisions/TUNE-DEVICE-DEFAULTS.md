---
id: TUNE-DEVICE-DEFAULTS
order: 140
summary: |-
  Decided 2026-10-07 (user: follow the recommendations): keep the shipped defaults for this PC (device config bench option D); re-run the DOTNET_TieredPGO=0 confirmation (24+ runs, Repeat 8) and adopt it only if it holds, via System.Runtime.TieredPGO=false in the app runtimeconfig; the PerfCsvListener.Enqueue race stays as is (accepted); local branch codex/review-all-20261004 deleted (ledger stays in tag review-20261004-archive).
---

# TUNE-DEVICE-DEFAULTS - device config bench outcome and small leftovers

## Current state

The bench of 2026-10-07 ([`../perf/2026-10-07-device-tuning-results.md`](../perf/2026-10-07-device-tuning-results.md)) found no measurable winner for
decoder, mode, workers, window, RAM % or caches on this PC (Turbo power plan, ~22 GB free RAM; A/A noise of per-run P95 up to 27 %).
One candidate remains: `DOTNET_TieredPGO=0` (two batches agree, below the 2x-noise bar).

## (a) Device config bench ([plan](../perf/PLAN-device-config-bench.md))

| | Change | Pros | Cons |
|---|---|---|---|
| A | Adopt the best measured values per setting | Possible gain | No setting beat the noise; risk of tuning to one PC |
| B | Per-device auto-tuning at start-up | Adapts to each machine | Large new code and test surface for no measured gain |
| C | Change defaults to the bench rank-1 values | Simple | Same as A, and the ranking is inside the noise |
| D | Keep the shipped defaults | No risk, no work; matches the data | Gives up a possible small gain on other hardware |

Decision: **D**, chosen by the user on 2026-10-07 (the recommendation).

## (b) DOTNET_TieredPGO=0

Option (a): re-run the confirmation (24+ runs, Repeat 8) and adopt only if it holds, by setting `System.Runtime.TieredPGO=false`
in the app's runtimeconfig. Not adopted until the re-run confirms it.

## (c) PerfCsvListener.Enqueue race

A row that passes the `_writerFaulted` check can be written after the drain and is then never counted as dropped. Only the diagnostic
counters are affected. Left as is (accepted).

## (d) Branch codex/review-all-20261004

Deleted locally; the ledger stays in tag `review-20261004-archive`.

## When to revisit

(a) with real-world baseline comparison or memory-pressure data; (b) when the re-run is done; (c) if the counters are ever used for decisions.
