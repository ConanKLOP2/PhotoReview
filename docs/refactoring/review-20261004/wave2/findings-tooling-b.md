# Findings: Benchmark.Cli and Localization.Generator (wave 2)

312 functions: OK 287, ISSUE 23, NEEDS-EVIDENCE 2, REMOVED 0 (ledger rows); 15 findings (P1 0, P2 4, P3 11). Ledger: [ledger-tooling-b.tsv](ledger-tooling-b.tsv) (the ledger rationales reference the T-B-xx IDs). Evidence (never compiled by the build): `evidence/T-B-01-forbidden-keys`, `T-B-02-flatjson-depth`, `T-B-04-raw-survey-markdown-overwrite`, `T-B-15-iodecode-corrupt-file` (`.cs.txt`). All 310 shard IDs from PR #284's ledger appear exactly once, plus 2 new IDs: `N-tooling-b-001` (Program.cs top-level statements, the synthesized Main) and `N-tooling-b-002` (the `CacheDirOverrideAppPaths` primary constructor and its initializers). Neither shard path changed between baseline `5d291076` and master `d24b830d`. No open PR touched these files (#284, #294, #296, #299, #300). Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files.

Reproduced: T-B-02 (scratch console project run outside the repo) and T-B-07 (byte inspection). Everything else was traced by reading. The benchmark CLI, any UI and any test were not run.

Safety facts verified: benchmark file actions run on `%TEMP%` copies with a temp deleter unless `PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN=1`; `--perf-session` redirects data and config to a marker-guarded `<outDir>\data` and only deletes marker dirs; `--perf-session` re-verifies every action target before each action key; the generator output is deterministic and its escaping of C# literals is sound.

## T-B-01 (P2, traced) PerfSession forbidden-key list misses Move-to and Copy-to
`tools/PhotoReview.Benchmark.Cli/PerfSession.cs:659-668` (`CollectForbiddenKeys`), used at 338-345, 389, 469; `usesCopy` at 191. The set covers Escape, System, SendToRecycleBin, Undo, NextFolder, PreviousFolder, Fullscreen, MoveToFolder2 and configured actions. `ShortcutMappings` also has `MoveToFolder` ("M"), `CopyToFolder` ("Y") and `OpenFolder`. Per `ShortcutRouter` they open the folder picker (`FileActionController.cs:684`) and then move or copy the current photo.
- Failure: scenario `{"steps":[{"open":"folder"},{"key":"M"}]}` has no copy and no action, so `usesCopy` is false and the key runs on the real source folder. `LoadScenario` and the pre-check accept it. The picker opens on the user's desktop; answering it moves a real photo.
- Remedy: derive the set from every file-, folder- or dialog-mapped shortcut, or allow only navigation and zoom keys on a real folder. Add a reflection test over `ShortcutMappings`.
- Rows: F15559, F15545, F15586.

## T-B-02 (P3, reproduced) FlatJsonReader depth cap is looser than System.Text.Json
`src/PhotoReview.Localization.Generator/FlatJsonReader.cs:64,133-135` (`SkipValue`, `MaxDepth` 64, root counted as 0). A `_meta` value nested 64 or 65 arrays deep is accepted at build time, but the runtime `JsonDocument.Parse` rejects it. Repro: array depth 64 gives STJ reject and generator accept; depth 65 the same; depths 63 and 66 agree. `Deep_And_Huge` only tests depth 5000.
- Remedy: start nested values at depth 1 and reject at `depth >= MaxDepth`; add boundary cases 62..66 to the differential test. Row: F02791.

## T-B-03 (P3, traced) Output directories are not checked against the photo folder
`Program.cs:16-17`, `DecoderBenchmark.cs:117,124`, `IoDecodeSplit.cs:56-58,78-81`, `RawDecoderBenchmark.cs:67,84`. Only perf-session validates (`ValidatePaths`). `summary.json/md`, `details.csv`, `raw.csv`, `raw-decoder-bench.*` and the per-profile JSON can be written into the photo folder when the output path equals or sits inside it; a same-named user file would be overwritten.
- Remedy: one shared output-directory guard (equal, inside or ancestor of the photo folder, with reparse-point resolution, see T-B-13). Rows: F15452, F15490, F15590, F15610 (F15438 notes it).

