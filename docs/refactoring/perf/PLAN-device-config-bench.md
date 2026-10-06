# Plan: bench many configurations to find the best one for this device (drafted 2026-10-06)

Status: PLAN ONLY, nothing measured yet. Replaces guesswork about `PreloadWorkerCount=8`, window 32/8, RAM 50 %, `WicDirect`.
Rule reminders: AGENTS.md priorities (min disk reads, use RAM, review speed first); do not compare numbers across the AR02c boundary;
Claude runs the measurements itself (real-machine rule); only visual checks go to the user.

## 0. Target device (measured 2026-10-06)

i7-9750H 6C/12T (laptop, Max-Q class thermals) | 31.9 GB RAM | one NVMe Kingston SNV2S500G 500 GB (DRAM-less) | RTX 2080 Max-Q + UHD 630 |
C: has only ~25 GB free | fixture F4 = `C:\Xiuren\[[WALLPAPER]` (1625 files, 13.2 GB, same disk as the OS and the caches) | build 2.0.326.
Consequences: (1) decode is CPU-bound, so workers > 12 logical threads cannot help; workers > 6 only pay off if SMT scales; (2) F4 + 4 GB disk cache +
batch cache dirs share the 25 GB free space; (3) a laptop throttles: results are only valid on AC power with a fixed power plan.

## 1. What can be tuned (knobs, defaults, levels to test)

| ID | Knob (where) | Default | Levels | Notes |
|---|---|---|---|---|
| K1 | `DecoderBackend` (`--decoder` exists) | WicDirect | Wpf, WicDirect, TurboJpeg | TurboJpeg needs `native/x64/turbojpeg.dll` |
| K2 | `LoadingMode` (`--mode` exists) | Preview | Fast, Preview, Original | Original = quality choice, compare separately, never "win" on speed alone |
| K3 | `PreloadWorkerCount` | 8 | 2, 4, 6, 8, 10, 12, 16 | cap 64; 12 = logical cores |
| K4 | `PreloadForwardCount` / `PreloadBackwardCount` | 32 / 8 | fwd 8,16,32,64,128 x bwd 0,4,8,16 (subset, see S3) | each preview ~12 MiB |
| K5 | `ImageCacheRamPercent` | 50 | 25, 35, 50, 65, 75 | min clamps from window; whole-folder mode when folder <= threshold |
| K6 | `UseSourceBytesCache` (+ `SourceBytesCapacityBytes`) (`--source-bytes-cache` exists) | off, 16 GiB | off; on at 4/8/16 GiB | trades RAM for re-read avoidance |
| K7 | `PreviewDiskCacheCapacityBytes` | 4 GiB | 0 (off), 2, 4, 8 GiB | C: free space limits the top end |
| K8 | `PreloadMemoryLoadLimit` / `MemoryReserveBytes` | 0.90 / 2 GiB | 0.85, 0.90, 0.95 / 1, 2, 4 GiB | safety knobs; judged only under memory pressure (S6) |
| K9 | `ScalingQuality` | HighQuality | per enum | affects decode/scale cost and quality; visual gate needed |
| K10 | `LoggingEnabled` | off | on/off | overhead already covered by `logging-on/off` profiles; keep off |
| K11 | Runtime (env, optional S8) | defaults | `DOTNET_gcServer`=0/1, `DOTNET_GCgen0size`, `DOTNET_TieredPGO`=0/1, `DOTNET_TieredCompilation`/ReadyToRun | measure GC % and first-image latency |
| K12 | OS (environment, not product) | - | power plan, Defender exclusion on fixture/cache, indexer paused | recorded as conditions, never as app config |

Not tuned here: file-action settings (`JournalDurability`, S8-move scenario is run only as a regression guard), UI/shortcut settings.

## 2. Harness gaps to close first (code PR "feat(harness): --set overrides + tune-matrix")

Today `--perf-session` can override only `--mode`, `--decoder`, `--source-bytes-cache` (`PerfSession.ParseArgs`); everything else comes from
`config.json`. Editing the real config between runs is unsafe and not reproducible. Needed:

1. **`--set Key=Value` (repeatable)** on `--perf-session`: in-memory override of a whitelisted `AppSettings` property (K3-K9, `ScalingQuality`),
   applied before `AppHost.BuildServices` captures `SettingsStore.Current` (same place as the existing overrides); `config.json` untouched;
   unknown key / out-of-range value = hard error; the effective values are written into the run's `meta.json` so every result is self-describing.
   Tests: `PerfSession.ParseArgs` cases + one Integration test that the override reaches `PreloadScheduler` (mutate: drop the apply -> fails).
