# Findings: Core IO / Settings / Session / Instance / Caching / Abstractions (wave 2)

177 functions: OK=168, ISSUE=8, NEEDS-EVIDENCE=1, REMOVED=0 (ledger rows; the rows are the affected functions of 7 findings, all P3; no P1 or P2). The shard had 172 ledger rows plus 5 new IDs, `N-core-io-001` to `-005` (the `TryCopyNew` overloads that take a `CopyCreationProof`, and `CopyCreationProof` itself), all OK. No ledger function was removed. Ledger: [ledger-core-io.tsv](ledger-core-io.tsv). Source: report of the review agent, written into the repo by the lead because the agent's Write tool refused report files. Line numbers are on master `d24b830d`.

Already fixed on master (not findings): R05 (non-finite font size, `SettingsNormalizer.cs:205-212`, `NonFiniteAndZoomStepSettingsTests`) and the keyboard-zoom-step clamp; R01 (copy ownership now proven by `CopyCreationProof`). The agent re-checked the atomic write, the journal-compaction rename, the settings backup and the session temp-file sweep and found no data-loss problem.

Repros were run through .NET 10 file-based apps on the real `PhotoReview.Core`, writing only to the agent's own temp folders (the junction test removes its junctions first). Sources (never compiled by the build): `evidence/core-io-F01-F02-vblank-resolverealpath.cs.txt`, `evidence/core-io-F03-sessionwriter-debounce-and-save-nan.cs.txt`, `evidence/core-io-F07-migrate-uilanguage.cs.txt`. No Stryker and no test suites were run.

| ID | Ledger rows | Where | Reproduced |
|---|---|---|---|
| F01 | F01346 (`Add` F01345 only triggers it) | `Abstractions/IDisplayClock.cs:61-91` | yes |
| F02 | F01919 | `IO/PhysicalFileSystem.cs:300-320` | yes |
| F03 | F02019, F02020, F02028 | `Session/SessionWriter.cs:31-64`, `:135-146` | yes |
| F04 | F02032 | `Session/SessionWriter.cs:200-237` | no, traced only |
| F05 | F01891 (NEEDS-EVIDENCE) | `IO/NavigationStatWorker.cs:33-68` | no, traced only |
| F06 | F02050 | `Settings/SettingsStore.cs:95-121`, `:181-193` | no, traced only |
| F07 | F02061 | `Settings/SettingsStore.cs:350-369` | yes |

## F01 (P3, reproduced) VBlankEstimator.Compute baseline from too few samples
The lower quartile of the intervals is used as the one-refresh baseline. With the minimum 4 samples that is the minimum interval, so one late wake-up gives a period 40% too small (100000 ticks instead of 166666 at 60 Hz). It recovers as more samples arrive.
- Remedy: report a timing only from about 8 samples, or use the median with an outlier gate.

## F02 (P3, reproduced) ResolveRealPath(@"C:\") returns a drive-relative path
`ResolveRealPath(@"C:\")` returns `C:` (drive-relative) instead of `C:\`. `ActionDestinationPolicy` trims and re-appends a separator, so containment is still correct today. The agent also tested whether a junction chained through a second junction could escape the resolver: it cannot, because the final target resolves fully.
- Remedy: keep the trailing separator for a root path.

## F03 (P3, reproduced, latent) SessionWriter debounce is not validated
Nothing validates `debounce`. A negative value makes `Task.Delay` throw inside `RunTimerAsync`, so `_timerCts` stays set and only `Flush` or `Dispose` ever writes. A zero debounce writes the file inside `Update`, under `_gate`, on the caller's thread. Production uses the 500 ms default. The same repro file also shows `SettingsStore.Save` with a NaN font size throwing `ArgumentException` before writing and creating no `config.json`; the missing finite check belongs to the App input (R05b, outside this shard).
- Remedy: validate the argument in the constructor and catch every exception in `RunTimerAsync`.

## F04 (P3, traced only; fix in open PR #296) SessionWriter.WriteBatch save is not bounded
The bounded flush limits only the wait for the writer lock, not its own `_store.Save`. A stalled save can block the UI-thread `Flush` and `Dispose` past the 2 s budget. Already fixed in open PR #296 (not merged when the agent checked), so the verdict stays ISSUE until it lands.

## F05 (P3, NEEDS-EVIDENCE, traced only) NavigationStatWorker is a single FIFO thread
`NavigationStatWorker.RunAsync` is a single FIFO thread, and a token cancelled after the item is queued is only honoured when the item is dequeued. A stalled SMB stat would therefore block every later navigation stat. Not proven without a stalled share.
- Suggested check: a unit test that blocks the first item and asserts a cancelled second task completes.

## F06 (P3, traced only) SettingsStore.Load keeps no backup for clamped or reset values
The `.corrupt-*` backup is made only on the salvage path. Values that the normalizer merely clamps or resets, and disabled optional shortcuts, are written back over `config.json` with no backup. The original values are lost; the startup dialog names the settings but not the old values.
- Remedy: back up before any repair write-back, or show the old values.

## F07 (P3, reproduced) SettingsStore.Migrate forces UiLanguage to "vi" for ConfigVersion < 3
For `ConfigVersion < 3`, `UiLanguage` is forced to `"vi"` even when the text names another language. Both `{"UiLanguage":"en"}` and `{"ConfigVersion":2,"UiLanguage":"en"}` parse to `vi`. This mainly affects Settings > Import of a hand-written file.
- Remedy: apply the migration default only when the property is absent.

## Considered and not reported (reasons are in the ledger rationales)
`SessionStore.Load` swallowing IO errors silently, the Q-R12 `Unknown` outcome treated as delivered, `TrySalvage` resetting the whole `Actions` list for one bad element, and a few programmer-error paths such as a null element passed to `Encode`.
