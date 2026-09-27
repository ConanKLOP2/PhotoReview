# AR16 readability probe (2026-09-26)

The per-file open probe cost ~114 ms per folder open on F4 (~68 % of the 166 ms first visual). Moved to a background pass (Q-AR7 c, #107). Interleaved master vs branch,
6 runs: catalog ready 305 -> 88 ms, first present 603 -> 353 ms (Folder trace), first visual 291 -> 262 ms; S2/S3 P95 and peak WS unchanged (within noise).
AR15c (`SourceBytesCache` read on the calling thread) is not worse with the cache on (CLI flag `--source-bytes-cache`).
