# Findings: Core Localization / Diagnostics / Updates (wave 2)

208 functions: 200 OK, 7 ISSUE, 1 NEEDS-EVIDENCE, 0 REMOVED. All findings are P3. Line numbers are on master `3db54da1`. None was reproduced by execution: every finding is traced by reading only. Two uncompiled draft repros are in `evidence/` (`W2CM-01-filelog-partial-batch.cs.txt`, `W2CM-02-03-languagecatalog-meta.cs.txt`). No open PR touched these files when checked (#284, #294, #296, #299, #300). Ledger: [ledger-core-misc.tsv](ledger-core-misc.tsv). Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files.

## W2CM-01 (P3) FileLog.Drain re-appends already flushed entries after a partial failure
`src/PhotoReview.Core/Diagnostics/FileLog.cs:246-266` (ledger F01548). Entries are dequeued only after `writer.Flush()`. If the batch fails part-way, the `using` disposal flushes the lines already buffered, but those entries stay queued and are appended again on the next 250 ms tick.
- Realistic case: the disk fills or the file is locked mid-batch, so lines are duplicated once it recovers.
- Exotic case: one entry whose `Exception.ToString()` throws. The entries before it are rewritten every 250 ms and everything behind it is never logged until 10000 newer writes trim the head.
- Existing tests (`UnwritableLog_EntriesAreKeptForTheNextDrain`, `Rotation_NoEntryLost`) cover total failure only.
- Remedy: format each entry into a string before touching the stream, and track how many entries were already flushed so a retry skips them.

## W2CM-02 (P3) Log forging through untrusted translation files
`FileLog.cs:177-197` `Write` (F01545) and `LanguageCatalog.cs:117,122,129` (F01933). FileLog writes the message verbatim. i18n warnings embed raw key names and `_meta.plural` text from untrusted translation files, and `LocalizationService.cs:70` logs them via `_log.Warn("i18n: " + warning)`. A key containing "\n2026-... [ERROR] ..." yields a forged log line. Impact is low because it is the user's own file. `OddMessages_WrittenVerbatim` pins the verbatim output on purpose.
- Remedy: sanitize at the untrusted boundary (the `LanguageCatalog` warnings) and keep FileLog verbatim.

## W2CM-03 (P3) Blank language names are accepted
`LanguageCatalog.ParseDocument` (`LanguageCatalog.cs:133-135`, F01933). `name ?? code` and `nativeName ?? name ?? code` fall back only when the property is absent. `"nativeName": ""` or `" "` is kept and `NativeNameDeclared` becomes true, so the language picker shows a blank row. Only the missing-name case is tested (`TryParse_NamesMissing_FallBackToNameThenCode`).
- Remedy: use `IsNullOrWhiteSpace` for the fallback; optionally trim, cap the length and strip control characters.

## W2CM-04 (P3) PerfCsvListener flush, writer fault and Dispose timeout
`src/PhotoReview.Core/Diagnostics/PerfCsvListener.cs:211-238` `WriterLoopAsync` (F01569) and `:283-302` `Dispose` (F01573).
1. Flushing happens only inside the row loop, when a row arrives more than 1 s after the last flush (`Writer_FlushesAfterOneSecond`). After the last event of a burst nothing flushes until Dispose, so a killed or hung process loses the final second or so of trace.
2. Any exception in `WriteRow` (for example disk full) silently ends the loop. Rows then pile up in the channel and are counted as dropped only once it is full, so the `# dropped=` trailer under-reports.
3. If `Wait(5 s)` times out, Dispose still writes the trailer and disposes the non-thread-safe `StreamWriter` while the writer task may be mid-write.
- Remedy: add a timed idle flush, skip the trailer and writer disposal on timeout, and count rows abandoned after a writer fault.

## W2CM-05 (P3) UpdateChecker error classification is too coarse
`src/PhotoReview.Core/Updates/UpdateChecker.cs:35-36, 51-54` `CheckAsync` (F02080). Every `HttpRequestException` (a TLS or certificate failure behind an intercepting proxy, a body that ends early) maps to Offline. Every HTTP 403 maps to RateLimited, and the gap test only asserts Failed.
- Remedy: map `HttpRequestError` values to Offline or BadResponse, and treat 403 as a rate limit only when `X-RateLimit-Remaining` is 0 or `Retry-After` is present.

## W2CM-06 (P3) PhotoReviewPerf.PathId has only 32 bits
`src/PhotoReview.Core/Diagnostics/PhotoReviewPerf.cs:43-50` (F01577). The id is 4 bytes of SHA-256. The chance of at least one collision is about 1 - exp(-n^2/2^33): roughly 25% at 50000 files and 3% at 15000. Colliding photos are merged in the PerfAnalysis output (diagnostics only, no user data).
- Remedy: widen to 8 bytes (16 hex characters) and update the analyzer and the tests that assume 8 characters.

## W2CM-07 (P3, NEEDS-EVIDENCE) Localizer.SetCurrent and a throwing CurrentChanged handler
`src/PhotoReview.Core/Localization/Localizer.cs:44-49` (F01959). The new localizer is published before the multicast `CurrentChanged` runs. A throwing handler would skip the remaining handlers (MainWindow, SettingsWindow, BenchmarkWindow, LocalizationSource) and propagate to the language-switch caller. All current handlers are trivial and unsubscribe on close, so no throwing path was found and it could not be proven.
- Remedy if confirmed: iterate `GetInvocationList()` with a per-handler try/catch.

## Areas checked and found clean (the ledger rationales name the covering tests)
FileLog rotation, queue cap and trim, disposed-handle races, culture-invariant timestamps; DiagOptions invariant parsing and Lazy snapshot; PhotoReviewPerf (all 26 EventSource `WriteEvent` signatures match their parameters); ReviewMetrics (EWMA CAS loop, bounded per-path table); LanguageCatalog and LanguageLoader (size cap by stat and by UTF-8 byte count, lone-surrogate catch); `Localizer.Create` placeholder validation; LocTemplate brace edge cases such as `{a{b}` and `{x}}`; AppVersion parsing and comparison; UpdateChecker 1 MB body cap with and without Content-Length; UpdateUrlPolicy (lookalike hosts, `/../` paths and `PhotoReviewEvil` are all rejected).

Not run: no tests, the review was read-only.