## T-B-04 (P2, traced) `--raw-survey --markdown` overwrites any existing file
`RawSurvey.cs:126-131` (`RunAsync`); parser at 62-91. The report goes to `File.WriteAllTextAsync` with no extension, exists or in-folder check.
- Failure: `--markdown D:\RAW\IMG_0001.CR2` replaces a RAW with the report.
- Remedy: require `.md`, refuse an existing file unless `--force`, refuse paths inside the surveyed folder. Row: F15635 (F15633 notes the parser accepts it).

## T-B-05 (P2, traced) `--cache-dir` is unvalidated and its folders are pruned or cleaned
`CacheDirOverrideAppPaths.cs:309-310`, `LocalUiNextProbe.cs:24`, `PerfSession.cs:936` (`ParseArgs`), `837-850` (`ValidatePaths` has no cache-dir parameter). Caches become `<cacheDir>\cache` and `<cacheDir>\thumbnails`. `PreviewImageService.CleanupLegacyCacheFiles` (`PreviewImageService.cs:236-246`) deletes every `*.png` and `*.png.meta` in the cache dir at start-up (up to 5000). `DiskCacheStore.PruneDirectory` LRU-deletes `*.png` in the thumbnails dir, and stale `*.tmp` files are removed.
- Failure: `--cache-dir D:\Photos` where `D:\Photos\cache\` holds exported PNGs deletes them at start. Without the flag the harness knowingly shares and prunes the real app cache; that is documented.
- Remedy: validate in `ParseArgs`/`ValidatePaths`: not inside the source, not overlapping outDir or the app data folder, must be empty or carry a tool marker. Rows: N-tooling-b-002, F15535, F15584, F15578, F15545.

## T-B-06 (P3, traced) Unhandled exceptions and exit codes other than 2 in several modes
`Program.cs:95-101` (catch filter lacks IOException, ArgumentException, NotSupportedException, UnauthorizedAccessException for output paths), `103-107` (`--ui-next-probe`), `115-127` (`--preload-bench`: only the worker parse is wrapped), `195-203` (`--explorer-probe`: missing folder); `LocalImageBenchmark.cs:13-15`, `LocalUiNextProbe.cs:76`. A stack trace and the CLR failure exit code appear instead of exit 2. `--benchmark-all <folder> <out> extra` silently ignores `extra`. `PerfSession.RunAsync` creates outDir and temp dirs before its `try`.
- Remedy: wrap mode dispatch once (print `PhotoReview.Benchmark.Cli: message`, exit 2) and reject surplus arguments. Rows: N-tooling-b-001, F15529, F15535, F15542, F15590.

## T-B-07 (P3, reproduced by byte inspection) Garbled title in every decoder-benchmark summary.md
`DecoderBenchmark.cs:398`; the em dash is double-encoded (bytes C3 A2 E2 82 AC E2 80 9D).
- Remedy: use a plain "-" or a correct U+2014 and assert the first line in `BenchmarkReportFormattingTests`. Row: F15473.

## T-B-08 (P3, traced) DecoderBenchmark option handling
`DecoderBenchmark.cs:82-84,124-126,199`. (a) Backend names are not de-duplicated, so `Wpf,Wpf` doubles the runs in one group; numeric enum names like `1` or `99` pass `Enum.TryParse`. (b) `Directory.CreateDirectory(outDir)` runs before `ParseOptions`, leaving an empty dir on a bad width. (c) `totalDecodes` is an unchecked int product and overflows for large `--iterations`; `ParsePositiveInt` has no upper bound.
- Remedy: Distinct plus `Enum.IsDefined`, parse options first, compute `totalDecodes` as long or cap iterations. Rows: F15448, F15452 (F15442 notes the missing bound).

## T-B-09 (P3, NEEDS-EVIDENCE, not reproduced) RawSurvey chunk loop assumes full reads
`RawSurvey.cs:374-409` (`ScanEmbeddedJpegs`). One `Read` per 1 MiB+64 KiB window and `streamPos` always advances 1 MiB. A short read (possible on network shares) would silently skip embedded JPEGs.
- Remedy: loop until the buffer is full or EOF and advance by bytes consumed. Row: F15646.

## T-B-10 (P3, traced) RawSurvey JPEG parser details
`RawSurvey.cs:432,436` (`orientation` is always 1; unused `BinaryReader`), `503-508` and `529-553` (`ScanForEoi` returns `stream.Position` when no EOI exists). The doc promises EXIF orientation but `EmbeddedJpeg.Orientation` is always 1. A preview without EOI is reported valid with length up to EOF and sets `lastFoundEnd` to EOF, hiding later previews of that file.
- Remedy: parse orientation or drop the field; return -1 and reject the candidate; remove the dead reader. Rows: F15648, F15649.

## T-B-11 (P3, traced) CLI strictness is inconsistent
`PerfSession.cs:906-956`, `BenchmarkCliArguments.cs:272-282`. `--mode` accepts undefined numeric enums while `--decoder` checks `IsDefined`. `--repeat` and `--slow-link-latency-ms` use culture-sensitive `int.TryParse`, unlike the invariant `NumberStyles.None` parsers elsewhere. `--slow-link-bandwidth-mbps Infinity` passes `> 0`. Repeated options and a repeated `--rules` silently last-win, whereas RawSurvey rejects repeats.
- Remedy: shared invariant helpers, `IsDefined` for every enum, reject repeats and non-finite doubles. Rows: F15584, F15441.

## T-B-12 (P3, traced) WpfTestHost timeout does not stop the body
`WpfTestHost.cs:74-81`; callers `PerfSession.cs:196-236` and `LocalUiNextProbe.cs:47-141`. `WaitAsync(timeout)` throws but the STA body keeps running while the callers' `finally` already deletes dataRoot, copyRoot and the temp root. `thread.Join(10 s)` also blocks the awaiter synchronously. Impact is limited to tool-owned temp folders.
- Remedy: pass a `CancellationToken` into the body and wait for the thread before cleanup. Rows: F15685, F15542.

## T-B-13 (P3, traced) Path containment ignores junctions and symlinks
`PerfSession.cs:837-850` (`ValidatePaths`), `873-878` (`IsUnder`). Lexical `GetFullPath` prefix test only. An outDir that is a junction into the source passes, so `data`, `copy` and reports land in the photo folder. The publish guard (R03, #292) already resolves ancestor junctions.
- Remedy: resolve reparse points before comparing and share the helper with T-B-03. Rows: F15578, F15581.

## T-B-14 (P3, NEEDS-EVIDENCE, not proven) XmlEscape lets XML non-characters into the generated doc comment
`TrGenerator.cs:267-286`. U+FFFE and U+FFFF pass into `/// <summary>English: "..."</summary>`. `FlatJsonReader` accepts them (scratch run: `\ufffe` value accepted, length 3). Whether the compiler raises CS1570 (and fails a warnings-as-errors build) was not tested.
- Remedy: map non-XML code points to a space and add one such character to `AwkwardText_StillCompiles`. Row: F02822.