2. **`tools/diag/tune-matrix.ps1`**: reads a config list (`tools/diag/tune/<stage>.json`: `[{id, set:{...}, decoder, mode, env:{...}}]`), expands
   to cell x repeat, **randomized and interleaved** order (config A,B,C,A,B,C ...), one fresh process per run, shared batch cache dir (like
   `run-matrix.ps1`), `-ColdDiskCache` support, resumable via `matrix.json` (re-run skips finished cells), fixture fingerprint guard (reuse
   `Fixture-Fingerprint.ps1`), sets/clears env vars per run. It calls `--perf-analyze` and writes one `results.csv` row per run.
3. **`tools/diag/tune-rank.ps1`**: aggregates `results.csv` -> per-config median of run medians, bootstrap 95 % CI, ratio vs the baseline config,
   constraint flags, Pareto table (speed vs peak RAM), markdown report.
4. **Resource sampler** inside the run (or sidecar): peak working set, private bytes, available RAM minimum, GC time %, page faults/s
   (`ProcessPageFaults`, `GcEventRecorder` already exist in the CLI), CPU% and `UiBusyMeter`. Verify what `--perf-analyze` already emits before adding.
5. Record per run: commit, build version, machine, power plan, AC flag, free RAM at start, fixture fingerprint, config hash.

Effort: ~1 day for items 1-3 (small, harness-only; production code untouched except reading overrides in the CLI project).

## 3. Metrics, constraints, objective

**Primary (per scenario, from the existing analyzer):** first-visual / RAM-hit P50, P95, P99 for S2 (slow Next), S3 (burst), S4 (jump); non-RAM-hit
share (rules R-PRE: <=10 % for S2, <=30 % for S3/S4); time until preload idle (S1 whole-folder warm-up); S1 first image time (cold start).
**Secondary:** peak working set, min available RAM, GC time %, page faults, frame P95 and input P95 (`rules.json` R-UI, R-THREAD, R-GC),
decode slowdown under contention (R-CONT).

**Hard constraints (a config violating any is disqualified, not "traded off"):** zero failed/blank images; no "Preload paused for memory" event
on a 32 GB idle box; peak WS <= 20 GB and min available RAM >= 4 GB (never starve the OS); GC time <= 10 %; frame P95 <= 33 ms; input P95 <= 16 ms;
visual output identical to baseline for the same decoder family (hash/size check; K9 and decoder changes also get a human look).

