# Device-tuning stage files

Each `*.json` here is a pure JSON array of configs (`id`, optional `decoder`, `mode`, `set`, `env`) for `tools\diag\tune-matrix.ps1`
(no comments are possible in JSON, so the documentation lives in this file). Plan: `docs/refactoring/perf/PLAN-device-config-bench.md`.
The config with id `default` is the shipped default of that stage: it is the baseline and the A/A noise group of `tune-rank.ps1`.
Stage files whose default is not called `default` (`s2-workers.json`: `w8`) need `-Baseline w8 -NoiseConfig w8` when ranking.

```powershell
# every stage: preview, run (background, hard timeout, PC on AC, no sleep), rank
.\tools\diag\tune-matrix.ps1 -Stage s3-window -Profile gate -FixtureAlias F4 -Repeat 3 -DryRun
.\tools\diag\tune-matrix.ps1 -Stage s3-window -Profile gate -FixtureAlias F4 -Repeat 3 -SkipBuild        # prints the batch dir
.\tools\diag\tune-rank.ps1   -BatchDir <batch dir> -Baseline default                                     # report.md + results.csv
```

| File | Question | Configs | Notes |
|---|---|---|---|
| `s0-noise.json` | noise floor | `default` x6 | A/A group |
| `s1-decoder-mode.json` | decoder x mode | Wpf/WicDirect/TurboJpeg x Fast/Preview | `Original` is a quality choice, never ranked on speed |
| `s2-workers.json` | worker count | `PreloadWorkerCount` 2..16 | baseline `w8` |
| `s3-window.json` | preload window screening | 8 points of fwd {8,16,32,64,128} x bwd {0,4,8,16}: corners, centre and the default (32/8) | then refine |
| `s3-window-refine.json` | refine template | 3x3 grid fwd {24,32,48} x bwd {4,8,12} around the default | edit the grid around the screening winner, keep one `default` entry |
| `s4-ram.json` | RAM share | `ImageCacheRamPercent` 25,35,50(default),65,75 | run on F4 (large), `F-mid` and `F-small`: whole-folder vs window mode differ |
| `s5-caches.json` | caches | source-bytes cache off/4/8/16 GiB; disk cache 0/2/8 GiB (4 GiB = default) | run warm and with `-ColdDiskCache` |
| `s6-pressure.json` | memory-safety knobs | `PreloadMemoryLoadLimit` 0.85/0.90/0.95 x `MemoryReserveBytes` 1/2/4 GiB | needs `MemBalloon.ps1`, see below |
| `s8-runtime.json` | runtime flags | env `DOTNET_gcServer`, `DOTNET_TieredPGO`, `DOTNET_GCgen0size` | see below |

## Fixtures

`F4` (`C:\Xiuren\[[WALLPAPER]`, 2743 files, ~26.6 GiB) is the "large" regime (> 16 GiB RAM budget, window mode). The smaller regimes are NTFS hard-link
subsets of F4 on the same volume (zero extra disk space): `tools\diag\make-subset-fixture.ps1` (`F-small` ~2.5 GiB, `F-mid` ~10 GiB under `C:\Xiuren\_tune`). Hard links
share data with F4: the bench only reads them, never write through them. Aliases live in `work\diag\fixtures.local.json` (git-ignored); `-FixtureAlias F-small` selects one.
Never compare numbers across fixtures.

## Resource sampler

`tune-matrix.ps1` passes `--resource-sample` to every run: `resources.csv` in the run dir (500 ms: available physical RAM, working set, peak WS, private bytes,
GC pause %, page faults/s, CPU %). `tune-rank.ps1` takes min available RAM / peak WS from it (`-NoResourceSample` on the matrix turns it off).

## S6 memory pressure

`tools\diag\MemBalloon.ps1 -SizeGb N` commits and touches N GB of private memory and holds it until a stop file appears or its timeout passes; it refuses more than
(available RAM - 3 GB). Scenarios: S3 burst + S4 jump (`-Scenario s3-next-burst,s4-jump`) and a soak of `s2-next-slow`, with the balloon at 8, 16 and 22 GB.
For each size: start the balloon, wait for the ready file, run the stage, release, cool down 2 minutes (22 GB leaves ~7 GB of 32 GB: close other apps first).

```powershell
$stop = "$env:TEMP\balloon.stop"; $ready = "$env:TEMP\balloon.ready"; Remove-Item $stop, $ready -ErrorAction SilentlyContinue
$b = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-File','tools\diag\MemBalloon.ps1','-SizeGb','16','-StopFile',$stop,'-ReadyFile',$ready,'-TimeoutMinutes','180'
for ($i = 0; $i -lt 120 -and -not (Test-Path $ready); $i++) { Start-Sleep 1 }          # bounded wait
.\tools\diag\tune-matrix.ps1 -Stage s6-pressure -Scenario s3-next-burst,s4-jump -FixtureAlias F4 -Repeat 2 -SkipBuild -BatchDir <OutRoot>\tune-s6-16g
New-Item $stop -ItemType File | Out-Null; $b | Wait-Process -Timeout 60
```

Use one batch dir per balloon size and rank each separately (a config's min available RAM depends on the balloon). With a balloon the checklist warning "free RAM < 24 GB" is expected
(it is only a warning). The soak is the same stage with `-Scenario s2-next-slow -Repeat 5` (about 5 minutes of keys per config).

## S8 runtime flags: which env vars apply

The benchmark process is `PhotoReview.Benchmark.Cli.exe` hosting the app graph in-process; the shipped app has no `runtimeconfig` overrides (no `ServerGarbageCollector`, `TieredPGO`,
`TieredCompilation` in any csproj/props, `PublishReadyToRun` only at publish). So `DOTNET_*` variables set per child process by `tune-matrix` are the only source and take effect:
`DOTNET_gcServer` (0/1) and `DOTNET_TieredPGO` (0/1; default on in .NET 10) are documented runtime knobs. `DOTNET_GCgen0size` is a hexadecimal byte count (`4000000` = 64 MB) and is honoured by
the runtime GC but undocumented, so verify it per run: every `session.json` records `runtime.serverGc`, `runtime.gcLatencyMode` and the `DOTNET_*` values seen, and `process.json` the gen0/1/2 GC counts.
Caveats: `gcServer=1` needs the RAM headroom check (server GC grows the heap per core); the benchmark host is not the published ReadyToRun build, so JIT-related flags (PGO) matter more here than in a published app;
a result is only transferable to the real app if it is then set via `runtimeconfig.template.json`/csproj (not done: no product change in this tooling PR).
