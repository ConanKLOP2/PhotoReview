Summary (tooling-a, 306 functions): OK 291, ISSUE 12, NEEDS-EVIDENCE 3, REMOVED 0 (ledger rows; the rows are the affected functions of 10 findings). No P1; one P2 (PA-01); nine P3. Source: the review agent wrote this file in its scratchpad because its Write tool refused report files; the lead copied it here and corrected the severity count (the original said fourteen P3).

# Findings: Benchmarking and PerfAnalysis (wave2 tooling-a)

Reviewed on master d24b830d (shard src/PhotoReview.Benchmarking/, src/PhotoReview.PerfAnalysis/). All function rows are in ledger-tooling-a.tsv. Method: read every body plus callers and tests; nothing was compiled or run. "Reproduced" means a deterministic repro sketch exists under evidence/*.cs.txt (never compiled); everything else is traced by reading.

File safety result (no finding): every write Benchmarking makes goes to its own %TEMP% paths (Guid-named scratch cache dir under PhotoReview-Benchmark-Cache, PhotoReview-Benchmark-Action-<guid>.bin[.moved|.copy]). Source photos are only read. BenchmarkRecycleBin.TempCopyDeleter calls File.Delete but its only caller passes the scratch copy. PerfAnalysis reads perf-*.csv/session.json/process.json and writes only summary.md/summary.json into the caller's runDir. CA1305: all machine output (log lines, Markdown, JSON) is invariant.

Open PRs checked (gh pr list): #300, #299, #296, #294, #284 touch none of these files; none of the findings is fixed on master.

## PA-01 (P2) t_decode is reduced by t_read although the Decode event no longer contains the read
- Where: src/PhotoReview.PerfAnalysis/PerfAnalyzeNav.cs:347 (rec.TDecodeMs = Math.Max(0, decodeMs - readMs) in BuildNavRecord); emitter src/PhotoReview.Imaging/Caching/PreviewImageService.cs:686-702.
- Wrong: the comment says Decode "also covers the in-memory read that SourceRead reports" under PHOTOREVIEW_DIAG_PREREAD. On master DecodeFromSource logs SourceRead first and only then sets perfT0 (line 700) and logs Decode, so Decode measures the decoder call alone. The subtraction removes read time from a value that never included it.
- Scenario: PHOTOREVIEW_DIAG_PREREAD=1, SourceRead 30 ms, Decode 40 ms -> t_decode reported 10 ms (true 40). R-DEC share, R-DISK (avg(t_read + t_decode)), R-CONT decode averages and the slowest-decile t_decode share are biased low; Decode shorter than its read is floored to 0.
- Reproduced: sketch evidence/PA-01.cs.txt (expected 40, actual 10). No existing test covers the subtraction.
- Remedy: delete the subtraction (keep TReadMs and TDecodeMs independent), fix the comment, add the sketch as a test.

## BM-01 (P3) BenchmarkMetrics.Since over-reports TopSourceOpens for paths outside the baseline top 10
- Where: src/PhotoReview.Benchmarking/BenchmarkMetrics.cs:41-45.
- Wrong: TopSourceOpens lists only 10 paths; a path opened during warm-up but not in the baseline top 10 keeps its full cumulative count in the measured-iterations report (doc comment admits it). SourceOpenCount stays exact.
- Scenario: 12 warm-up opens of distinct files, then one more open of an unlisted file: reported count 2, measured opens 1.
- Reproduced: sketch evidence/BM-01.cs.txt.
- Remedy: state in the report that TopSourceOpens may include warm-up opens, or expose an unbounded per-path map from ReviewMetrics.

## BW-01 (P3) Empty file list behaves inconsistently in the public runner API
- Where: src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:53 (PrepareIterationAsync), :226 (SelectParallelIndices), :232 (SelectFile), :235 (SelectIndex). Ledger F01286, F01298, F01300.
- Wrong: with files.Length == 0 the parallel workloads (Sequential, Random, Correctness) return Correct == true after decoding nothing (results.All of an empty array); FirstFrame/Preload/WarmNext throw IndexOutOfRange/DivideByZero.
- Scenario: a new caller or test that does not pre-check gets a vacuous PASS for some profiles. Today BenchmarkWindow and the CLI (Program.cs:28) both guard, so nothing user-visible.
- Reproduced: sketch evidence/BW-01.cs.txt.
- Remedy: throw ArgumentException for empty files at the top of PrepareIterationAsync and RunProfileAsync.

## BW-02 (P3, NEEDS-EVIDENCE) WarmNext workload alternates between folder start and end
- Where: src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:239 (SelectIndex WarmNext arm). Ledger F01301.
- Suspected: iterations map to 0, n-1, 1, n-2, ...; consecutive navigation centres jump across the folder and each WarmPreloadAround cancels the previous pass, while the profile names (fast-balanced, rapid-key-press, ...) describe sequential next-image navigation. In folders larger than twice the preload window the next image is almost never the preloaded one, so PreloadHits/timings may not measure what the profile claims.
- Not proven: alternation may be intentional (forward + backward) and the hit rate was not measured. BenchmarkWorkloadRunnerTests.WarmNextWorkloadRecordsPreloadHitAfterWarming drives the executor by hand; BenchmarkSelectionGoldenTests covers Random only.
- Remedy: confirm intent; use iteration % fileCount if sequential, else document and add a golden test.

## BW-03 (P3, NEEDS-EVIDENCE) Preload/WarmNext samples include the preload kick
- Where: src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:91-96 (executor.WarmPreloadAround(center) at line 94 inside the timed measure lambda). Ledger F01288.
- Suspected: the synchronous part of PreloadScheduler.PreloadAroundAsync (pace record, snapshot, CTS swap, task start) is added to every Preload/WarmNext sample; production kicks it after the frame is presented. Probably microseconds but systematic and only for these two workloads. Magnitude not measured.
- Remedy: kick preload after elapsed is taken, or document.

## BW-04 (P3, NEEDS-EVIDENCE) File-action profiles decode a .bin copy, so RAW sources never really decode
- Where: src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:118 (scratch name *.bin), :191-196 (swallowed decode failures). Ledger F01295.
- Suspected: FormatRoutingDecoder routes by extension; a RAW photo copied to *.bin goes to the standard decoder, which throws NotSupported/FileFormat, swallowed by design. Correct is then decided by File.Exists only, so action-move/delete/copy can PASS on a RAW folder without a successful decode. JPEG/PNG are unaffected (content sniffing).
- Not proven: the benchmark executor may reject RAW anyway; not traced to the end.
- Remedy: keep the source extension on the scratch copy, or record whether the decode ran.

## PA-02 (P3) LowSampleWarning counts incomplete navigations
- Where: src/PhotoReview.PerfAnalysis/PerfAnalyzeStats.cs:160 (Count < 20). Ledger F02988.
- Wrong: Count includes incomplete navs but percentiles use complete navs only. 25 navs, 15 incomplete: no N<20 marker though P95 is the max of 10 samples.
- Scenario: burst scenarios (S3/S4) where most navs are superseded before rendering.
- Reproduced: sketch evidence/PA-02.cs.txt.
- Remedy: use Count - Incomplete.

## PA-03 (P3, NEEDS-EVIDENCE) R-THREAD triggers on a single long dispatcher op
- Where: src/PhotoReview.PerfAnalysis/PerfAnalyzeRules.cs:150-159 (byDispatcher = dispatcherLongOpCount > 0). Ledger F02956.
- Suspected: doc says "repeated DispatcherLongOp (>16ms) during S2/S3" but one op in any scenario marks the rule true and no count threshold exists in rules.json. D11 spec is gone from the repo, so intent could not be checked. Test RThreadTriggersOnDispatcherLongOpsOrHighInputP95AndIsNotTriggeredOtherwise uses counts 3 and 0 only.
- Remedy: add a configurable minimum count or fix the doc comment.

## PA-04 (P3) PerfAnalyze.RunAsync is synchronous behind a Task
- Where: src/PhotoReview.PerfAnalysis/PerfAnalyze.cs:40. Ledger F02839.
- Wrong: not async; CSV parsing and both writes run on the caller thread and failures throw synchronously instead of a faulted Task. No impact today: the CLI (Program.cs:135) and tests await immediately.
- Remedy: make it async with Task.Run for the file work, or return Task.FromException.

## PA-05 (P3) summary.json mixes camelCase and PascalCase keys
- Where: src/PhotoReview.PerfAnalysis/PerfAnalyzeReport.cs:156-158 (anonymous types folder, startup, rules). Ledger F02924, F02926, F02927, F02928.
- Wrong: top-level and group members are explicit camelCase (csvFileCount, firstVisualMs, kindCounts) but new { f.Gen, f.T1CatalogReadyMs, ... }, new { p.Phase, p.Median, ... }, new { r.Rule, r.Triggered, ... } infer PascalCase. A case-sensitive camelCase consumer silently misses folder, startup and rules data.
- Reproduced: sketch evidence/PA-05.cs.txt (reading only).
- Remedy: name members explicitly in camelCase or set JsonNamingPolicy.CamelCase before any consumer depends on the keys.