**Objective among valid configs:** minimize the geometric mean of (P95 ratio vs baseline) over S2, S3, S4, weights 1:1:1 (S3 burst counts double
if the user's real habit is holding the key down: decide before looking at the data). Tie-break: lower peak RAM, then fewer workers.
A winner must beat baseline by **>= 10 % and >= 2x the A/A noise** (S0) or the baseline stays (diminishing returns rule).

## 4. Method (how not to fool ourselves)

- **Environment checklist before every batch (script prints and records it):** AC power, power plan High performance, battery saver off,
  no other heavy app, Defender real-time exclusion state noted, indexer/OneDrive idle, fixture fingerprint unchanged, >= 24 GB available RAM,
  C: >= 10 GB free after caches, CPU idle < 5 % for 60 s, lid open and fan profile unchanged. Cooldown 30 s between runs so the 6-core laptop
  CPU does not throttle into later cells; log CPU clock if available and discard thermally throttled runs.
- **Interleaved + randomized order**, repeats >= 5 per cell for decisions (3 for screening). Discard the first (warm-up) run per combo as `run-matrix.ps1` does.
- **Conditions:** `warm` is the main one (the real review session). `cold-diskcache` (batch cache dir emptied) for K7/K6. `cold-app` for
  startup. `cold-os` (empty standby list, RAMMap or reboot) is NOT automatable: one single manual round at the end, user does the reboot.
- **Two fixtures:** F4 (13.2 GB, fits 50 % RAM only partly) and a **small fixture (<= 3 GB, whole-folder mode)** plus, if available, a **large
  fixture (> 20 GB, window mode)**; K4/K5 conclusions are only valid per regime. Never mix numbers across fixtures or fixture states.
- **Noise floor first (S0):** identical config x 6 interleaved runs; define noise = P95 spread; the memory note says ~5 ms.
- **Search strategy:** staged coordinate descent with a final crossed round (full factorial of K1-K9 is ~10^4 cells = months); each stage fixes
  the winners of the earlier ones, then re-checks the earlier knobs in S7 for interactions.
- **Stop rule:** a stage ends when the top two configs are within noise; a knob whose best level is the default is left alone and recorded.

## 5. Stages (each = one batch, unattended, resumable; run via `run_in_background`, bounded by a hard timeout, no marker-wait loops)

Per-run cost to be calibrated in S0 (expect 3-5 min for S2 because it presses Next 100x at 1.5 s; S3/S4 are short). Counts below use screening
repeat 3 unless stated; "cells" = configs x scenarios.

| Stage | Question | Configs | Scenarios | Repeat | Cells (approx. runs) |
|---|---|---|---|---|---|
| S0 | noise floor + calibration | default x 6 | quick (S2 quick + S3) then gate | 6 | 12 |
| S1 | decoder x mode | {Wpf,WicDirect,TurboJpeg} x {Fast,Preview} = 6 | gate (S2,S3,S4) + S1 | 3 | ~54 |
| S2 | worker count | K3 7 levels (best decoder/mode) | gate + S1 (preload throughput) | 3 | ~63 |
| S3 | preload window | fwd {8,16,32,64,128} x bwd {0,4,8,16} -> screen 8 corners/centre first, then refine around the best | gate | 3 | ~72 |
| S4 | RAM share / whole-folder | K5 5 levels, F4 and small fixture | gate + S1; log peak WS | 3 | ~60 |
| S5 | caches | source-bytes off/4/8/16 GiB; disk cache 0/2/4/8 GiB; both under warm and cold-diskcache | gate | 3 | ~80 |
| S6 | memory pressure / safety | K8 grid 3 x 3 with a balloon process holding 8/16/22 GB | S3 + S4 + a 5-minute soak of S2 | 2 | ~54 |
| S7 | crossed finalists | top-2 per stage -> at most 24 crossed configs + baseline | **full** (S1, S1b, S2, S3, S4) | 5 | ~125 |
| S8 | runtime flags (optional) | gcServer, TieredPGO, gen0size x best config | S1 + S3 + GC % | 5 | ~40 |
| S9 | confirmation | best vs baseline vs runner-up, A/B/A/B | full + S6-zoom + S7-compare + S8-move + S9-large (regression guard) | 8 | ~72 |

Order matters: S1 -> S2 -> S3 -> S4 -> S5 (each uses prior winners). S6 can run any time after S4. Total ~530 runs; at ~3 min average that is
~26 h of machine time, so schedule as 6-8 overnight batches (the PC must stay on AC, no sleep: `request_keep_awake`). If S0 shows runs are
slower, cut S3 to the 8-point screen and S5 to the 4 decisive cells before starting. Disk use: batch cache dirs <= 8 GB each, delete after each stage.

## 6. Analysis and decision

1. After each stage: `tune-rank.ps1` -> a new file under `perf/` (date-tune-S-n) (methodology, tables, constraint flags) + one bullet in PERF-STATUS.
2. After S9: one decision fragment `decisions/TUNE-DEVICE-DEFAULTS.md` + regenerate OPEN-DECISIONS (options: (A) keep defaults; (B) change
   `PerformanceOptions` defaults for everyone; (C) keep defaults, ship a per-machine "Recommended Auto" rule computed from cores/RAM/disk
   (profile `recommended-auto` already exists), (D) only record the winning `config.json` values for this PC). Recommendation will follow the data:
   expect (C)/(D) for CPU-dependent knobs (workers, window) because they depend on core count, and (B) only for a clearly dominant default.
3. Human gate: user feels the winner on a real folder (arrow-key speed, no stutter, zoom unaffected) and re-checks kinetic arrow-key panning in the
   same session (open leftover). Visual only; numbers stay with Claude.
4. Re-run S9 on the final build after any decoder/preload code change and after Windows/driver updates (stale-evidence rule).

## 7. Risks and mitigations

- Thermal/power drift -> interleave, cooldowns, A/A noise floor, discard throttled runs. Background Windows tasks -> idle check + repeat counts.
- OS file cache makes "cold" meaningless -> label conditions honestly; cold-os only manually.
- Over-fitting to F4 (portrait JPEGs, one disk) -> second/third fixture; conclusions stated per regime.
- Memory bomb configs (workers 16 x window 128) -> run S6 safety balloon before recommending anything above default; hard RAM constraint disqualifies.
- Never delete/sweep the real Recycle Bin: S8-move uses the harness fake/scratch copies only (AGENTS rule).
- Config safety: all overrides are in-memory; the real `config.json` and caches are never touched (`--cache-dir` isolation, never `-SharedAppCache`).
- Bound everything: perf batches via background run with `timeout`; tests via `--blame-hang-timeout`.

## 8. Work breakdown (who does what, cheapest model)

1. Harness PR (`--set`, `tune-matrix.ps1`, `tune-rank.ps1`, tests): sonnet agent in a worktree, lead reviews (concurrency-neutral, harness only).
2. Pilot S0 (lead runs it): calibrates run time, noise, and tells whether to trim the plan.
3. Stages S1-S9: lead launches batches (background), a haiku agent may transcribe tables into the perf docs, lead decides.
4. Decision fragment + docs sync PR; Release build refresh per `CLAUDE.local.md` after any code PR.