## T-B-15 (P2, traced) `--io-decode-split` aborts the whole run on one bad image
`IoDecodeSplit.cs:68-76` (loop without catch), `86-172` (`MeasureFile`), `Program.cs:164-169` (catches only DirectoryNotFound and InvalidOperation). A truncated or undecodable image throws NotSupportedException or FileFormatException from `BitmapDecoder.Create` or `BitmapImage.EndInit`. `raw.csv` and `summary.md` are written only after the loop, so up to 60 files of measurement are lost and the process ends with an unhandled exception. DecoderBenchmark, RawDecoderBenchmark and RawSurvey were already hardened against this.
- Remedy: catch per-file failures with `IsMeasurementFailure`, add error rows, write reports in a `finally`, exit non-zero if any file failed. Rows: F15490, F15493, F15497.

## Test coverage notes
Well covered: `BenchmarkCliArgumentsTests`, `BenchmarkReportFormattingTests`, `BenchmarkTrustTests`, `RawBenchmarkToolTests`, `LocalizationGeneratorTests` and `FlatJsonReaderDifferentialTests` (4x20000 fuzz). No test found for: `PerfSession.LoadScenario`, `ValidatePaths`/`IsUnder`, `CollectForbiddenKeys`, `MetricsEquivalent`'s field list, `WpfTestHost`, `UiBusyMeter`, `LocalImageBenchmark`, `LocalUiNextProbe`, `CacheDirOverrideAppPaths`, and the generator's incremental-cache behaviour.
